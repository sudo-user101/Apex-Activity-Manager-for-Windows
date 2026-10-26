using System.Diagnostics;
using System.Security.Principal;
using Apex.Core;

namespace Apex.Services;

/// Live frame rates for every app that's drawing (games included), from
/// Windows' graphics event tracing, read by Intel's open-source PresentMon
/// (MIT; shipped in `tools`). Runs only while the Gaming page or Game mode
/// needs it. Reading these events needs administrator rights or the
/// "Performance Log Users" group; Apex says so and can add you once.
internal static class FrameMonitor
{
    public enum State { Off, Starting, Running, NeedsPermission, Missing, Failed }
    public static State Status { get; private set; } = State.Off;
    public static string Detail { get; private set; } = "";
    public static readonly FrameHistory History = new();
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static readonly object gate = new();
    static Process? process;
    static int users;
    public static double Now => clock.Elapsed.TotalSeconds;
    public static event Action? Changed;

    static string Tool => Path.Combine(AppContext.BaseDirectory, "tools", "PresentMon.exe");

    /// Starts reading frames (counted: each caller stops its own).
    public static void Acquire()
    {
        lock (gate)
        {
            users++;
            if (process != null) return;
            Begin();
        }
    }

    public static void Release()
    {
        lock (gate)
        {
            users = Math.Max(0, users - 1);
            if (users == 0) End();
        }
    }

    public static FrameStats Stats(int pid) { lock (gate) return History.Stats(pid, Now); }

    public static List<(int Pid, string App, FrameStats Stats)> Apps()
    {
        lock (gate)
        {
            History.Prune(Now);
            return History.Apps.Select(a => (a.Pid, a.App, History.Stats(a.Pid, Now))).Where(a => a.Item3.Fps > 0).OrderByDescending(a => a.Item3.Fps).ToList();
        }
    }

    static void Set(State s, string detail = "") { Status = s; Detail = detail; Changed?.Invoke(); }

    static void Begin()
    {
        if (!File.Exists(Tool)) { Set(State.Missing, "The frame-rate component isn’t installed. Reinstall Apex."); return; }
        Set(State.Starting);
        try
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo(Tool, "--output_stdout --no_console_stats --stop_existing_session --session_name ApexFrames --no_track_input")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true },
                EnableRaisingEvents = true,
            };
            var csv = new PresentMonCsv();
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                var f = csv.Parse(e.Data);
                if (csv.HasHeader && Status != State.Running) Set(State.Running);
                if (f is { } frame) lock (gate) History.Add(frame, Now);
            };
            string lastError = "";
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                if (e.Data.Trim().Length > 0) lastError = e.Data.Trim();
                if (e.Data.Contains("access denied", StringComparison.OrdinalIgnoreCase))
                    Set(State.NeedsPermission, "Windows only lets administrators, or members of “Performance Log Users”, read frame rates.");
            };
            p.Exited += (_, _) =>
            {
                lock (gate) process = null;
                if (Status is State.Running or State.Starting)
                    Set(State.Failed, "The frame-rate reader stopped" + (lastError.Length > 0 ? ": " + lastError : "."));
            };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            process = p;
            // Its output arrives in batches, and only once something draws: running for 2 s
            // without an error means the trace session is live.
            _ = Task.Delay(2000).ContinueWith(_ => { if (Status == State.Starting && process == p && !p.HasExited) Set(State.Running); });
        }
        catch (Exception e) { Set(State.Failed, e.Message); }
    }

    static void End()
    {
        try { if (process is { HasExited: false } p) p.Kill(); } catch { }
        process = null;
        Set(State.Off);
    }

    public static void StopAll() { lock (gate) { users = 0; End(); } }

    /// Adds this user to "Performance Log Users" (Windows asks for permission).
    /// It takes effect after signing out and in again.
    public static bool GrantPermission()
    {
        try
        {
            var group = new SecurityIdentifier("S-1-5-32-559").Translate(typeof(NTAccount)).Value;    // localized name
            group = group[(group.IndexOf('\\') + 1)..];
            var user = WindowsIdentity.GetCurrent().Name;
            return Native.RunElevated("net.exe", $"localgroup \"{group}\" \"{user}\" /add");
        }
        catch { return false; }
    }
}
