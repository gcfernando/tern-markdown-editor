using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tern.Desktop.Services;

namespace Tern.Desktop.Tests;

public sealed class AILoopbackIntegrationTests
{
    [Theory]
    [InlineData("chat-completions")]
    [InlineData("ollama-chat")]
    public async Task Reviewed_message_round_trips_through_real_loopback_transport(string providerId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var provider = AIProviders.Get(providerId);
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = connection.GetStream();
            var header = new StringBuilder();
            var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                Assert.True(header.Length < 16384);
                await stream.ReadExactlyAsync(one, deadline.Token);
                header.Append((char)one[0]);
            }
            var length = int.Parse(header.ToString().Split("\r\n").Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);
            var body = new byte[length];
            await stream.ReadExactlyAsync(body, deadline.Token);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("Reviewed loopback text café 😀", json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.Contains("Authorization: Bearer artificial-test-key", header.ToString(), StringComparison.OrdinalIgnoreCase);
            var response = Encoding.UTF8.GetBytes(providerId == "chat-completions"
                ? "{\"choices\":[{\"message\":{\"content\":\"local protocol verified\"}}]}"
                : "{\"message\":{\"content\":\"local protocol verified\"},\"done\":true}");
            var responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(responseHeader, deadline.Token);
            await stream.WriteAsync(response, deadline.Token);
        }, deadline.Token);
        using var client = new AIConnectionClient();
        var answer = await client.SendAsync(new Uri($"http://127.0.0.1:{port}/chat"), "test-model", "artificial-test-key", "Reviewed loopback text café 😀", provider, deadline.Token);
        Assert.Equal("local protocol verified", answer);
        await server;
    }
}
