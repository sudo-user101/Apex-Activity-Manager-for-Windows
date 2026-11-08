using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Apex.Core;

public enum CloudProvider { Anthropic, Gemini, Mistral, OpenAI, OpenRouter, Custom }

/// Cloud AI with the person's own key, as on the Mac. Every provider but
/// Anthropic uses the common chat API; Anthropic has its own. Pure parts are tested.
public static class CloudProviders
{
    public static readonly CloudProvider[] All = (CloudProvider[])Enum.GetValues(typeof(CloudProvider));

    public static string Title(CloudProvider p) => p switch
    {
        CloudProvider.Anthropic => "Anthropic", CloudProvider.Gemini => "Google Gemini", CloudProvider.Mistral => "Mistral",
        CloudProvider.OpenAI => "OpenAI", CloudProvider.OpenRouter => "OpenRouter", _ => "Other (OpenAI-compatible)",
    };

    public static string? Base(CloudProvider p) => p switch
    {
        CloudProvider.Anthropic => "https://api.anthropic.com/v1",
        CloudProvider.Gemini => "https://generativelanguage.googleapis.com/v1beta/openai",
        CloudProvider.Mistral => "https://api.mistral.ai/v1",
        CloudProvider.OpenAI => "https://api.openai.com/v1",
        CloudProvider.OpenRouter => "https://openrouter.ai/api/v1",
        _ => null,
    };

    public static string? KeyPage(CloudProvider p) => p switch
    {
        CloudProvider.Anthropic => "https://console.anthropic.com/settings/keys",
        CloudProvider.Gemini => "https://aistudio.google.com/app/apikey",
        CloudProvider.Mistral => "https://console.mistral.ai/api-keys",
        CloudProvider.OpenAI => "https://platform.openai.com/api-keys",
        CloudProvider.OpenRouter => "https://openrouter.ai/settings/keys",
        _ => null,
    };

