using System.Runtime.InteropServices;

namespace Apex.Services;

/// A hidden window that receives the Win32 messages WinUI doesn't surface:
/// the notification-area icon's clicks, clipboard changes, and Explorer
/// restarting (the icon has to be added again then). Never shown.
internal sealed class MessageWindow : IDisposable
{
    public IntPtr Handle { get; }
    public event Func<uint, IntPtr, IntPtr, bool>? Message;     // msg, wParam, lParam → handled
    readonly WndProc proc;       // kept alive: Windows calls it

    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

    public MessageWindow(string name)
    {
        proc = (h, m, w, l) => Message?.Invoke(m, w, l) == true ? IntPtr.Zero : DefWindowProc(h, m, w, l);
        var cls = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = GetModuleHandle(null), lpszClassName = "Apex." + name,
        };
        RegisterClassEx(ref cls);
        // A plain, never-shown popup (not message-only): it also gets Explorer's "TaskbarCreated" broadcast.
        Handle = CreateWindowEx(0, cls.lpszClassName, cls.lpszClassName, 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
    }

    public void Dispose() { if (Handle != IntPtr.Zero) DestroyWindow(Handle); }
}
