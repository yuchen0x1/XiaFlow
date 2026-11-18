using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using XiaFlow.Core.Engines;
using XiaFlow.Core.Logging;
using XiaFlow.Core.Tasks;

namespace XiaFlow.Core.Integration
{
    /// <summary>
    /// Windows 右键菜单集成（HKCU 注册，无需管理员）。
    /// Win10 与 Win11 均有效；Win11 中位于“显示更多选项”经典菜单。
    /// 卸载时需调用 RemoveAll 清理（安装程序卸载脚本同步执行）。
    /// </summary>
    public static class ContextMenu
    {
        const string ShellKey = "Software\\Classes";

        public static string ExePath()
        {
            return Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, Branding.ExecutableName);
        }

        static void SetValue(string key, string name, string value)
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ShellKey + "\\" + key);
            k.SetValue(name, value);
        }

        static void RemoveKeyTree(string key)
        {
            try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(ShellKey + "\\" + key, false); }
            catch { /* 不存在 */ }
        }

        /// <summary>为文件/目录注册压缩菜单（多选 Player 模式传递全部文件）。</summary>
        public static void RegisterCompressMenu()
        {
            var exe = ExePath();
            var menus = new (string key, string label, string args)[]
            {
                ("*\\shell\\XiaFlowCompress", "添加到 XiaFlow 压缩包", $"\"{exe}\" --compress \"%1\""),
                ("*\\shell\\XiaFlowCompressZip", "压缩为 ZIP", $"\"{exe}\" --compress-zip \"%1\""),
                ("*\\shell\\XiaFlowCompressPwd", "压缩并设置密码…", $"\"{exe}\" --compress-pwd \"%1\""),
                ("Directory\\shell\\XiaFlowCompress", "添加到 XiaFlow 压缩包", $"\"{exe}\" --compress \"%1\""),
                ("Directory\\shell\\XiaFlowCompressZip", "压缩为 ZIP", $"\"{exe}\" --compress-zip \"%1\""),
                ("Directory\\shell\\XiaFlowCompressPwd", "压缩并设置密码…", $"\"{exe}\" --compress-pwd \"%1\""),
            };
            foreach (var (key, label, args) in menus)
            {
                SetValue(key + "\\command", "", args);
                SetValue(key, "MUIVerb", label);
                SetValue(key, "Icon", $"\"{exe}\"");
                SetValue(key, "MultiSelectModel", "Player");
            }
            Logger.Info("右键压缩菜单已注册");
        }

        /// <summary>为支持的压缩格式注册解压/打开菜单。</summary>
        public static void RegisterExtractMenu()
        {
            var exe = ExePath();
            var exts = FormatRegistry.Formats
                .SelectMany(f => f.Extensions)
                .Where(e => e.StartsWith('.') && !IsDocumentExt(e))
                .Distinct().ToList();

            foreach (var ext in exts)
            {
                var key = $"SystemFileAssociations\\{ext}\\shell\\XiaFlow";
                SetValue(key, "MUIVerb", "匣流 XiaFlow");
                SetValue(key, "Icon", $"\"{exe}\"");
                SetValue(key, "SubCommands", "");
                SetValue(key + "\\shell\\open", "MUIVerb", "使用匣流打开");
                SetValue(key + "\\shell\\open\\command", "", $"\"{exe}\" --open \"%1\"");
                SetValue(key + "\\shell\\extractHere", "MUIVerb", "解压到当前目录");
                SetValue(key + "\\shell\\extractHere\\command", "", $"\"{exe}\" --extract-here \"%1\"");
                SetValue(key + "\\shell\\extractFolder", "MUIVerb", "解压到独立文件夹");
                SetValue(key + "\\shell\\extractFolder\\command", "", $"\"{exe}\" --extract-to-folder \"%1\"");
                SetValue(key + "\\shell\\batch", "MUIVerb", "加入批量解压队列");
                SetValue(key + "\\shell\\batch\\command", "", $"\"{exe}\" --batch \"%1\"");
                SetValue(key + "\\shell\\test", "MUIVerb", "测试压缩包完整性");
                SetValue(key + "\\shell\\test\\command", "", $"\"{exe}\" --test \"%1\"");
            }
            Logger.Info("右键解压菜单已注册");
        }

        static bool IsDocumentExt(string ext) =>
            new[] { ".docx", ".xlsx", ".pptx", ".doc", ".xls", ".ppt", ".odt", ".epub", ".apk", ".jar", ".ipa" }
            .Contains(ext, StringComparer.OrdinalIgnoreCase);

        public static void RemoveAll()
        {
            string[] trees =
            {
                "*\\shell\\XiaFlowCompress", "*\\shell\\XiaFlowCompressZip", "*\\shell\\XiaFlowCompressPwd",
                "Directory\\shell\\XiaFlowCompress", "Directory\\shell\\XiaFlowCompressZip", "Directory\\shell\\XiaFlowCompressPwd",
            };
            foreach (var t in trees) RemoveKeyTree(t);
            foreach (var f in FormatRegistry.Formats.SelectMany(x => x.Extensions).Distinct())
                RemoveKeyTree($"SystemFileAssociations\\{f}\\shell\\XiaFlow");
            Logger.Info("右键菜单已清理");
        }

        public static bool IsRegistered()
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ShellKey + "\\*\\shell\\XiaFlowCompress");
            return k != null;
        }
    }

    /// <summary>
    /// 文件关联：注册 ProgId 并加入“打开方式”列表（Win10/11 不允许程序直接改写 UserChoice，
    /// 由用户在系统“打开方式”中确认选择 —— 诚实行为）。
    /// </summary>
    public static class FileAssociation
    {
        const string ProgId = "XiaFlow.Archive";

        public static void RegisterProgId()
        {
            var exe = ContextMenu.ExePath();
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($"Software\\Classes\\{ProgId}");
            k.SetValue("", "匣流压缩包");
            k.SetValue("FriendlyTypeName", "匣流压缩包");
            using var icon = k.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{exe}\",0");
            using var cmd = k.CreateSubKey("shell\\open\\command");
            cmd.SetValue("", $"\"{exe}\" --open \"%1\"");
        }

        /// <summary>把扩展名加入 XiaFlow 的“打开方式”候选。</summary>
        public static void AddToOpenWith(string ext)
        {
            if (!ext.StartsWith('.')) ext = "." + ext;
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($"Software\\Classes\\{ext}\\OpenWithProgids");
            k.SetValue(ProgId, string.Empty);
        }

        public static void RegisterAllSupported()
        {
            RegisterProgId();
            foreach (var ext in FormatRegistry.Formats.SelectMany(f => f.Extensions).Distinct())
                AddToOpenWith(ext);
            Logger.Info("文件关联（打开方式候选）已注册");
        }

        public static void RemoveAll()
        {
            try
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree($"Software\\Classes\\{ProgId}", false);
                foreach (var ext in FormatRegistry.Formats.SelectMany(f => f.Extensions).Distinct())
                {
                    using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($"Software\\Classes\\{ext}\\OpenWithProgids", true);
                    k?.DeleteValue(ProgId, false);
                }
            }
            catch (Exception ex) { Logger.Warn("清理文件关联失败: " + ex.Message); }
        }
    }

    /// <summary>开机启动（HKCU Run）。</summary>
    public static class AutoStart
    {
        const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

        public static void Set(bool enable)
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable) k.SetValue(Branding.AppName, $"\"{ContextMenu.ExePath()}\" --minimized");
            else k.DeleteValue(Branding.AppName, false);
        }

        public static bool IsEnabled()
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Branding.AppName) != null;
        }
    }

    /// <summary>最近打开的压缩包（历史记录）。</summary>
    public static class RecentArchives
    {
        public sealed class Record
        {
            public string Path { get; set; } = "";
            public DateTime OpenedAt { get; set; }
            public bool Pinned { get; set; }
        }

        public static List<Record> Load() => JsonStore.Load<List<Record>>(AppPaths.RecentArchivesFile);

        public static void Add(string path)
        {
            var list = Load();
            list.RemoveAll(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, new Record { Path = path, OpenedAt = DateTime.Now });
            if (list.Count > 50) list.RemoveRange(50, list.Count - 50);
            JsonStore.Save(AppPaths.RecentArchivesFile, list);
        }

        public static void TogglePin(string path)
        {
            var list = Load();
            var r = list.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
            if (r != null) { r.Pinned = !r.Pinned; JsonStore.Save(AppPaths.RecentArchivesFile, list); }
        }
    }

    /// <summary>单实例 + 命令行转发（命名管道 IPC）。</summary>
    public static class SingleInstance
    {
        public const string MutexName = "XiaFlow.SingleInstance.Mutex";
        public const string PipeName = "XiaFlow.SingleInstance.Pipe";

        public static Mutex TryAcquire() => new(false, MutexName, out var createdNew) { };

        public static void SendArgs(string[] args)
        {
            try
            {
                using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", PipeName, System.IO.Pipes.PipeDirection.Out);
                pipe.Connect(1500);
                using var w = new System.IO.StreamWriter(pipe) { AutoFlush = true };
                w.Write(string.Join('\n', args));
            }
            catch (Exception ex)
            {
                Logger.Warn("单实例参数转发失败: " + ex.Message);
            }
        }
    }
}
