using System;
using System.IO;
using System.Text.Json;
using XiaFlow.Core.Logging;

namespace XiaFlow.Core.Tasks
{
    /// <summary>JSON 文件持久化助手（原子写入，防损坏）。</summary>
    public static class JsonStore
    {
        static readonly JsonSerializerOptions _opts = new()
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static T Load<T>(string path) where T : new()
        {
            try
            {
                if (!File.Exists(path)) return new T();
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<T>(json) ?? new T();
            }
            catch (Exception ex)
            {
                Logger.Error($"读取 {path} 失败，使用默认值", ex);
                return new T();
            }
        }

        public static void Save<T>(string path, T data)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(data, _opts));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                Logger.Error($"保存 {path} 失败", ex);
            }
        }
    }
}
