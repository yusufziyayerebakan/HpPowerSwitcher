using System.Runtime.InteropServices;

namespace HpPowerSwitcher.Services;

/// <summary>Shell notification icon and native menu; no WinForms message loop or controls.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 81;
    private readonly IntPtr _window;
    private readonly SubclassProc _procedure;
    private readonly Action _show, _disableBloat, _exit;
    private readonly Action<HpThermalMode> _setMode;
    private readonly uint _taskbarCreated;
    private NotifyIconData _data;
    private bool _disposed;

    public TrayIcon(IntPtr window, string iconPath, Action show, Action<HpThermalMode> setMode, Action disableBloat, Action exit)
    {
        _window = window;
        _show = show; _setMode = setMode; _disableBloat = disableBloat; _exit = exit;
        _procedure = WindowMessage;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        var icon = LoadImage(IntPtr.Zero, iconPath, 1, 16, 16, 0x10);
        _data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = 1,
            Flags = 1 | 2 | 4, Callback = CallbackMessage, Icon = icon,
            Tip = "HP Power Switcher", Info = "", InfoTitle = ""
        };
        if (!SetWindowSubclass(window, _procedure, 1, 0))
        { if (icon != IntPtr.Zero) DestroyIcon(icon); throw new InvalidOperationException("Could not attach tray callback."); }
        if (!Shell_NotifyIcon(0, ref _data))
        { Dispose(); throw new InvalidOperationException("Could not create notification icon."); }
    }

    private IntPtr WindowMessage(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        // Exceptions must never cross an unmanaged window-procedure boundary.
        try
        {
            if (message == _taskbarCreated) Shell_NotifyIcon(0, ref _data);
            if (message == CallbackMessage)
            {
                if (lParam.ToInt64() == 0x203) _show(); // WM_LBUTTONDBLCLK
                if (lParam.ToInt64() == 0x205) ShowMenu(); // WM_RBUTTONUP
                return IntPtr.Zero;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, 0, 1, "Open Power Switcher");
            AppendMenu(menu, 0x800, 0, null);
            HpThermalMode[] modes = [HpThermalMode.SmartSense, HpThermalMode.Balanced, HpThermalMode.Performance,
                HpThermalMode.Cool, HpThermalMode.Quiet, HpThermalMode.PowerSaver];
            for (int i = 0; i < modes.Length; i++) AppendMenu(menu, 0, (uint)(10 + i), modes[i].ToString());
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, 2, "Stop telemetry & bloat");
            AppendMenu(menu, 0, 3, "Exit");
            GetCursorPos(out var point);
            SetForegroundWindow(_window);
            uint command = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, _window, IntPtr.Zero);
            PostMessage(_window, 0, UIntPtr.Zero, IntPtr.Zero);
            if (command == 1) _show();
            else if (command == 2) _disableBloat();
            else if (command == 3) _exit();
            else if (command >= 10 && command < 10 + modes.Length) _setMode(modes[command - 10]);
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shell_NotifyIcon(2, ref _data);
        RemoveWindowSubclass(_window, _procedure, 1);
        if (_data.Icon != IntPtr.Zero) DestroyIcon(_data.Icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr window, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
}
