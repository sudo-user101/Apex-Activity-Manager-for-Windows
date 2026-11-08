using System.Net.Http.Headers;
using System.Security.Cryptography;
using Apex.Core;

namespace Apex.Services;

/// The network side of VirusTotal. The key lives in Credential Manager.
internal static class VirusTotalClient
{
    const string Target = "Apex Activity Manager: VirusTotal";
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public static string? Key => LumenAccount.Read(Target);
    public static bool SaveKey(string key) => key.Trim().Length > 0 && LumenAccount.Write(key.Trim(), Target);
    public static void RemoveKey() => LumenAccount.Delete(Target);

    public static async Task<string?> Sha256(string path)
    {
        try
        {
            await using var f = File.OpenRead(path);
            return Convert.ToHexString(await SHA256.HashDataAsync(f)).ToLowerInvariant();
        }
        catch { return null; }
    }

    public enum Outcome { Found, Unknown, StillScanning, Problem }

    /// Unknown: VirusTotal has never seen this file.
    public static async Task<(Outcome, VirusTotal.Report?, string?)> Lookup(string sha, string key)
    {
        try
        {
            using var res = await Send(HttpMethod.Get, VirusTotal.Base + "/files/" + sha, key);
            if ((int)res.StatusCode == 404) return (Outcome.Unknown, null, null);
            if (!res.IsSuccessStatusCode) return (Outcome.Problem, null, VirusTotal.Problem((int)res.StatusCode));
            var r = VirusTotal.FromLookup(await res.Content.ReadAsStringAsync());
            return r == null ? (Outcome.Problem, null, VirusTotal.Problem(200)) : (Outcome.Found, r, null);
        }
        catch { return (Outcome.Problem, null, "Couldn’t reach VirusTotal. Check your internet connection."); }
    }

    /// Uploads, then waits for the scan (every 20 s, inside the free key's 4 a minute).
    public static async Task<(Outcome, VirusTotal.Report?, string?)> Scan(string path, string key, CancellationToken cancel)
    {
        try
        {
            var size = new FileInfo(path).Length;
            if (size > VirusTotal.UploadLimit) return (Outcome.Problem, null, "This file is too big for VirusTotal. The limit is 650 MB.");
            var address = VirusTotal.Base + "/files";
            if (size > VirusTotal.DirectLimit)
            {
                using var u = await Send(HttpMethod.Get, VirusTotal.Base + "/files/upload_url", key);
                if (!u.IsSuccessStatusCode) return (Outcome.Problem, null, VirusTotal.Problem((int)u.StatusCode));
                var given = System.Text.Json.Nodes.JsonNode.Parse(await u.Content.ReadAsStringAsync())?["data"]?.GetValue<string>();
                if (!VirusTotal.IsSafeUploadAddress(given)) return (Outcome.Problem, null, VirusTotal.Problem(0));
                address = given!;
            }
            await using var file = File.OpenRead(path);
            using var form = new MultipartFormDataContent { { new StreamContent(file), "file", Path.GetFileName(path) } };
            using var post = new HttpRequestMessage(HttpMethod.Post, address) { Content = form };
            post.Headers.Add("x-apikey", key);
            using var sent = await http.SendAsync(post, cancel);
            if (!sent.IsSuccessStatusCode) return (Outcome.Problem, null, VirusTotal.Problem((int)sent.StatusCode));
            var id = VirusTotal.IdFrom(await sent.Content.ReadAsStringAsync());
            if (id == null) return (Outcome.Problem, null, VirusTotal.Problem(200));
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), cancel);
                using var a = await Send(HttpMethod.Get, VirusTotal.Base + "/analyses/" + Uri.EscapeDataString(id), key);
                if ((int)a.StatusCode == 429) continue;
                if (!a.IsSuccessStatusCode) return (Outcome.Problem, null, VirusTotal.Problem((int)a.StatusCode));
                if (VirusTotal.FromAnalysis(await a.Content.ReadAsStringAsync()) is { } r) return (Outcome.Found, r, null);
            }
            return (Outcome.StillScanning, null, null);
        }
        catch (OperationCanceledException) { return (Outcome.Problem, null, "Stopped."); }
        catch (IOException) { return (Outcome.Problem, null, "Apex can’t read this file, so it can’t be sent."); }
        catch { return (Outcome.Problem, null, "Couldn’t reach VirusTotal. Check your internet connection."); }
    }

    static Task<HttpResponseMessage> Send(HttpMethod m, string url, string key)
    {
        var req = new HttpRequestMessage(m, url);
        req.Headers.Add("x-apikey", key);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http.SendAsync(req);
    }
}
