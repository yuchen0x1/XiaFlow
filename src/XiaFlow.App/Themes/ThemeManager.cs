using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using XiaFlow.App.Pages;

namespace XiaFlow.App.Themes
{
    /// <summary>主题切换（深色 / 浅色 / 跟随系统）。</summary>
    public static class ThemeManager
    {
        static ResourceDictionary Load(string src)
        {
            var uri = new Uri(src, UriKind.Relative);
            return Application.Current.Resources.MergedDictionaries
                .FirstOrDefault(d => d.Source == uri) ?? new ResourceDictionary { Source = uri };
        }

        public static void Apply(Core.ThemeMode mode)
        {
            var effective = mode;
            if (effective == Core.ThemeMode.FollowSystem)
            {
                effective = IsSystemDark() ? Core.ThemeMode.Dark : Core.ThemeMode.Light;
            }
            var dict = effective == Core.ThemeMode.Dark ? "Themes/Dark.xaml" : "Themes/Light.xaml";
            var dictionaries = Application.Current.Resources.MergedDictionaries;
            var old = dictionaries.FirstOrDefault(d =>
                d.Source != null && (d.Source.OriginalString == "Themes/Dark.xaml" || d.Source.OriginalString == "Themes/Light.xaml"));
            var @new = new ResourceDictionary { Source = new Uri(dict, UriKind.Relative) };
            var idx = old != null ? dictionaries.IndexOf(old) : dictionaries.Count - 1;
            if (idx >= 0) dictionaries[idx] = @new; else dictionaries.Add(@new);
        }

        static bool IsSystemDark()
        {
            try
            {
                var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize");
                var v = key?.GetValue("AppsUseLightTheme");
                return (v is int i && i == 0);
            }
            catch { return true; }
        }
    }

    /// <summary>内置矢量图标（Geometry 字符串）。</summary>
    public static class Icons
    {
        static Geometry? Parse(string data) => string.IsNullOrEmpty(data) ? null : Geometry.Parse(data);

        public static readonly Geometry Home = Geometry.Parse("M12,3 L21,10 21,21 14,21 14,14 10,14 10,21 3,21 3,10 Z");
        public static readonly Geometry Folder = Geometry.Parse("M3,5 L9,5 11,7 21,7 21,19 3,19 Z M3,9 L21,9");
        public static readonly Geometry Compress = Geometry.Parse("M12,2 L12,9 M8.5,5.5 L12,9 15.5,5.5 M4,11 L20,11 20,21 4,21 Z M8,15 L16,15");
        public static readonly Geometry Extract = Geometry.Parse("M12,14 L12,21 M8.5,17.5 L12,21 15.5,17.5 M4,3 L20,3 20,12 4,12 Z M8,7 L16,7");
        public static readonly Geometry Layers = Geometry.Parse("M12,3 L21,8 12,13 3,8 Z M5,11.5 L12,15.5 19,11.5 M5,15 L12,19 19,15");
        public static readonly Geometry Backup = Geometry.Parse("M12,4 A8,8 0 1 1 4,12 M12,4 L8.5,7.5 M12,4 L15.5,7.5 M12,10 L12,14 15,16");
        public static readonly Geometry Restore = Geometry.Parse("M20,12 A8,8 0 1 1 12,4 M12,4 L15.5,7.5 M12,4 L8.5,7.5 M12,10 L12,14 15,16");
        public static readonly Geometry Tasks = Geometry.Parse("M4,6 L9,6 9,11 4,11 Z M4,14 L9,14 9,19 4,19 Z M12,8 L20,8 M12,16 L20,16");
        public static readonly Geometry History = Geometry.Parse("M12,4 A8,8 0 1 1 4,12 L7,12 M12,4 A8,8 0 1 1 20,12 L17,12 M12,8 L12,12.5 15.5,15");
        public static readonly Geometry Settings = Geometry.Parse("M12,8.5 A3.5,3.5 0 1 0 12,15.5 A3.5,3.5 0 1 0 12,8.5 M12,2.5 L13.2,5.2 16,4.5 17.5,7 20.2,8 19.8,10.8 21.5,12 19.8,13.2 20.2,16 17.5,17 16,19.5 13.2,18.8 12,21.5 10.8,18.8 8,19.5 6.5,17 3.8,16 4.2,13.2 2.5,12 4.2,10.8 3.8,8 6.5,7 8,4.5 10.8,5.2 Z");
        public static readonly Geometry Info = Geometry.Parse("M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 M12,8 L12,8.01 M12,11 L12,16");
        public static readonly Geometry Search = Geometry.Parse("M10.5,4 A6.5,6.5 0 1 0 10.5,17 A6.5,6.5 0 1 0 10.5,4 M15.5,15.5 L20,20");
        public static readonly Geometry File = Geometry.Parse("M6,2 L14,2 18,6 18,22 6,22 Z M14,2 L14,6 18,6");
        public static readonly Geometry Close = Geometry.Parse("M5,5 L19,19 M19,5 L5,19");
        public static readonly Geometry Minimize = Geometry.Parse("M5,12 L19,12");
        public static readonly Geometry Maximize = Geometry.Parse("M5,5 L19,5 19,19 5,19 Z");
        public static readonly Geometry Refresh = Geometry.Parse("M19,12 A7,7 0 1 1 12,5 L12,2 M12,5 L15,8 M12,5 L9,8");
        public static readonly Geometry Play = Geometry.Parse("M7,4 L19,12 7,20 Z");
        public static readonly Geometry Pause = Geometry.Parse("M7,4 L10.5,4 10.5,20 7,20 Z M13.5,4 L17,4 17,20 13.5,20 Z");
        public static readonly Geometry Stop = Geometry.Parse("M5,5 L19,5 19,19 5,19 Z");
        public static readonly Geometry Check = Geometry.Parse("M4,12.5 L9.5,18 20,6.5");
        public static readonly Geometry Warn = Geometry.Parse("M12,3 L22,20 2,20 Z M12,9 L12,14 M12,16.5 L12,17");
        public static readonly Geometry Plus = Geometry.Parse("M12,4 L12,20 M4,12 L20,12");
        public static readonly Geometry Trash = Geometry.Parse("M4,7 L20,7 M9,7 L9,4 15,4 15,7 M6,7 L7,21 17,21 18,7 M10,11 L10,17 M14,11 L14,17");
        public static readonly Geometry Shield = Geometry.Parse("M12,3 L20,6 20,12 C20,17 16.5,20 12,21.5 7.5,20 4,17 4,12 L4,6 Z M9,12 L11.5,14.5 15.5,9.5");
        public static readonly Geometry Clock = Geometry.Parse("M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 M12,7 L12,12.5 16,15");
        public static readonly Geometry Open = Geometry.Parse("M13,4 L20,4 20,11 M20,4 L11,13 M18,13 L18,20 4,20 4,6 11,6");
        public static readonly Geometry Download = Geometry.Parse("M12,3 L12,15 M7.5,10.5 L12,15 16.5,10.5 M4,19 L20,19");
        public static readonly Geometry Up = Geometry.Parse("M12,20 L12,6 M6,12 L12,6 18,12");
        public static readonly Geometry Preview = Geometry.Parse("M2,12 C5,6 9,4.5 12,4.5 C15,4.5 19,6 22,12 C19,18 15,19.5 12,19.5 C9,19.5 5,18 2,12 Z M12,9.5 A2.5,2.5 0 1 0 12,14.5 A2.5,2.5 0 1 0 12,9.5");
    }
}
