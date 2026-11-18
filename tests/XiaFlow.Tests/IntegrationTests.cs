using System;
using System.IO;
using Microsoft.Win32;
using XiaFlow.Core.Integration;
using Xunit;

namespace XiaFlow.Tests
{
    /// <summary>系统集成：右键菜单注册与清理（HKCU 用户级，真实注册表操作）。</summary>
    public class IntegrationTests
    {
        [Fact]
        public void ContextMenu_Register_And_Remove()
        {
            try
            {
                ContextMenu.RegisterCompressMenu();
                Assert.True(ContextMenu.IsRegistered(), "注册后应能检测到右键菜单");

                // 校验关键注册表值
                using (var cmd = Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell\XiaFlowCompress\command"))
                {
                    Assert.NotNull(cmd);
                    var value = cmd!.GetValue("") as string;
                    Assert.NotNull(value);
                    // 命令指向当前进程 exe（测试宿主 / 生产环境为 XiaFlow.exe）+ 压缩参数
                    Assert.Contains("--compress", value);
                    Assert.Contains(".exe\"", value!);
                }
                using (var dir = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell\XiaFlowCompress\command"))
                {
                    Assert.NotNull(dir);
                }

                // 清理（真实卸载路径）
                ContextMenu.RemoveAll();
                Assert.False(ContextMenu.IsRegistered(), "清理后应检测不到右键菜单");
            }
            finally
            {
                ContextMenu.RemoveAll();
            }
        }

        [Fact]
        public void ContextMenu_Extract_Menu_Registers_Formats()
        {
            try
            {
                ContextMenu.RegisterExtractMenu();
                using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\SystemFileAssociations\.7z\shell\XiaFlow\shell\extractHere\command");
                Assert.NotNull(k);
                Assert.Contains("--extract-here", k!.GetValue("") as string);
                using var rar = Registry.CurrentUser.OpenSubKey(@"Software\Classes\SystemFileAssociations\.rar\shell\XiaFlow\shell\open\command");
                Assert.NotNull(rar); // RAR 仅解压：右键打开可用
            }
            finally
            {
                ContextMenu.RemoveAll();
            }
        }

        [Fact]
        public void FileAssociation_Registers_ProgId()
        {
            try
            {
                FileAssociation.RegisterAllSupported();
                using var progId = Registry.CurrentUser.OpenSubKey(@"Software\Classes\XiaFlow.Archive\shell\open\command");
                Assert.NotNull(progId);
                Assert.Contains("--open", progId!.GetValue("") as string);
                using var ow = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.7z\OpenWithProgids");
                Assert.NotNull(ow?.GetValue("XiaFlow.Archive"));
            }
            finally
            {
                FileAssociation.RemoveAll();
            }
        }

        [Fact]
        public void AutoStart_Set_And_Unset()
        {
            AutoStart.Set(true);
            Assert.True(AutoStart.IsEnabled());
            AutoStart.Set(false);
            Assert.False(AutoStart.IsEnabled());
        }
    }
}
