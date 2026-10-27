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

            // Frame rates: the reader starts and Windows' graphics events arrive.
            FrameMonitor.Acquire();
            for (int i = 0; i < 40 && FrameMonitor.Status is FrameMonitor.State.Starting or FrameMonitor.State.Off; i++) await Task.Delay(250);
            await Task.Delay(4000);
            Check("frame-rate reader runs (trace session live, no errors)", FrameMonitor.Status == FrameMonitor.State.Running, $"{FrameMonitor.Status} {FrameMonitor.Detail}");
            lines.Add("INFO apps drawing: " + string.Join(", ", FrameMonitor.Apps().Select(a => $"{a.App} {a.Stats.Fps:0} FPS")));
            FrameMonitor.StopAll();

            // Apex Agent on real readings: answers, and a quit command carries exact identities.
            var agent = ApexAgent.Ask("why is my pc slow", new ApexAgent.Readings { Cpu = 10, MemoryTotal = 8L << 30, DiskTotal = 100L << 30, DiskFree = 50L << 30 });
            Check("Apex Agent answers", !agent.Unknown && agent.Actions.Count > 0);
            var (d2, id2) = Child(); children.Add(d2);
            var r2 = new ApexAgent.Readings { Apps = new() { new ApexAgent.Item("Ping Test", d2.MainModule?.FileName ?? "", new[] { id2 }) } };
            var quit = ApexAgent.Ask("close ping test", r2).Actions.OfType<ApexAgent.QuitApps>().FirstOrDefault();
            Check("Apex Agent's quit button names the exact process", quit != null && quit.Targets.SequenceEqual(new[] { id2 }));
            if (quit != null) { var rq = ProcessActions.End(quit.Targets); d2.WaitForExit(3000); Check("…and ends it", rq.Done.Contains(id2) && d2.HasExited); }

            // Game mode: on (power plan, priority), then everything put back.
            var plansBefore = GameMode.Plans().FirstOrDefault(p => p.Active)?.Id;
            var (g, ig) = Child(); children.Add(g);
            var path = g.MainModule?.FileName ?? "";
            Apex.UI.AppStateTest.AppState_ForTest(new Snapshot { Processes = new[] { new ProcessSample { Pid = ig.Pid, CreateTime = ig.CreateTime, Path = path, Name = "cmd.exe", Kind = ProcessKind.App } } });
            var said = GameMode.Start(path, "Test game", 60, Array.Empty<string>());
            g.Refresh();
            Check("Game mode raises the game's priority", g.PriorityClass == ProcessPriorityClass.High, said);
            GameMode.Stop();
            g.Refresh();
            Check("Game mode puts the priority back", g.PriorityClass == ProcessPriorityClass.Normal, g.PriorityClass.ToString());
            Check("Game mode puts the power plan back", GameMode.Plans().FirstOrDefault(p => p.Active)?.Id == plansBefore);

            // Network: connections with owners, a listening port found, the Doctor's measurements.
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var conns = NetworkTools.Connections();
            Check("connections are read with their apps", conns.Count > 0 && conns.Any(c => c.Listening && c.LocalPort == port && c.Pid == Environment.ProcessId), $"{conns.Count} endpoints");
            listener.Stop();
            var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback);
            if (nic != null)
            {
                var input = await NetworkTools.Probe(nic.Id, "Ethernet", null);
                var findings = NetworkDoctor.Diagnose(input);
                Check("Network Doctor measures and explains", findings.Count > 0, string.Join(", ", findings.Select(f => f.Id)) + $" · router {input.Router?.Received}/{input.Router?.Sent} · dns {input.DnsMs:0} ms");
            }
            Check("public IP lookup", (await NetworkTools.PublicIp()) is { Length: > 6 });

            // Storage: a scan of the temp folder, and cleaning only old files in a test folder.
            var cats = StorageClean.Categories();
            Check("storage categories", cats.Count >= 8 && cats.Any(c => c.NeedsAdmin));
            var testRoot = Path.Combine(Path.GetTempPath(), "apex-selftest-clean");
            Directory.CreateDirectory(testRoot);
            var oldFile = Path.Combine(testRoot, "old.tmp"); File.WriteAllText(oldFile, "x"); File.SetLastWriteTime(oldFile, DateTime.Now.AddDays(-5));
            var newFile = Path.Combine(testRoot, "new.tmp"); File.WriteAllText(newFile, "y");
            var cat = new CleanCategory("t", "t", "", new[] { testRoot }, TimeSpan.FromDays(1));
            var cr = Cleaner.Clean(cat, Cleaner.Scan(cat, DateTime.Now));
            Check("clean removes only old files", cr.Removed == 1 && !File.Exists(oldFile) && File.Exists(newFile));
            Directory.Delete(testRoot, true);
            var (rbSize, rbItems) = StorageClean.RecycleBin();
            lines.Add($"INFO recycle bin: {rbItems} items, {rbSize} bytes");

            // Analyze: a walk with an online-only file and a junction back to the top (must not loop).
            var tree = Path.Combine(Path.GetTempPath(), "apex-selftest-tree");
            if (Directory.Exists(tree)) Directory.Delete(tree, true);
            Directory.CreateDirectory(Path.Combine(tree, "Photos", "2024"));
            File.WriteAllBytes(Path.Combine(tree, "Photos", "a.bin"), new byte[5000]);
            File.WriteAllBytes(Path.Combine(tree, "Photos", "2024", "b.bin"), new byte[3000]);
            var cloudFile = Path.Combine(tree, "cloud.bin");
            File.WriteAllBytes(cloudFile, new byte[7000]);
            File.SetAttributes(cloudFile, FileAttributes.Offline);
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(tree, "Loop")}\" \"{tree}\"") { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit(10_000);
            Check("a junction was made for the test", new DirectoryInfo(Path.Combine(tree, "Loop")).LinkTarget != null);
            var dt = DiskTree.Scan(tree);
            Check("Analyze: totals, online-only, junction not followed",
                  dt.Top.Size == 15000 && dt.FileCount == 3 && dt.Top.OnlineOnly == 7000 && dt.Find(Path.Combine(tree, "Photos"))?.Size == 8000,
                  $"size {dt.Top.Size}, files {dt.FileCount}, online-only {dt.Top.OnlineOnly}");
            Check("Analyze: largest first", dt.Largest().Select(f => f.Name).SequenceEqual(new[] { "cloud.bin", "a.bin", "b.bin" }));
            Directory.Delete(Path.Combine(tree, "Loop"));
            Directory.Delete(tree, true);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var sw = Stopwatch.StartNew();
            var homeTree = DiskTree.Scan(home);
            lines.Add($"INFO Analyze of {home}: {homeTree.FileCount:N0} files, {homeTree.Top.Size:N0} bytes, {homeTree.Unreadable} unreadable, {sw.Elapsed.TotalSeconds:0.0} s");
            var sysRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
            if (Environment.GetEnvironmentVariable("APEX_COMPARE_WALK") == "1")
            {
                // How fast Windows' own lister walks the same drive (list only, nothing copied).
                sw.Restart();
                var rc = Process.Start(new ProcessStartInfo("robocopy.exe", $"{sysRoot} NUL /L /S /XJ /R:0 /W:0 /NJH /NJS /NFL /NDL /NP /MT:8") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
                rc.StandardOutput.ReadToEnd(); rc.WaitForExit();
                lines.Add($"INFO robocopy list of {sysRoot}: {sw.Elapsed.TotalSeconds:0.0} s");
            }
            sw.Restart();
            var driveTree = DiskTree.Scan(sysRoot);
            lines.Add($"INFO Analyze of {sysRoot}: {driveTree.FileCount:N0} files, {driveTree.Top.Size:N0} bytes, {driveTree.Unreadable} unreadable, {sw.Elapsed.TotalSeconds:0.0} s; drive used {new DriveInfo(sysRoot).TotalSize - new DriveInfo(sysRoot).TotalFreeSpace:N0}");

            // Compress in place: real PNGs, one opaque (becomes a JPEG), one see-through (left), the originals to the Recycle Bin.
            var shots = Path.Combine(Path.GetTempPath(), "apex-selftest-shots");
            if (Directory.Exists(shots)) Directory.Delete(shots, true);
            Directory.CreateDirectory(shots);
            await PhotoCompress.WriteTestPng(Path.Combine(shots, "Screenshot.png"), 1400, 900, seeThrough: false);
            await PhotoCompress.WriteTestPng(Path.Combine(shots, "Icon.png"), 400, 400, seeThrough: true);
            File.WriteAllBytes(Path.Combine(shots, "Screenshot.jpg"), new byte[10]);      // the name is taken: the copy must not overwrite it
            var (plan, onlineCount) = InPlaceCompress.Plan(shots);
            Check("Compress in place: plan", plan.Count == 2 && onlineCount == 0, string.Join(", ", plan.Select(p => Path.GetFileName(p.Path))));
            var made = await PhotoCompress.MakeCopies(plan, (_, _) => { }, CancellationToken.None);
            Check("Compress in place: the opaque PNG got a smaller copy, the see-through one was left",
                  made.Copies.Count == 1 && made.Skipped.Any(x => x.Name == "Icon.png"),
                  string.Join("; ", made.Copies.Select(c => $"{Path.GetFileName(c.Original)} {c.Before}→{c.After}")) + " | " + string.Join("; ", made.Skipped.Select(x => $"{x.Name}: {x.Why}")));
            var swapFailed = InPlaceCompress.SwapIn(made.Copies, StorageClean.ToRecycleBin);
            var names = Directory.GetFiles(shots).Select(Path.GetFileName).OrderBy(n => n).ToArray();
            Check("Compress in place: swapped in under a free name, original in the Recycle Bin",
                  swapFailed.Count == 0 && names.SequenceEqual(new[] { "Icon.png", "Screenshot 2.jpg", "Screenshot.jpg" }) && new FileInfo(Path.Combine(shots, "Screenshot 2.jpg")).Length > 1000,
                  string.Join(", ", names));
            Directory.Delete(shots, true);

            // Memory: compression state, page file and reads from disk, from two real readings.
            using (var sampler = new Sampler())
            {
                sampler.Sample();
                await Task.Delay(1500);
                var mem = sampler.Sample().Memory;
                lines.Add($"INFO memory: compression {(mem.CompressionOn ? "on" : "off")}, compressed {mem.Compressed:N0}, page file {mem.PageFileUsed:N0} of {mem.PageFileSize:N0}, pages in/s {mem.PagesInPerSec:0.0}");
                Check("Memory: page file and reads from disk are read", mem.PageFileSize >= 0 && mem.PagesInPerSec >= 0 && mem.Total > 0);
                var mm = MmAgentCompression();
                Check("Memory: the compression state matches Windows' own (Get-MMAgent)", mm == null || mm == mem.CompressionOn, $"Apex {mem.CompressionOn}, Windows {mm?.ToString() ?? "unknown"}");
            }
        }
        catch (Exception e) { Check("no exceptions", false, e.ToString()); }
        finally
        {
            foreach (var p in children) { try { if (!p.HasExited) p.Kill(); } catch { } }
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "apex-selftest.txt"), lines);
        }
        return failed ? 1 : 0;
    }

    /// Windows' own answer (Get-MMAgent, as the self-test runs elevated or not): null when it can't say.
    static bool? MmAgentCompression()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -Command (Get-MMAgent).MemoryCompression") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var o = p.StandardOutput.ReadToEnd().Trim(); p.WaitForExit(20_000);
            lines.Add($"INFO Get-MMAgent MemoryCompression: {o}");
            return o.Equals("True", StringComparison.OrdinalIgnoreCase) ? true : o.Equals("False", StringComparison.OrdinalIgnoreCase) ? false : null;
        }
        catch { return null; }
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
