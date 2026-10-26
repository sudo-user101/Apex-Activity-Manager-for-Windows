using System.Runtime.InteropServices;

namespace Apex.Services;

/// Keeps the PC (and optionally the screen) awake for a while or until
/// turned off, as on the Mac. `SetThreadExecutionState` on the UI thread,
/// which lives as long as Apex does; Windows drops it when Apex quits.
internal static class StayAwake
{
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    /// Until when (null: until turned off). Only meaningful while `IsOn`.
    public static DateTime? Until { get; private set; }
    public static bool IsOn { get; private set; }
    public static bool KeepDisplayOn { get; private set; }
    public static event Action? Changed;
    static Microsoft.UI.Dispatching.DispatcherQueueTimer? timer;

    /// Must be called on the UI thread.
    public static void Start(TimeSpan? duration, bool display)
    {
        IsOn = true; KeepDisplayOn = display;
        Until = duration is { } d ? DateTime.Now + d : null;
        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | (display ? ES_DISPLAY_REQUIRED : 0));
        timer ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Stop();
        if (duration is { } t) { timer.Interval = t; timer.IsRepeating = false; timer.Tick += (_, _) => Stop(); timer.Start(); }
        Changed?.Invoke();
    }

    public static void Stop()
    {
        IsOn = false; Until = null;
        timer?.Stop();
        SetThreadExecutionState(ES_CONTINUOUS);
        Changed?.Invoke();
    }
}
