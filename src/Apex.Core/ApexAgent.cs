using System.Text.RegularExpressions;

namespace Apex.Core;

/// Apex Agent: built-in answers to the questions PC users ask most, worked
/// out on this PC with no internet and no AI, as on the Mac. It matches the
/// question to a topic by its words, answers with this PC's readings, and
/// offers the button that fixes it. It also carries out commands ("quit
/// Chrome", "close everything except Spotify", "stop process node", "I want a
/// steady 60 FPS in my game") after you press the button. Pure: tested.
public static class ApexAgent
{
    public abstract record AgentAction(string Title)
    {
        /// Does something (rather than opening a page).
        public virtual bool Acts => false;
    }
    public sealed record OpenPage(string Page, string Name) : AgentAction("Open " + Name);
    public sealed record FreeMemory() : AgentAction("Free Up Memory");
    public sealed record QuitApps(ProcessIdentity[] Targets, string[] Names) : AgentAction(Names.Length == 1 ? "End " + Names[0] : $"End {Names.Length} Apps")
    { public override bool Acts => true; }
    public sealed record StopProcesses(ProcessIdentity[] Targets, string[] Names) : AgentAction(Names.Length == 1 ? "Stop " + Names[0] : $"Stop {Names.Length} Processes")
    { public override bool Acts => true; }
    public sealed record StartGameMode(string GamePath, string GameName, double TargetFps) : AgentAction($"Game Mode · {TargetFps:0} FPS")
    { public override bool Acts => true; }

    /// An open app, or one of your own processes, as the answer saw it.
    public sealed record Item(string Name, string Path, ProcessIdentity[] Targets, double Cpu = 0, long Memory = 0, double Fps = 0);

    /// What the agent knows about this PC right now (from Apex's sample).
    public sealed class Readings
    {
        public List<Item> Apps { get; init; } = new();
        public List<Item> Processes { get; init; } = new();
        public double Cpu { get; init; }
        public long MemoryUsed { get; init; }
        public long MemoryTotal { get; init; }
        public string Pressure { get; init; } = "Normal";
        public long DiskFree { get; init; }
        public long DiskTotal { get; init; }
        public int? Battery { get; init; }
        public bool OnPower { get; init; } = true;
        public double Gpu { get; init; }
        public string Network { get; init; } = "";
    }

    public sealed record Answer(string Text, List<AgentAction> Actions, bool Unknown = false);

    static readonly string[] QuitWords = { "quit", "close", "kill", "exit", "end", "shut", "terminate", "stop" };
    static readonly HashSet<string> Filler = new() { "the", "my", "a", "an", "please", "can", "you", "could", "would", "for", "me", "i", "want", "to", "and",
        "of", "now", "just", "okay", "ok", "it", "that", "this", "are", "is", "not", "using", "use", "in", "on", "too", "also", "some", "down", "task", "tasks" };
    static readonly HashSet<string> AllWords = new() { "all", "every", "everything", "everyone" };
    static readonly HashSet<string> AppWords = new() { "app", "windows", "window" };

    public static Answer Ask(string question, Readings r)
    {
        var q = " " + Normalized(question) + " ";
        if (q.Trim().Length == 0) return Fallback;
        if (Game(q, r) is { } g) return g;
        if (Command(q, r) is { } c) return c;
        if (new[] { "hi", "hello", "hey", "good morning", "good evening" }.Any(w => q == $" {w} "))
            return new("Hello! Ask me about your PC: why it’s slow, what’s filling the disk, the battery, Wi-Fi, games and more.", new());
        if (new[] { "thanks", "thank you", "thx", "cheers" }.Any(w => q.Contains($" {w} ")))
            return new("You’re welcome. Anything else about your PC?", new());
        (Topic Topic, int Score)? best = null;
        foreach (var t in Topics)
        {
            int score = t.Words.Sum(w => q.Contains($" {w.Key} ") ? w.Value : 0);
            if (score > (best?.Score ?? 0)) best = (t, score);
        }
        return best is { Score: >= 2 } b ? b.Topic.Answer(r) : Fallback;
    }

