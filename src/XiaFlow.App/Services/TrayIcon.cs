using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace XiaFlow.App.Services
{
    /// <summary>
    /// Win32 Shell_NotifyIcon 托盘图标（零依赖实现）：
    /// 左键恢复窗口、右键菜单、气泡通知。
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        const int WM_TRAY = 0x8000 + 4711;   // WM_APP 自定义
        const int WM_LBUTTONUP = 0x0202;
        const int WM_RBUTTONUP = 0x0205;
        const int WM_LBUTTONDBLCLK = 0x0203;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint load);

        const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
        const uint NIIF_INFO = 1, NIIF_WARNING = 2, NIIF_ERROR = 3, NIIF_USER = 4;

        IntPtr _hwnd;
        HwndSource? _source;
        bool _added;
        readonly string _iconPath;

        public event Action? Activated;
        public event Action? MenuRequested;

        public TrayIcon(string iconPath, string tooltip)
        {
            _iconPath = iconPath;
            _source = new HwndSource(new HwndSourceParameters("XiaFlowTray")
            {
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                WindowStyle = 0,
            });
            _source.AddHook(WndProc);
            _hwnd = _source.Handle;
            Show(tooltip);
        }

        void Show(string tooltip)
        {
            var nid = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAY,
                szTip = tooltip,
                hIcon = LoadIcon(),
            };
            _added = Shell_NotifyIcon(NIM_ADD, ref nid);
            var ver = nid;
            ver.uVersion = 4;
            Shell_NotifyIcon(NIM_SETVERSION, ref ver);
        }

        IntPtr LoadIcon()
        {
            try
            {
                if (System.IO.File.Exists(_iconPath))
                    return LoadImage(IntPtr.Zero, _iconPath, 1 /*IMAGE_ICON*/, 16, 16, 0x10 /*LR_LOADFROMFILE*/);
            }
            catch { }
            return IntPtr.Zero;
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_TRAY) return IntPtr.Zero;
            var m = lParam.ToInt32();
            switch (m)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    Activated?.Invoke();
                    handled = true;
                    break;
                case WM_RBUTTONUP:
                    MenuRequested?.Invoke();
                    handled = true;
                    break;
            }
            return IntPtr.Zero;
        }

        /// <summary>显示 Windows 气泡通知。</summary>
        public void ShowBalloon(string title, string message, string level = "info")
        {
            if (!_added) return;
            var nid = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NIF_INFO,
                szInfo = message,
                szInfoTitle = title,
                dwInfoFlags = level switch { "error" => NIIF_ERROR, "warn" => NIIF_WARNING, "success" => NIIF_USER, _ => NIIF_INFO },
            };
            Shell_NotifyIcon(NIM_MODIFY, ref nid);
        }

        public void Dispose()
        {
            if (_added)
            {
                var nid = new NOTIFYICONDATA
                {
                    cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                    hWnd = _hwnd,
                    uID = 1,
                };
                Shell_NotifyIcon(NIM_DELETE, ref nid);
                _added = false;
            }
            _source?.Dispose();
            _source = null;
        }
    }
}