    /// A typed address: HTTPS, or plain HTTP only on this PC. Null when fine.
    public static string? CheckBase(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Host.Length == 0) return "That address doesn’t look right (it starts with https://).";
        if (u.Scheme == "https") return null;
        if (u.Scheme == "http" && (u.IsLoopback || u.Host == "localhost")) return null;
        return "Use an https:// address. Your key would travel unprotected over plain http.";
    }

    /// Only models that chat. Pure: tested.
    public static List<string> ChatModels(IEnumerable<string> ids)
    {
        string[] skip = { "embed", "tts", "whisper", "dall-e", "moderation", "transcribe", "audio", "image", "realtime", "rerank" };
        return ids.Where(id => id.Length > 0 && !skip.Any(s => id.Contains(s, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// Plain words for an error, naming the service. Pure: tested.
    public static string Problem(string service, int code, string body)
    {
        string? detail = null;
        try { var e = JsonNode.Parse(body)?["error"]; detail = e is JsonValue v ? v.GetValue<string>() : e?["message"]?.GetValue<string>(); } catch { }
        return code switch
        {
            401 or 403 => $"{service} didn’t accept this API key.",
            402 => $"The {service} account needs credit. Add some on its billing page.",
            404 => $"{service} doesn’t know that model or address. Pick another model.",
            429 => "Too many questions at once for this account. Wait a moment and ask again.",
            >= 500 and <= 599 => $"{service} is busy right now. Ask again in a moment.",
            _ => detail != null ? $"{service}: {detail}" : $"{service} answered {code}.",
        };
    }

    static JsonArray Messages(IEnumerable<(string Role, string Text)> history) =>
        new(history.TakeLast(20).Select(h => (JsonNode)new JsonObject { ["role"] = h.Role, ["content"] = h.Text }).ToArray());

    public static JsonObject OpenAIBody(string model, IEnumerable<(string Role, string Text)> history, string system)
    {
        var m = Messages(history);
        m.Insert(0, new JsonObject { ["role"] = "system", ["content"] = system });
        return new() { ["model"] = model, ["messages"] = m, ["stream"] = true };
    }

    public static JsonObject AnthropicBody(string model, IEnumerable<(string Role, string Text)> history, string system) =>
        new() { ["model"] = model, ["max_tokens"] = 16000, ["system"] = system, ["messages"] = Messages(history), ["stream"] = true };

    /// One line of the common streaming reply → the text in it, if any.
    public static string? OpenAIPiece(string line)
    {
        if (!line.StartsWith("data:")) return null;
        var data = line[5..].Trim();
        if (data == "[DONE]") return null;
        try { return JsonNode.Parse(data)?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>(); } catch { return null; }
    }

    /// One line of Anthropic's streaming reply → text, or why it stopped.
    public static (string? Text, string? Stop) AnthropicPiece(string line)
    {
        if (!line.StartsWith("data:")) return (null, null);
        try
        {
            var j = JsonNode.Parse(line[5..].Trim());
            return j?["type"]?.GetValue<string>() switch
            {
                "content_block_delta" => (j["delta"]?["text"]?.GetValue<string>(), null),
                "message_delta" => (null, j["delta"]?["stop_reason"]?.GetValue<string>()),
                _ => (null, null),
            };
        }
        catch { return (null, null); }
    }
}

public sealed class CloudChatException(string message) : Exception(message);

/// The network side. No key is ever written here; the app passes it in.
public static class CloudChat
{
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
    const string AnthropicVersion = "2023-06-01";

    static HttpRequestMessage Request(HttpMethod m, CloudProvider p, string url, string key)
    {
        var r = new HttpRequestMessage(m, url);
        if (p == CloudProvider.Anthropic) { r.Headers.Add("x-api-key", key); r.Headers.Add("anthropic-version", AnthropicVersion); }
        else r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return r;
    }

    /// Checks the key (nothing is spent) and lists the models it can use.
    public static async Task<List<string>> Models(CloudProvider p, string baseUrl, string key)
    {
        var service = CloudProviders.Title(p);
        try
        {
            using var req = Request(HttpMethod.Get, p, baseUrl.TrimEnd('/') + "/models", key);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var res = await http.SendAsync(req, cts.Token);
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) throw new CloudChatException(CloudProviders.Problem(service, (int)res.StatusCode, body));
            var ids = JsonNode.Parse(body)?["data"]?.AsArray().Select(x => x?["id"]?.GetValue<string>() ?? "") ?? Enumerable.Empty<string>();
            return CloudProviders.ChatModels(ids);
        }
        catch (CloudChatException) { throw; }
        catch (Exception) { throw new CloudChatException($"Couldn’t reach {service}. Check the internet connection."); }
    }

    /// Streams the answer; `onText` gets each piece.
    public static async Task Ask(CloudProvider p, string baseUrl, string key, string model,
                                 IEnumerable<(string Role, string Text)> history, string system, Action<string> onText, CancellationToken cancel)
    {
        var service = CloudProviders.Title(p);
        bool anthropic = p == CloudProvider.Anthropic;
        var body = anthropic ? CloudProviders.AnthropicBody(model, history, system) : CloudProviders.OpenAIBody(model, history, system);
        using var req = Request(HttpMethod.Post, p, baseUrl.TrimEnd('/') + (anthropic ? "/messages" : "/chat/completions"), key);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage res;
        try { res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new CloudChatException($"Couldn’t reach {service}. Check the internet connection."); }
        using (res)
        {
            if (!res.IsSuccessStatusCode) throw new CloudChatException(CloudProviders.Problem(service, (int)res.StatusCode, await res.Content.ReadAsStringAsync(cancel)));
            using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(cancel));
            while (await reader.ReadLineAsync(cancel) is { } line)
            {
                if (anthropic)
                {
                    var (text, stop) = CloudProviders.AnthropicPiece(line);
                    if (text != null) onText(text);
                    if (stop == "max_tokens") onText("\n\n(The answer was cut short. Ask it to continue.)");
                }
                else if (CloudProviders.OpenAIPiece(line) is { } piece) onText(piece);
            }
        }
    }
}