    public static readonly Answer Fallback = new("I can’t answer that one yet. I know the common questions about a PC (speed, storage, battery, heat, Wi-Fi, frozen apps and games), but not this. Try **Local AI** above if you have a model on this PC.", new(), true);

    // ---- Game mode ---------------------------------------------------------------

    /// "I want a steady 60 fps in Fortnite", "play X at 144", "game mode X 60". Null when it isn't one.
    static Answer? Game(string q, Readings r)
    {
        var m = Regex.Match(q, @" (\d{2,3}) ?(fps|frames|frame rate|hz)? ");
        // A frame-rate wish or "game mode"; never a command to close something ("close the game").
        bool asks = q.Contains(" fps ") || q.Contains(" frames ") || q.Contains(" frame rate ") || q.Contains(" game mode ")
                    || q.Contains(" play ") || q.Contains(" steady ") || q.Contains(" stable ");
        if (!asks || QuitWords.Any(w => q.Contains($" {w} ")) || q.StartsWith(" how ") || q.StartsWith(" why ") || q.StartsWith(" what ")) return null;
        double target = m.Success && double.TryParse(m.Groups[1].Value, out var n) && n is >= 20 and <= 500 ? n : 60;
        var words = q.Trim().Split(' ');
        // The game: an open app named in the words, else the app drawing the most frames.
        var named = Matches(words, r.Apps);
        var game = named.FirstOrDefault() ?? r.Apps.Where(a => a.Fps > 0).OrderByDescending(a => a.Fps).FirstOrDefault();
        if (game == null)
            return new("Start the game first, then ask me again: I’ll pick it up, quiet the other apps, switch to the fastest power plan and keep an eye on the frame rate.", new() { new OpenPage("gaming", "Gaming") });
        var others = r.Apps.Count(a => a.Path != game.Path);
        return new($"I’ll put **{game.Name}** in Game mode with a target of **{target:0} FPS**: " +
                   (others > 0 ? $"the other {others} app{(others == 1 ? "" : "s")} go into Efficiency mode, " : "") +
                   "Windows switches to its fastest power plan, and the game gets high priority. I’ll watch the frame rate and tell you what holds it back if it drops. " +
                   "Everything goes back to normal when you stop or close the game. (No app can promise a frame rate; the game and your hardware decide. I’ll show how close we get.)",
                   new() { new StartGameMode(game.Path, game.Name, target), new OpenPage("gaming", "Gaming") });
    }

    // ---- Commands -------------------------------------------------------------

