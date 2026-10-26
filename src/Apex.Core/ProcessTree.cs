namespace Apex.Core;

/// One row of the Processes table: a section header (Apps, Background
/// processes, Windows processes), an app with its helper processes, or one
/// process.
public sealed class ProcessRow
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public int? Pid { get; init; }
    public int[] Pids { get; init; } = Array.Empty<int>();
    /// The exact processes on screen (ID + start time): what actions act on.
    public ProcessIdentity[] Targets { get; init; } = Array.Empty<ProcessIdentity>();
    public bool IsSection { get; init; }
    public ProcessKind Kind { get; init; }
    public double Cpu { get; init; }
    public long Memory { get; init; }
    public double DiskPerSec { get; init; }
    public double Gpu { get; init; }
    public bool Suspended { get; init; }
    public List<ProcessRow> Children { get; init; } = new();
    public int Depth { get; set; }
}

public enum ProcessSortKey { Name, Cpu, Memory, Disk, Gpu }

public static class ProcessTree
{
    public static string SectionTitle(ProcessKind k, int count) => k switch
    {
        ProcessKind.App => $"Apps ({count})",
        ProcessKind.Background => $"Background processes ({count})",
        _ => $"Windows processes ({count})",
    };

    /// Sections → apps (one row per program, its processes underneath) →
    /// processes. `search` matches name, path or PID.
    public static List<ProcessRow> Build(IReadOnlyList<ProcessSample> procs, string search = "",
                                         ProcessSortKey sort = ProcessSortKey.Cpu, bool ascending = false)
    {
        var q = search.Trim();
        bool Matches(ProcessSample p) => q.Length == 0
            || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Path.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Pid.ToString() == q;

        var result = new List<ProcessRow>();
        foreach (var kind in new[] { ProcessKind.App, ProcessKind.Background, ProcessKind.Windows })
        {
            var members = procs.Where(p => p.Kind == kind && Matches(p)).ToList();
            if (members.Count == 0) continue;
            // One row per program (same executable), its processes as children.
            var programs = members.GroupBy(p => string.IsNullOrEmpty(p.Path) ? "name:" + p.Name : p.Path.ToLowerInvariant())
                .Select(g => ProgramRow(g.ToList()))
                .ToList();
            var rows = Sort(programs, sort, ascending);
            result.Add(new ProcessRow
            {
                Id = "section:" + kind, Name = SectionTitle(kind, rows.Count), IsSection = true, Kind = kind,
                Cpu = rows.Sum(r => r.Cpu), Memory = rows.Sum(r => r.Memory),
                DiskPerSec = rows.Sum(r => r.DiskPerSec), Gpu = rows.Max(r => r.Gpu), Children = rows,
            });
        }
        return result;
    }

    static ProcessRow ProgramRow(List<ProcessSample> g)
    {
        // The process with a window (or the oldest parent) names the program.
        var main = g.FirstOrDefault(p => p.HasWindow) ?? g.OrderBy(p => g.Any(o => o.Pid == p.ParentPid) ? 1 : 0).ThenBy(p => p.Pid).First();
        string name = string.IsNullOrEmpty(main.DisplayName) ? main.Name : main.DisplayName;
        var children = g.Count > 1
            ? g.OrderByDescending(p => p.Cpu).Select(p => Leaf(p)).ToList()
            : new List<ProcessRow>();
        return new ProcessRow
        {
            Id = g.Count > 1 ? "app:" + main.Path.ToLowerInvariant() + ":" + main.Name : "pid:" + main.Pid,
            Name = g.Count > 1 ? $"{name} ({g.Count})" : name,
            Path = main.Path, Pid = g.Count == 1 ? main.Pid : null, Pids = g.Select(p => p.Pid).ToArray(), Targets = g.Select(p => p.Identity).ToArray(),
            Kind = main.Kind, Cpu = g.Sum(p => p.Cpu), Memory = g.Sum(p => p.Memory),
            DiskPerSec = g.Sum(p => p.DiskPerSec), Gpu = g.Max(p => p.Gpu), Suspended = g.All(p => p.Suspended),
            Children = children,
        };
    }

    static ProcessRow Leaf(ProcessSample p) => new()
    {
        Id = "pid:" + p.Pid, Name = (string.IsNullOrEmpty(p.DisplayName) ? p.Name : p.DisplayName) + $"  ·  {p.Pid}",
        Path = p.Path, Pid = p.Pid, Pids = new[] { p.Pid }, Targets = new[] { p.Identity }, Kind = p.Kind, Cpu = p.Cpu, Memory = p.Memory,
        DiskPerSec = p.DiskPerSec, Gpu = p.Gpu, Suspended = p.Suspended,
    };

    public static List<ProcessRow> Sort(List<ProcessRow> rows, ProcessSortKey key, bool ascending)
    {
        Func<ProcessRow, IComparable> by = key switch
        {
            ProcessSortKey.Name => r => r.Name.ToLowerInvariant(),
            ProcessSortKey.Memory => r => r.Memory,
            ProcessSortKey.Disk => r => r.DiskPerSec,
            ProcessSortKey.Gpu => r => r.Gpu,
            _ => r => r.Cpu,
        };
        var sorted = ascending ? rows.OrderBy(by).ThenBy(r => r.Name) : rows.OrderByDescending(by).ThenBy(r => r.Name);
        return sorted.ToList();
    }

    /// Flattens for display: sections and programs always, a program's
    /// processes only when it's expanded.
    public static List<ProcessRow> Flatten(IEnumerable<ProcessRow> rows, ISet<string> expanded, ISet<string>? collapsedSections = null)
    {
        var list = new List<ProcessRow>();
        foreach (var s in rows)
        {
            s.Depth = 0; list.Add(s);
            if (collapsedSections?.Contains(s.Id) == true) continue;
            foreach (var r in s.Children)
            {
                r.Depth = 1; list.Add(r);
                if (expanded.Contains(r.Id)) foreach (var c in r.Children) { c.Depth = 2; list.Add(c); }
            }
        }
        return list;
    }
}
