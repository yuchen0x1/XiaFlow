using XiaFlow.Core;

namespace XiaFlow.App.Services
{
    /// <summary>供 XAML x:Static 引用的版本文本。</summary>
    public static class VersionTextLocator
    {
        public static string Version => Branding.VersionText;
    }
}
