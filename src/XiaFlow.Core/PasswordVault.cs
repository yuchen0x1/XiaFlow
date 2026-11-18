using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XiaFlow.Core.Engines;
using XiaFlow.Core.Logging;

namespace XiaFlow.Core
{
    /// <summary>密码库条目。</summary>
    public sealed class PasswordEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";      // 用户可读标签
        public string Password { get; set; } = "";  // 仅内存中存在明文
        public DateTime Created { get; set; } = DateTime.Now;
        public DateTime LastUsed { get; set; }
    }

    /// <summary>
    /// 常用密码库：Windows DPAPI（CurrentUser）加密存储，绝不明文落盘、绝不写入日志。
    /// </summary>
    public sealed class PasswordVault
    {
        readonly object _lock = new();
        List<PasswordEntry> _entries = new();

        public PasswordVault()
        {
            Load();
        }

        [DllImport("crypt32.dll", SetLastError = true)]
        static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

        [StructLayout(LayoutKind.Sequential)]
        struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        static byte[] Protect(byte[] plain)
        {
            var pin = Marshal.AllocHGlobal(plain.Length);
            var blobIn = new DATA_BLOB { cbData = plain.Length, pbData = pin };
            try
            {
                Marshal.Copy(plain, 0, pin, plain.Length);
                if (!CryptProtectData(ref blobIn, "XiaFlow", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var blobOut))
                    throw new InvalidOperationException("DPAPI 加密失败");
                var arr = new byte[blobOut.cbData];
                Marshal.Copy(blobOut.pbData, arr, 0, blobOut.cbData);
                Marshal.FreeHGlobal(blobOut.pbData);
                return arr;
            }
            finally
            {
                Marshal.FreeHGlobal(pin);
            }
        }

        static byte[] Unprotect(byte[] cipher)
        {
            var pin = Marshal.AllocHGlobal(cipher.Length);
            var blobIn = new DATA_BLOB { cbData = cipher.Length, pbData = pin };
            try
            {
                Marshal.Copy(cipher, 0, pin, cipher.Length);
                if (!CryptUnprotectData(ref blobIn, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var blobOut))
                    throw new InvalidOperationException("DPAPI 解密失败");
                var arr = new byte[blobOut.cbData];
                Marshal.Copy(blobOut.pbData, arr, 0, blobOut.cbData);
                Marshal.FreeHGlobal(blobOut.pbData);
                return arr;
            }
            finally
            {
                Marshal.FreeHGlobal(pin);
            }
        }

        void Load()
        {
            lock (_lock)
            {
                _entries.Clear();
                try
                {
                    if (!File.Exists(AppPaths.PasswordVaultFile)) return;
                    var cipher = File.ReadAllBytes(AppPaths.PasswordVaultFile);
                    var plain = Unprotect(cipher);
                    _entries = JsonSerializer.Deserialize<List<PasswordEntry>>(Encoding.UTF8.GetString(plain)) ?? new();
                }
                catch (Exception ex)
                {
                    Logger.Error("密码库读取失败", ex);
                }
            }
        }

        void Save()
        {
            lock (_lock)
            {
                try
                {
                    var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_entries));
                    File.WriteAllBytes(AppPaths.PasswordVaultFile, Protect(plain));
                }
                catch (Exception ex)
                {
                    Logger.Error("密码库保存失败", ex);
                }
            }
        }

        public IReadOnlyList<PasswordEntry> GetAll() { lock (_lock) return _entries.ToList(); }

        public PasswordEntry Add(string name, string password)
        {
            var e = new PasswordEntry { Name = name, Password = password };
            lock (_lock) { _entries.Add(e); Save(); }
            return e;
        }

        public void Remove(string id)
        {
            lock (_lock) { _entries.RemoveAll(x => x.Id == id); Save(); }
        }

        public void TouchUsed(string id)
        {
            lock (_lock)
            {
                var e = _entries.FirstOrDefault(x => x.Id == id);
                if (e == null) return;
                e.LastUsed = DateTime.Now;
                Save();
            }
        }

        /// <summary>
        /// 尝试从用户保存的密码库匹配加密压缩包（逐个试密）。命中返回条目。
        /// 使用 Test（真实解密校验）而非 List —— ZIP/7z 内容加密时 List 不校验密码。
        /// </summary>
        public PasswordEntry? TryMatch(string archive, IEnumerable<PasswordEntry>? candidates = null)
        {
            try
            {
                using var engine = new SevenZipEngine();
                var list = candidates ?? GetAll();
                foreach (var c in list)
                {
                    try
                    {
                        engine.Test(archive, c.Password);
                        TouchUsed(c.Id);
                        return c;
                    }
                    catch (ArchiveException aex) when (aex.Kind == ArchiveErrorKind.WrongPassword)
                    {
                        continue;
                    }
                    catch (ArchiveException aex) when (aex.Kind == ArchiveErrorKind.NotArchive)
                    {
                        return null; // 不是压缩包，别浪费时间试密
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"密码匹配尝试异常: {ex.Message}");
            }
            return null;
        }
    }

    /// <summary>密码强度评估。</summary>
    public static class PasswordStrength
    {
        public static (int Score, string Label) Evaluate(string password)
        {
            if (string.IsNullOrEmpty(password)) return (0, "无");
            int classes = 0;
            if (password.Any(char.IsLower)) classes++;
            if (password.Any(char.IsUpper)) classes++;
            if (password.Any(char.IsDigit)) classes++;
            if (password.Any(c => !char.IsLetterOrDigit(c))) classes++;
            int score = classes * 2 - 1;                      // 1..7
            if (password.Length >= 12) score++;               // 长度奖励
            if (password.Length < 8) score -= 2;              // 短密码惩罚
            score = Math.Clamp(score, 0, 5);
            return (score, score switch
            {
                0 or 1 or 2 => "弱",
                3 => "中",
                4 => "强",
                _ => "极强"
            });
        }
    }
}