    static Answer? Command(string q, Readings r)
    {
        var words = q.Trim().Split(' ').ToList();
        int verbAt = words.FindIndex(w => QuitWords.Contains(w));
        if (verbAt < 0) return null;
        if (new[] { "how", "why", "what", "when", "where", "does", "is", "should" }.Contains(words[0])) return null;
        var rest = words.Skip(verbAt + 1).Where(w => !QuitWords.Contains(w)).ToList();
        var keepWords = new List<string>();
        int cut = rest.FindIndex(w => new[] { "except", "but", "besides", "apart", "other", "without", "excluding", "keep" }.Contains(w));
        if (cut >= 0) { keepWords = rest.Skip(cut + 1).ToList(); rest = rest.Take(cut).ToList(); }
        bool wantsProcesses = rest.Contains("process");
        bool saysAll = rest.Any(AllWords.Contains) || (rest.Any(AppWords.Contains) && !wantsProcesses);
        var named = Matches(rest, r.Apps);
        var kept = Matches(keepWords, r.Apps);

        if (wantsProcesses)
        {
            var wanted = rest.Where(w => !Filler.Contains(w) && w != "process" && !AllWords.Contains(w)).ToList();
            var found = r.Processes.Where(p => wanted.Any(w => p.Name.ToLowerInvariant().StartsWith(w))).ToList();
            if (found.Count > 0)
                return new($"I found {List(found.Select(f => f.Name))}. Stopping {(found.Count == 1 ? "it" : "them")} ends {(found.Count == 1 ? "it" : "them")} straight away; anything unsaved is lost.",
                           new() { new StopProcesses(found.SelectMany(f => f.Targets).ToArray(), found.Select(f => f.Name).ToArray()) });
            var busy = r.Processes.Where(p => p.Cpu >= 5).OrderByDescending(p => p.Cpu).Take(3).ToList();
            if (busy.Count == 0) return new("None of your processes is doing much right now, so there’s nothing worth stopping. Windows’ own processes are never stopped.", new());
            var actions = busy.Select(b => (AgentAction)new StopProcesses(b.Targets, new[] { b.Name })).ToList();
            actions.Add(new OpenPage("processes", "Processes"));
            return new($"Which one? Your busiest processes right now are {List(busy.Select(b => $"**{b.Name}** ({b.Cpu:0}% CPU)"))}. I only stop your own processes, never Windows’.", actions);
        }
        if (named.Count > 0 && !saysAll)
            return new($"I’ll end {List(named.Select(n => $"**{n.Name}**"))}. Anything unsaved in {(named.Count == 1 ? "it" : "them")} is lost.",
                       new() { new QuitApps(named.SelectMany(n => n.Targets).ToArray(), named.Select(n => n.Name).ToArray()) });
        if (saysAll)
        {
            var targets = r.Apps.Where(a => !kept.Contains(a)).ToList();
            if (targets.Count == 0) return new("There are no other apps open to end.", new());
            var keepNote = kept.Count == 0 ? "" : " " + List(kept.Select(k => $"**{k.Name}**")) + " stays open.";
            return new($"I’ll end these {targets.Count} app{(targets.Count == 1 ? "" : "s")}: {List(targets.Select(t => t.Name))}.{keepNote} Anything unsaved in them is lost. Apex stays open.",
                       new() { new QuitApps(targets.SelectMany(t => t.Targets).ToArray(), targets.Select(t => t.Name).ToArray()) });
        }
        var unknown = rest.Where(w => !Filler.Contains(w) && !AppWords.Contains(w)).ToList();
        if (unknown.Count > 0)
            return new($"I can’t find an open app called “{string.Join(' ', unknown)}”. " + (r.Apps.Count == 0 ? "No apps are open." : "Open apps: " + List(r.Apps.Select(a => a.Name)) + "."), new());
        return null;
    }

    /// Open apps named in the words: the whole name, or a distinctive word of it.
    public static List<Item> Matches(IEnumerable<string> words, IEnumerable<Item> apps)
    {
        var w = words.ToList();
        var text = " " + string.Join(' ', w) + " ";
        return apps.Where(a =>
        {
            var name = Normalized(a.Name);
            if (name.Length > 0 && text.Contains($" {name} ")) return true;
            return name.Split(' ').Any(x => x.Length >= 3 && !Filler.Contains(x) && w.Contains(x));
        }).ToList();
    }

    static string List(IEnumerable<string> items)
    {
        var l = items.ToList();
        return l.Count switch { 0 => "", 1 => l[0], _ => string.Join(", ", l.Take(l.Count - 1)) + " and " + l[^1] };
    }

    static readonly Dictionary<string, string> Same = new()
    {
        ["pc"] = "computer", ["laptop"] = "computer", ["wifi"] = "wi fi", ["wireless"] = "wi fi", ["ram"] = "memory", ["storage"] = "disk",
        ["space"] = "disk", ["hdd"] = "disk", ["ssd"] = "disk", ["drive"] = "disk", ["laggy"] = "slow", ["lag"] = "slow", ["lagging"] = "slow",
        ["sluggish"] = "slow", ["slower"] = "slow", ["freezing"] = "frozen", ["freezes"] = "frozen", ["freeze"] = "frozen", ["hung"] = "frozen",
        ["unresponsive"] = "frozen", ["hot"] = "heat", ["overheating"] = "heat", ["warm"] = "heat", ["temperature"] = "heat", ["fans"] = "fan",
        ["noisy"] = "loud", ["drains"] = "drain", ["draining"] = "drain", ["apps"] = "app", ["applications"] = "app", ["application"] = "app",
        ["programs"] = "app", ["program"] = "app", ["internet"] = "wi fi", ["disconnects"] = "disconnect", ["processes"] = "process",
        ["games"] = "game", ["gaming"] = "gaming", ["stutter"] = "stutters", ["stuttering"] = "stutters", ["framerate"] = "frame rate",
        ["startup"] = "login", ["boot"] = "login", ["cleanup"] = "clean", ["clear"] = "clean",
    };

