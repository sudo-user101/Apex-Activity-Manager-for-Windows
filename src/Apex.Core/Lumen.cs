namespace Apex.Core;

/// Lumen AI: Apex's own cloud AI, run by CyberNet Labs, as on the Mac. Two
/// ways in: a monthly plan with CyberNet Labs, or API access bought from
/// CyberNet Labs. Either way the account gives a Lumen key; questions go to
/// the Lumen API (the common chat API: `/models`, `/chat/completions`).
public static class Lumen
{
    // CyberNet Labs' addresses, in one place. Until the service is live these
    // stay null: the Lumen screen then says it's opening soon and its buttons
    // wait. APEX_LUMEN_BASE points at a stand-in server for testing.
    public const string? LiveBase = null;
    public const string? PlansPage = null;
    public const string? ApiPage = null;

    public static string? Base => Environment.GetEnvironmentVariable("APEX_LUMEN_BASE") is { Length: > 0 } b ? b : LiveBase;
    public static bool IsLive => Base != null;

    /// The model Lumen answers with (the service picks its best one).
    public const string Model = "lumen";

    public enum Access { Plan, Api }
    public static string Title(Access a) => a == Access.Plan ? "Monthly Plan" : "API Access";
    public static string Detail(Access a) => a == Access.Plan
        ? "One monthly price from CyberNet Labs. Ask as much as your plan allows."
        : "Buy API credit from CyberNet Labs and pay only for what you ask.";
    public static string? Page(Access a) => a == Access.Plan ? PlansPage : ApiPage;

    public const string Blurb =
        "Apex’s own cloud AI, by CyberNet Labs. It sees what’s going on across your PC, remembers what’s been tried, and works through problems that keep coming back.";
    public const string NotYet =
        "Plans and API access open when CyberNet Labs launches Lumen AI. Until then, Apex Agent answers common questions on this PC, and Local AI uses a model on this PC.";

    /// What the service said about a key, in plain words. Pure: tested.
    public static string Problem(int status) => status switch
    {
        401 or 403 => "Lumen AI didn’t accept that key. Copy it again from your CyberNet Labs account.",
        402 => "Your Lumen plan or credit has run out. Top it up at CyberNet Labs.",
        429 => "Lumen AI is busy, or you’ve asked a lot in a short time. Try again in a minute.",
        >= 500 => "Lumen AI is having trouble right now. Try again in a few minutes.",
        _ => $"Lumen AI answered with an error ({status}).",
    };
}
