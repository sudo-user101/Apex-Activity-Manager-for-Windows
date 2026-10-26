using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Apex.Services;

/// Chat with a model running on this PC, as on the Mac: any local server with
/// the common chat API (LM Studio on port 1234, Ollama on 11434). Each
/// question carries Apex's live readings. Nothing leaves the PC.
internal static class LocalChat
{
    public sealed record Server(string Name, string BaseUrl);
    static readonly Server[] Known = { new("LM Studio", "http://127.0.0.1:1234/v1"), new("Ollama", "http://127.0.0.1:11434/v1") };
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// The first local server that answers, with its models.
    public static async Task<(Server? Server, List<string> Models)> Find()
    {
        foreach (var s in Known)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var json = JsonNode.Parse(await http.GetStringAsync(s.BaseUrl + "/models", cts.Token));
                var models = json?["data"]?.AsArray().Select(m => m?["id"]?.GetValue<string>() ?? "").Where(m => m.Length > 0).ToList() ?? new();
                return (s, models);
            }
            catch { }
        }
        return (null, new());
    }

    public const string Instructions =
        "You are the assistant inside Apex Activity Manager for Windows, by CyberNet Labs. Help only with this PC and its apps, " +
        "in plain words for anyone. Use the readings below; say when you don't know. Point to the Apex page that helps " +
        "(Processes, Performance, Gaming, Startup apps, Users, Services, Free Up Memory). Never call an app malware just because it's busy or unknown.";

    /// Streams the answer; `onText` gets each piece as it arrives.
    public static async Task Ask(Server server, string model, IEnumerable<(string Role, string Text)> history, string readings,
                                 Action<string> onText, CancellationToken cancel)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = Instructions + "\n\nThis PC right now:\n" + readings } };
        foreach (var (role, text) in history) messages.Add(new JsonObject { ["role"] = role, ["content"] = text });
        var body = new JsonObject { ["model"] = model, ["messages"] = messages, ["stream"] = true };
        using var req = new HttpRequestMessage(HttpMethod.Post, server.BaseUrl + "/chat/completions")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel);
        res.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(cancel));
        while (await reader.ReadLineAsync(cancel) is { } line)
        {
            if (!line.StartsWith("data:")) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            try
            {
                var piece = JsonNode.Parse(data)?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(piece)) onText(piece);
            }
            catch (JsonException) { }
        }
    }
}
