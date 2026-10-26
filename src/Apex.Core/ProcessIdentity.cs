namespace Apex.Core;

/// One particular process: its ID plus when it started (a FILETIME, as
/// Windows reports it). An ID alone isn't enough to act on: once a process
/// exits, Windows can give its ID to a new, unrelated process. Actions check
/// the start time on a handle to the process and act through that handle,
/// so they can only ever reach the process the user chose.
public readonly record struct ProcessIdentity(int Pid, long CreateTime)
{
    /// Both parts known (the start time can't be read for a few protected processes).
    public bool IsComplete => Pid > 0 && CreateTime > 0;
}

/// What happened to one process an action was meant for.
public enum ActionResult
{
    /// Done to the intended process.
    Done,
    /// It had already exited: nothing to do.
    Exited,
    /// Refused: its ID now belongs to a different process.
    Reused,
    /// The intended process is running, but only an administrator can act on it.
    NeedsAdmin,
    /// Refused: its identity couldn't be checked, so nothing was done.
    Unverifiable,
    /// Refused by Apex's own rules (System, Apex itself).
    Protected,
    /// Windows returned another error.
    Failed,
}

/// The outcome of an action on several processes, each checked on its own.
public sealed class ActionReport
{
    public List<ProcessIdentity> Done { get; } = new();
    public List<ProcessIdentity> NeedsAdmin { get; } = new();
    public List<(ProcessIdentity Target, ActionResult Result)> Refused { get; } = new();
    public List<string> Failures { get; } = new();

    public void Add(ProcessIdentity target, ActionResult result, string? error = null)
    {
        switch (result)
        {
            case ActionResult.Done or ActionResult.Exited: Done.Add(target); break;
            case ActionResult.NeedsAdmin: NeedsAdmin.Add(target); break;
            case ActionResult.Failed: Failures.Add(error ?? "Windows didn’t allow it."); break;
            default: Refused.Add((target, result)); break;
        }
    }

    /// Plain words for what wasn't done, and why. Null when everything went through.
    public string? Message(IReadOnlyDictionary<ProcessIdentity, string> names, string verb)
    {
        string Name(ProcessIdentity t) => "“" + (names.TryGetValue(t, out var n) ? n : "process " + t.Pid) + "”";
        string List(List<ProcessIdentity> ts) => ts.Count switch
        {
            1 => Name(ts[0]),
            2 => Name(ts[0]) + " and " + Name(ts[1]),
            _ => string.Join(", ", ts.Take(ts.Count - 1).Select(Name)) + " and " + Name(ts[^1]),
        };
        var lines = new List<string>();
        var reused = Refused.Where(r => r.Result == ActionResult.Reused).Select(r => r.Target).ToList();
        var unverifiable = Refused.Where(r => r.Result == ActionResult.Unverifiable).Select(r => r.Target).ToList();
        var protectedOnes = Refused.Where(r => r.Result == ActionResult.Protected).Select(r => r.Target).ToList();
        if (reused.Count > 0)
            lines.Add($"{List(reused)} had already closed, and {(reused.Count == 1 ? "its process ID now belongs" : "their process IDs now belong")} to something else, so nothing was done to {(reused.Count == 1 ? "it" : "them")}.");
        if (unverifiable.Count > 0)
            lines.Add($"Apex couldn’t confirm {List(unverifiable)} {(unverifiable.Count == 1 ? "is" : "are")} still the same, so it didn’t {verb} {(unverifiable.Count == 1 ? "it" : "them")}.");
        if (protectedOnes.Count > 0) lines.Add($"Apex doesn’t {verb} {List(protectedOnes)}.");
        if (NeedsAdmin.Count > 0)
            lines.Add($"{List(NeedsAdmin)} {(NeedsAdmin.Count == 1 ? "belongs" : "belong")} to another user or Windows. Restart Apex as administrator (⋯ › Restart as administrator) to {verb} {(NeedsAdmin.Count == 1 ? "it" : "them")}.");
        lines.AddRange(Failures.Distinct());
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }
}