    /// Lower case, letters and digits only, single spaces; a few spellings evened out.
    public static string Normalized(string s)
    {
        var chars = s.ToLowerInvariant().Replace('’', '\'').Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        var words = new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries).SelectMany(w => (Same.TryGetValue(w, out var v) ? v : w).Split(' '));
        return string.Join(' ', words);
    }

    // ---- Topics -----------------------------------------------------------------

    sealed record Topic(string Id, Dictionary<string, int> Words, Func<Readings, Answer> Answer);

    static string Gb(long b) => Fmt.Bytes(b);

    /// For tests: which topic a question lands on.
    public static string? TopicOf(string question)
    {
        var q = " " + Normalized(question) + " ";
        (string Id, int Score)? best = null;
        foreach (var t in Topics)
        {
            int score = t.Words.Sum(w => q.Contains($" {w.Key} ") ? w.Value : 0);
            if (score > (best?.Score ?? 0)) best = (t.Id, score);
        }
        return best is { Score: >= 2 } b ? b.Id : null;
    }

    static readonly Topic[] Topics =
    {
        new("slow", new() { ["slow"] = 3, ["speed up"] = 3, ["faster"] = 2, ["performance"] = 2, ["frozen"] = 1 }, r =>
        {
            var lines = new List<string> { "Here’s what’s slowing your PC down right now:" };
            var top = r.Apps.Concat(r.Processes).OrderByDescending(a => a.Cpu).FirstOrDefault();
            if (top is { Cpu: >= 30 }) lines.Add($"- **{top.Name}** is using {top.Cpu:0}% of the processor. End it if you don’t need it, or put it in **Efficiency mode**.");
            if (r.Pressure != "Normal") lines.Add($"- Memory pressure is **{r.Pressure}**: Windows is short of memory and using the page file.");
            if (r.DiskTotal > 0 && (double)r.DiskFree / r.DiskTotal < 0.1) lines.Add($"- The system disk is nearly full ({Gb(r.DiskFree)} free). Windows slows down below about 10% free.");
            if (lines.Count == 1)
                lines = new() { $"Nothing is overloading your PC right now: the processor is at {r.Cpu:0}%, memory pressure is {r.Pressure}, and there’s {Gb(r.DiskFree)} free on the system disk.",
                                "", "If it felt slow a moment ago, a restart once a week also helps." };
            else lines.AddRange(new[] { "", "Steps that help most:", "1. Close apps you’re not using (and browser tabs).", "2. Click **Free Up Memory**.", "3. Restart if it’s been days." });
            return new(string.Join("\n", lines), new() { new OpenPage("processes", "Processes"), new FreeMemory() });
        }),
        new("storage", new() { ["disk"] = 2, ["full"] = 2, ["free up"] = 3, ["clean"] = 2, ["large files"] = 3, ["delete"] = 1, ["running out"] = 2 }, r =>
            new($"Your system disk has **{Gb(r.DiskFree)} free** of {Gb(r.DiskTotal)}.\n\nTo get space back safely:\n1. Empty the Recycle Bin.\n2. Settings › System › Storage › **Temporary files**: Windows Update leftovers, delivery cache and old upgrade files are usually the biggest.\n3. Turn on **Storage Sense** so it happens by itself.\n\nApex’s own cleaner is coming in the next update.",
                new() { new OpenPage("performance", "Performance") })),
        new("battery", new() { ["battery"] = 2, ["drain"] = 3, ["battery life"] = 3, ["lasts"] = 2 }, r =>
            new((r.Battery is { } b ? $"The battery is at **{b}%**{(r.OnPower ? " and plugged in" : "")}. " : "This PC has no battery. ") +
                "\n\nTo make it last longer:\n1. Lower the screen brightness: the display uses the most power.\n2. Put busy apps in **Efficiency mode**.\n3. Turn on **Energy saver** (Settings › System › Power & battery).\n4. Games, video calls and browser tabs with video drain it fastest.",
                new() { new OpenPage("processes", "Processes") })),
        new("heat", new() { ["heat"] = 3, ["fan"] = 3, ["loud"] = 2, ["cool"] = 1 }, r =>
        {
            var top = r.Apps.Concat(r.Processes).OrderByDescending(a => a.Cpu).FirstOrDefault();
            var t = top is { Cpu: >= 30 } ? $"**{top.Name}** is working hard ({top.Cpu:0}% CPU), which makes heat. " : "";
            return new(t + "PCs run warm under load and slow down to protect themselves when too hot.\n\nTo cool it:\n1. End or put the busiest app in **Efficiency mode**.\n2. Keep the vents clear and the PC on a hard, flat surface.\n3. Clean dust from the fans if it’s been a while.",
                new() { new OpenPage("processes", "Processes") });
        }),
        new("wifi", new() { ["wi fi"] = 3, ["network"] = 2, ["disconnect"] = 3, ["router"] = 2, ["no internet"] = 3, ["slow internet"] = 3, ["ping"] = 2 }, r =>
            new((r.Network.Length == 0 ? "Your PC isn’t connected to a network right now." : $"You’re connected through **{r.Network}**.") +
                "\n\nQuick fixes:\n1. Turn Wi-Fi off and on (Win+A).\n2. Restart the router (unplug it for 10 seconds).\n3. Move closer to the router.\n4. Settings › Network & internet › Advanced network settings › **Network reset** if nothing else works.",
                new() { new OpenPage("performance", "Performance") })),
        new("frozen", new() { ["frozen"] = 3, ["not responding"] = 4, ["stuck"] = 2, ["crash"] = 2, ["crashes"] = 2 }, _ =>
            new("To close an app that’s stuck: in **Processes**, select it and press **Delete** (End task). Unsaved work is lost. If the same app keeps freezing, update it.",
                new() { new OpenPage("processes", "Processes") })),
        new("games", new() { ["game"] = 2, ["gaming"] = 3, ["stutters"] = 3, ["frame rate"] = 3, ["fps"] = 3, ["low fps"] = 4 }, r =>
        {
            var drawing = r.Apps.Where(a => a.Fps > 0).OrderByDescending(a => a.Fps).FirstOrDefault();
            var now = drawing != null ? $"**{drawing.Name}** is running at about **{drawing.Fps:0} FPS** right now. " : "";
            return new(now + "Tell me the frame rate you want, like **“steady 60 FPS in my game”**, and I’ll turn on Game mode: other apps go into Efficiency mode, Windows uses its fastest power plan, the game gets high priority, and I watch the frame rate and say what holds it back.",
                new() { new OpenPage("gaming", "Gaming") });
        }),
        new("login", new() { ["login"] = 3, ["starts"] = 1, ["slow login"] = 4, ["takes long"] = 2 }, _ =>
            new("Apps that start when you sign in slow the start. Open **Startup apps** and switch off the ones you don’t need right away.",
                new() { new OpenPage("startup", "Startup apps") })),
        new("memory", new() { ["memory"] = 3, ["memory full"] = 4, ["page file"] = 3 }, r =>
            new($"Memory: **{Gb(r.MemoryUsed)} of {Gb(r.MemoryTotal)}** in use, pressure **{r.Pressure}**. " +
                (r.Pressure == "Normal" ? "That’s fine: Windows uses free memory to cache files and gives it back when apps need it." : "Close or quiet apps you aren’t using: **Free Up Memory** lists them biggest first."),
                new() { new FreeMemory() })),
    };
}
