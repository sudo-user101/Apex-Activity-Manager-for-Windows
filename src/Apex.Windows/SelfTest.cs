using System.Diagnostics;
using Apex.Core;
using Apex.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Apex;

/// `Apex.exe --selftest`: checks the features that touch other processes and
/// the clipboard on a real Windows, using only processes it starts itself,
/// and writes PASS / FAIL lines to %TEMP%\apex-selftest.txt (the build fails
/// on any FAIL). Nothing else on the PC is touched.
internal static class SelfTest
{
    static readonly List<string> lines = new();
    static bool failed;

    static void Check(string name, bool ok, string detail = "")
    {
        lines.Add($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " · " + detail : "")}");
        failed |= !ok;
    }

    /// A harmless child process (waits a minute), and its identity.
    static (Process P, ProcessIdentity Id) Child()
    {
        var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
        Thread.Sleep(200);
        return (p, new ProcessIdentity(p.Id, p.StartTime.ToFileTime()));
    }

    public static async Task<int> Run()
    {
        var children = new List<Process>();
        try
        {
            // Process identity: End task acts only on the process that was chosen.
            var (a, ia) = Child(); var (b, ib) = Child(); children.Add(a); children.Add(b);
            Check("identity matches the kernel", ProcessActions.Check(ia) == ActionResult.Done, ia.ToString());
            var r = ProcessActions.End(new[] { ia });
            a.WaitForExit(3000);
            Check("end a valid identity", r.Done.Contains(ia) && a.HasExited);
            Check("an exited process: nothing to do", ProcessActions.End(new[] { ia }).Done.Contains(ia));
            var stale = ib with { CreateTime = ib.CreateTime + 1 };
            var rs = ProcessActions.End(new[] { stale });
            Thread.Sleep(300);
            Check("a reused ID is refused", rs.Refused.Any(x => x.Result == ActionResult.Reused) && !b.HasExited);
            Check("an incomplete identity is refused", ProcessActions.End(new[] { ib with { CreateTime = 0 } }).Refused.Any(x => x.Result == ActionResult.Unverifiable) && !b.HasExited);
            Check("Apex itself and System are protected", ProcessActions.End(new[] { new ProcessIdentity(Environment.ProcessId, 1), new ProcessIdentity(4, 1) }).Refused.Count == 2);
            var (c, ic) = Child(); children.Add(c);
            var mixed = ProcessActions.End(new[] { ic, stale });
            c.WaitForExit(3000);
            Check("mixed targets: each checked on its own", mixed.Done.Contains(ic) && c.HasExited && mixed.Refused.Count == 1 && !b.HasExited);
            Check("refusals are explained", (mixed.Message(new Dictionary<ProcessIdentity, string> { [stale] = "ping" }, "end") ?? "").Contains("“ping” had already closed"));

            // Efficiency mode: applied, then restored.
            Check("efficiency mode on", Efficiency.Apply(ib, true));
            b.Refresh();
            Check("priority is idle", b.PriorityClass == ProcessPriorityClass.Idle, b.PriorityClass.ToString());
            Check("efficiency mode off", Efficiency.Apply(ib, false));
            b.Refresh();
            Check("priority is normal again", b.PriorityClass == ProcessPriorityClass.Normal, b.PriorityClass.ToString());
            Check("efficiency mode refuses a reused ID", !Efficiency.Apply(stale, true));
            Check("trim memory", Efficiency.Trim(new[] { new ProcessSample { Pid = ib.Pid, CreateTime = ib.CreateTime } }) == 1);

            // Clipboard history: records copies, skips private ones.
            ClipboardHistory.Shared.Start();
            ClipboardHistory.Shared.Enabled = true;
            var text = "apex-selftest-" + Guid.NewGuid().ToString("N")[..8];
            var d = new DataPackage(); d.SetText(text); Clipboard.SetContent(d); Clipboard.Flush();
            await Task.Delay(1500);
            Check("clipboard history records a copy", ClipboardHistory.Shared.Items.Any(i => i.Text == text),
                  ClipboardHistory.Shared.Items.Count + " item(s)");
            var secret = "apex-secret-" + Guid.NewGuid().ToString("N")[..8];
            var s = new DataPackage(); s.SetText(secret); s.SetData("ExcludeClipboardContentFromMonitorProcessing", new byte[] { 0 }.AsBufferStream());
            Clipboard.SetContent(s); Clipboard.Flush();
            await Task.Delay(1500);
            Check("a password manager's copy is skipped", !ClipboardHistory.Shared.Items.Any(i => i.Text == secret));
        }
        catch (Exception e) { Check("no exceptions", false, e.ToString()); }
        finally
        {
            foreach (var p in children) { try { if (!p.HasExited) p.Kill(); } catch { } }
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "apex-selftest.txt"), lines);
        }
        return failed ? 1 : 0;
    }

    static Windows.Storage.Streams.IRandomAccessStream AsBufferStream(this byte[] bytes)
    {
        var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var w = new Windows.Storage.Streams.DataWriter(stream);
        w.WriteBytes(bytes);
        w.StoreAsync().AsTask().Wait();
        w.DetachStream();
        stream.Seek(0);
        return stream;
    }
}
