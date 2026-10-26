using System.Runtime.InteropServices;
using Apex.Core;

namespace Apex.Services;

/// The last 50 things copied (text, files, pictures), as on the Mac, for the
/// tray's Clipboard tab. Kept in memory only. Copies that ask to be kept out
/// of clipboard tools (password managers mark them) are skipped. Windows'
/// own history (Win+V) is separate; the tab links to it.
///
/// It listens with `AddClipboardFormatListener`, so it reads the clipboard
/// only when it changes.
internal sealed class ClipboardHistory
{
    public static readonly ClipboardHistory Shared = new();
    public const int Max = 50;

    public List<ClipItem> Items { get; } = new();
    public bool Enabled
    {
        get => enabled;
        set { enabled = value; Settings.Set("clipboardHistory", value); if (!value) Clear(); }
    }
    bool enabled = Settings.Get("clipboardHistory", true);
    public event Action? Changed;

    MessageWindow? window;
    bool selfCopy;      // our own "copy again" mustn't be recorded twice

    [DllImport("user32.dll", SetLastError = true)] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr h);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(IntPtr drop, uint index, System.Text.StringBuilder? file, uint size);
    const uint WM_CLIPBOARDUPDATE = 0x031D, CF_UNICODETEXT = 13, CF_HDROP = 15, CF_DIB = 8;

    // What password managers and other private copies set (an empty or 0 value).
    static readonly uint Exclude = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    static readonly uint CanIncludeInHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");

    public void Start()
    {
        if (window != null) return;
        window = new MessageWindow("Clipboard");
        window.Message += (msg, _, _) =>
        {
            if (msg != WM_CLIPBOARDUPDATE) return false;
            if (selfCopy) { selfCopy = false; return true; }
            if (enabled) Read();
            return true;
        };
        AddClipboardFormatListener(window.Handle);
    }

    public void Clear() { Items.Clear(); Changed?.Invoke(); }

    /// Marks the next clipboard change as Apex's own (copy again).
    public void WillCopy() => selfCopy = true;

    void Read()
    {
        // The clipboard can be briefly locked by the app that's writing to it.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(window!.Handle)) break;
            if (attempt == 4) return;
            Thread.Sleep(20);
        }
        ClipItem? item = null;
        try
        {
            if (IsClipboardFormatAvailable(Exclude) || CanIncludeIsZero()) return;
            if (IsClipboardFormatAvailable(CF_HDROP)) item = Files();
            else if (IsClipboardFormatAvailable(CF_UNICODETEXT)) item = Text();
            else if (IsClipboardFormatAvailable(CF_DIB)) item = Picture();
        }
        catch { }
        finally { CloseClipboard(); }
        if (item == null) return;
        Add(item);
    }

    public void Add(ClipItem item)
    {
        ClipList.Insert(Items, item, Max);
        Changed?.Invoke();
    }

    static bool CanIncludeIsZero()
    {
        if (!IsClipboardFormatAvailable(CanIncludeInHistory)) return false;
        var h = GetClipboardData(CanIncludeInHistory);
        if (h == IntPtr.Zero) return false;
        var p = GlobalLock(h);
        try { return p != IntPtr.Zero && (int)GlobalSize(h) >= 4 && Marshal.ReadInt32(p) == 0; }
        finally { GlobalUnlock(h); }
    }

    static ClipItem? Text()
    {
        var h = GetClipboardData(CF_UNICODETEXT);
        if (h == IntPtr.Zero) return null;
        var p = GlobalLock(h);
        try
        {
            var text = p == IntPtr.Zero ? null : Marshal.PtrToStringUni(p);
            return string.IsNullOrWhiteSpace(text) ? null : ClipItem.FromText(text);
        }
        finally { GlobalUnlock(h); }
    }

    static ClipItem? Files()
    {
        var h = GetClipboardData(CF_HDROP);
        if (h == IntPtr.Zero) return null;
        uint n = DragQueryFile(h, 0xFFFFFFFF, null, 0);
        var paths = new List<string>();
        for (uint i = 0; i < n; i++)
        {
            var sb = new System.Text.StringBuilder(1024);
            if (DragQueryFile(h, i, sb, (uint)sb.Capacity) > 0) paths.Add(sb.ToString());
        }
        return paths.Count == 0 ? null : ClipItem.FromFiles(paths);
    }

    /// A picture: kept as a small PNG preview plus the full bitmap (DIB) to copy back.
    static ClipItem? Picture()
    {
        var h = GetClipboardData(CF_DIB);
        if (h == IntPtr.Zero) return null;
        var p = GlobalLock(h);
        try
        {
            int size = (int)GlobalSize(h);
            if (p == IntPtr.Zero || size <= 40 || size > 64 * 1024 * 1024) return null;     // skip absurdly large ones
            var dib = new byte[size];
            Marshal.Copy(p, dib, 0, size);
            int width = BitConverter.ToInt32(dib, 4), height = Math.Abs(BitConverter.ToInt32(dib, 8));
            return ClipItem.FromPicture(dib, width, height);
        }
        finally { GlobalUnlock(h); }
    }
}
