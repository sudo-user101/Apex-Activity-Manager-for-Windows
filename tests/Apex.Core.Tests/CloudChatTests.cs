using System.Net;
using System.Net.Sockets;
using System.Text;
using Apex.Core;

namespace Apex.Core.Tests;

public class CloudChatTests
{
    [Fact] public void ProvidersHaveTheirAddresses()
    {
        foreach (var p in CloudProviders.All.Where(p => p != CloudProvider.Custom))
        {
            Assert.StartsWith("https://", CloudProviders.Base(p));
            Assert.StartsWith("https://", CloudProviders.KeyPage(p));
        }
        Assert.Null(CloudProviders.Base(CloudProvider.Custom));
    }

    [Fact] public void TypedAddressesMustProtectTheKey()
    {
        Assert.Null(CloudProviders.CheckBase("https://api.example.com/v1"));
        Assert.Null(CloudProviders.CheckBase("http://127.0.0.1:8080/v1"));
        Assert.Null(CloudProviders.CheckBase("http://localhost:1234/v1"));
        Assert.NotNull(CloudProviders.CheckBase("http://api.example.com/v1"));
        Assert.NotNull(CloudProviders.CheckBase("not an address"));
        Assert.NotNull(CloudProviders.CheckBase("ftp://example.com"));
    }

    [Fact] public void OnlyChatModels()
    {
        Assert.Equal(new[] { "gpt-5", "claude-sonnet" }, CloudProviders.ChatModels(new[] { "gpt-5", "text-embedding-3", "whisper-1", "claude-sonnet", "dall-e-3", "" }));
    }

    [Fact] public void ProblemsInPlainWords()
    {
        Assert.Equal("OpenAI didn’t accept this API key.", CloudProviders.Problem("OpenAI", 401, "{}"));
        Assert.Contains("needs credit", CloudProviders.Problem("Mistral", 402, ""));
        Assert.Equal("Gemini: model gone", CloudProviders.Problem("Gemini", 400, "{\"error\":{\"message\":\"model gone\"}}"));
    }

    [Fact] public void RequestBodies()
    {
        var h = new[] { ("user", "hi") };
        var o = CloudProviders.OpenAIBody("m", h, "sys");
        Assert.Equal("system", o["messages"]![0]!["role"]!.GetValue<string>());
        Assert.True(o["stream"]!.GetValue<bool>());
        var a = CloudProviders.AnthropicBody("m", h, "sys");
        Assert.Equal("sys", a["system"]!.GetValue<string>());
        Assert.Equal("user", a["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal(16000, a["max_tokens"]!.GetValue<int>());
    }

    [Fact] public void StreamPieces()
    {
        Assert.Equal("Hel", CloudProviders.OpenAIPiece("data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}"));
        Assert.Null(CloudProviders.OpenAIPiece("data: [DONE]"));
        Assert.Null(CloudProviders.OpenAIPiece(": keep-alive"));
        Assert.Equal(("lo", null), CloudProviders.AnthropicPiece("data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"lo\"}}"));
        Assert.Equal((null, "max_tokens"), CloudProviders.AnthropicPiece("data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"max_tokens\"}}"));
    }

    /// The real HTTP code against a tiny local stand-in server (loopback only).
    [Fact] public async Task AsksAndStreamsFromAStandInServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string? seenAuth = null;
        var server = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                using var c = await listener.AcceptTcpClientAsync();
                using var s = c.GetStream();
                var buf = new byte[65536]; var n = await s.ReadAsync(buf);
                var req = Encoding.UTF8.GetString(buf, 0, n);
                seenAuth ??= req.Split("\r\n").FirstOrDefault(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase));
                var body = req.StartsWith("GET")
                    ? "{\"data\":[{\"id\":\"model-a\"},{\"id\":\"text-embedding\"}]}"
                    : "data: {\"choices\":[{\"delta\":{\"content\":\"Hello \"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"there\"}}]}\n\ndata: [DONE]\n\n";
                var head = $"HTTP/1.1 200 OK\r\nContent-Type: {(req.StartsWith("GET") ? "application/json" : "text/event-stream")}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n";
                await s.WriteAsync(Encoding.UTF8.GetBytes(head + body));
            }
        });
        var b = $"http://127.0.0.1:{port}/v1";
        var models = await CloudChat.Models(CloudProvider.Custom, b, "test-key");
        Assert.Equal(new[] { "model-a" }, models);
        var said = new StringBuilder();
        await CloudChat.Ask(CloudProvider.Custom, b, "test-key", "model-a", new[] { ("user", "hi") }, "sys", t => said.Append(t), CancellationToken.None);
        Assert.Equal("Hello there", said.ToString());
        Assert.Equal("Authorization: Bearer test-key", seenAuth);
        await server; listener.Stop();
    }
}
