using System.Runtime.InteropServices;

namespace Apex.Services;

/// Apex's icon in the notification area (by the clock): the Windows
/// counterpart of the Mac app's menu bar item. Click: the panel. Right-click:
/// Open Apex, Quit. The tooltip shows CPU and memory.
internal sealed class Tray : IDisposable
{
    public event Action? Clicked, OpenRequested, QuitRequested;
    readonly MessageWindow window = new("Tray");
    readonly IntPtr icon;
    readonly uint taskbarCreated;
    const uint CallbackMessage = 0x8000 + 1;   // WM_APP + 1
    const uint WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, WM_CONTEXTMENU = 0x7B, NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public uint cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadImage(IntPtr inst, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_SHOWTIP = 0x80;

    string tip = "Apex Activity Manager";

    public Tray()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Apex.ico");
        icon = LoadImage(IntPtr.Zero, path, 1 /* IMAGE_ICON */, 16, 16, 0x10 /* LR_LOADFROMFILE */);
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        window.Message += OnMessage;
        Add();
    }

    NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = window.Handle, uID = 1, uFlags = flags,
        uCallbackMessage = CallbackMessage, hIcon = icon, szTip = tip, szInfo = "", szInfoTitle = "", uVersion = 4,
    };

    void Add()
    {
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_ADD, ref d);
        Shell_NotifyIcon(NIM_SETVERSION, ref d);      // version 4: clicks arrive as NIN_SELECT / WM_CONTEXTMENU
    }

    /// A Windows notification from the tray icon (Apex's alerts).
    public void Notify(string title, string text, bool warning)
    {
        var d = Data(0x10 /* NIF_INFO */);
        d.szInfoTitle = title.Length > 63 ? title[..63] : title;
        d.szInfo = text.Length > 255 ? text[..252] + "…" : text;
        d.dwInfoFlags = warning ? 2u /* NIIF_WARNING */ : 1u /* NIIF_INFO */;
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    /// The hover text (at most 127 characters).
    public void SetTip(string text)
    {
        tip = text.Length > 127 ? text[..127] : text;
        var d = Data(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    bool OnMessage(uint msg, IntPtr w, IntPtr l)
    {
        if (msg == taskbarCreated) { Add(); return false; }     // Explorer restarted
        if (msg != CallbackMessage) return false;
        uint evt = (uint)(l.ToInt64() & 0xFFFF);
        switch (evt)
        {
            case NIN_SELECT or NIN_KEYSELECT or WM_LBUTTONUP: Clicked?.Invoke(); break;
            case WM_CONTEXTMENU or WM_RBUTTONUP: ShowMenu(); break;
        }
        return true;
    }

    void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, (UIntPtr)1, "Open Apex");
        AppendMenu(menu, 0x800 /* MF_SEPARATOR */, UIntPtr.Zero, null);
        AppendMenu(menu, 0, (UIntPtr)2, "Quit Apex");
        GetCursorPos(out var p);
        SetForegroundWindow(window.Handle);       // so the menu closes when clicking elsewhere
        int chosen = TrackPopupMenuEx(menu, 0x100 /* TPM_RETURNCMD */ | 0x20 /* TPM_BOTTOMALIGN */, p.X, p.Y, window.Handle, IntPtr.Zero);
        DestroyMenu(menu);
        if (chosen == 1) OpenRequested?.Invoke();
        else if (chosen == 2) QuitRequested?.Invoke();
    }

    public static POINT Cursor() { GetCursorPos(out var p); return p; }

    public void Dispose()
    {
        var d = Data(0);
        Shell_NotifyIcon(NIM_DELETE, ref d);
        if (icon != IntPtr.Zero) DestroyIcon(icon);
        window.Dispose();
    }
}
