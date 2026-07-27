using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MaxFlowWindows.Core;
using Xunit;

namespace Speak.Tests;

public sealed class RestApiServerTests
{
    private static readonly string Token = new('t', 32);

    [Fact]
    public async Task RouteRequiresBearerToken()
    {
        int port = ReserveLoopbackPort();
        using var server = CreateServer(port);
        server.Start();
        using var client = CreateClient(port);

        using HttpResponseMessage unauthorized = await client.GetAsync("ping");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Token);
        using HttpResponseMessage authorized = await client.GetAsync("ping");

        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        using JsonDocument body = JsonDocument.Parse(
            await authorized.Content.ReadAsStringAsync());
        Assert.Equal("pong", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OriginMustBeExplicitlyAllowedAndNeverUsesWildcardCors()
    {
        const string allowedOrigin = "https://trusted.example.test";
        int port = ReserveLoopbackPort();
        using var server = CreateServer(port, allowedOrigin);
        server.Start();
        using var client = CreateClient(port);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Token);

        using var forbiddenRequest = new HttpRequestMessage(HttpMethod.Get, "ping");
        forbiddenRequest.Headers.Add("Origin", "https://attacker.example.test");
        using HttpResponseMessage forbidden = await client.SendAsync(forbiddenRequest);

        using var allowedRequest = new HttpRequestMessage(HttpMethod.Get, "ping");
        allowedRequest.Headers.Add("Origin", allowedOrigin);
        using HttpResponseMessage allowed = await client.SendAsync(allowedRequest);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.False(forbidden.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(
            allowedOrigin,
            Assert.Single(allowed.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.DoesNotContain(
            "*",
            allowed.Headers.GetValues("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task AllowedOriginCanCompleteStrictAuthorizationPreflight()
    {
        const string allowedOrigin = "https://trusted.example.test";
        int port = ReserveLoopbackPort();
        using var server = CreateServer(port, allowedOrigin);
        server.Start();
        using var client = CreateClient(port);
        using var request = new HttpRequestMessage(HttpMethod.Options, "ping");
        request.Headers.Add("Origin", allowedOrigin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add(
            "Access-Control-Request-Headers",
            "Authorization, Content-Type");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            allowedOrigin,
            Assert.Single(response.Headers.GetValues(
                "Access-Control-Allow-Origin")));
        Assert.Equal(
            "GET",
            Assert.Single(response.Headers.GetValues(
                "Access-Control-Allow-Methods")));
        Assert.Contains(
            "Authorization",
            Assert.Single(response.Headers.GetValues(
                "Access-Control-Allow-Headers")));
    }

    [Fact]
    public async Task PreflightRejectsUnapprovedHeaders()
    {
        const string allowedOrigin = "https://trusted.example.test";
        int port = ReserveLoopbackPort();
        using var server = CreateServer(port, allowedOrigin);
        server.Start();
        using var client = CreateClient(port);
        using var request = new HttpRequestMessage(HttpMethod.Options, "ping");
        request.Headers.Add("Origin", allowedOrigin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "X-Unsafe");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public void AllowedOriginMustBeAnOriginNotAUrlWithAPath()
    {
        Assert.Throws<ArgumentException>(() => new RestApiServer(
            ReserveLoopbackPort(),
            Token,
            new[] { "https://trusted.example.test/application" }));
    }

    [Fact]
    public async Task StopCancelsAndDrainsSlowConnections()
    {
        int port = ReserveLoopbackPort();
        using var server = CreateServer(port);
        server.Start();
        var clients = new List<TcpClient>();
        try
        {
            for (int index = 0; index < 8; index++)
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                clients.Add(client);
                byte[] partialRequest = Encoding.ASCII.GetBytes(
                    "GET /ping HTTP/1.1\r\nHost: 127.0.0.1\r\n");
                await client.GetStream().WriteAsync(partialRequest);
            }

            await Task.Delay(100);
            await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            foreach (TcpClient client in clients)
                client.Dispose();
        }
    }

    [Fact]
    public async Task RequestBodyOverOneMegabyteIsRejectedBeforeHandlerRuns()
    {
        int port = ReserveLoopbackPort();
        bool handlerInvoked = false;
        using var server = new RestApiServer(port, Token);
        server.RegisterRoute("POST", "/upload", _ =>
        {
            handlerInvoked = true;
            return new { accepted = true };
        });
        server.Start();

        string response = await SendRawRequestAsync(
            port,
            "POST /upload HTTP/1.1\r\n" +
            "Host: 127.0.0.1\r\n" +
            $"Authorization: Bearer {Token}\r\n" +
            "Content-Type: application/json\r\n" +
            "Content-Length: 1048577\r\n" +
            "Connection: close\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 413 ", response, StringComparison.Ordinal);
        Assert.False(handlerInvoked);
    }

    [Fact]
    public async Task Utf8RequestBodyIsReadUsingDeclaredByteLength()
    {
        int port = ReserveLoopbackPort();
        using var server = new RestApiServer(port, Token);
        server.RegisterRoute("POST", "/echo", request => new { request.Body });
        server.Start();
        using var client = CreateClient(port);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", Token);
        using var content = new StringContent(
            "{\"text\":\"مرحبا 🌍\"}",
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await client.PostAsync("echo", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "{\"text\":\"مرحبا 🌍\"}",
            document.RootElement.GetProperty("body").GetString());
    }

    private static RestApiServer CreateServer(
        int port,
        params string[] allowedOrigins)
    {
        var server = new RestApiServer(port, Token, allowedOrigins);
        server.RegisterRoute("GET", "/ping", _ => new { message = "pong" });
        return server;
    }

    private static HttpClient CreateClient(int port)
    {
        return new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<string> SendRawRequestAsync(int port, string request)
    {
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        using NetworkStream stream = client.GetStream();
        byte[] requestBytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(requestBytes, timeout.Token);
        await stream.FlushAsync(timeout.Token);

        using var response = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, timeout.Token);
            if (read == 0)
            {
                break;
            }

            response.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(response.ToArray());
    }
}
