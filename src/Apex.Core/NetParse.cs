using System.Text.RegularExpressions;

namespace Apex.Core;

/// One network connection and the process that owns it.
public sealed record Connection(int Pid, string Protocol, string Local, int LocalPort, string Remote, int RemotePort, string State)
{
    public bool Listening => State == "Listening" || (Protocol == "UDP" && Remote.Length == 0);
    public bool LocalOnly => Remote is "127.0.0.1" or "::1" || Remote.StartsWith("127.");
}

public static class NetParse
{
    /// `netsh wlan show interfaces` → signal quality % (the first connected interface). Pure: tested.
    public static int? WlanSignal(string text)
    {
        var m = Regex.Match(text, @"^\s*Signal\s*:\s*(\d+)\s*%", RegexOptions.Multiline);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    /// The TCP states Windows uses (MIB_TCP_STATE), in plain words.
    public static string TcpState(int s) => s switch
    {
        1 => "Closed", 2 => "Listening", 3 => "Opening", 4 => "Opening", 5 => "Connected", 6 => "Closing", 7 => "Closing",
        8 => "Closing", 9 => "Closing", 10 => "Closing", 11 => "Closing", 12 => "Closing", _ => "—",
    };
}
