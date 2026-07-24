using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MaxFlowWindows.Core;

public sealed class HttpRequest
{
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string RawUrl { get; set; } = "";
    public Dictionary<string, string> Query { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> PathParams { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Body { get; set; } = "";
    public string ContentType { get; set; } = "";
}

public sealed class RestApiServer : IDisposable
{
    private readonly int _port;
    private readonly List<RouteEntry> _routes = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _task;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public bool IsRunning => _task != null && !_task.IsCompleted;

    public RestApiServer(int port)
    {
        _port = port;
    }

    public void RegisterRoute(string method, string pathPattern, Func<HttpRequest, Task<object?>> handler)
    {
        _routes.Add(new RouteEntry { Method = method, Pattern = pathPattern, Segments = pathPattern.Split('/', StringSplitOptions.RemoveEmptyEntries), Handler = handler });
    }

    public void RegisterRoute(string method, string pathPattern, Func<HttpRequest, object?> handler)
    {
        _routes.Add(new RouteEntry { Method = method, Pattern = pathPattern, Segments = pathPattern.Split('/', StringSplitOptions.RemoveEmptyEntries), Handler = req => Task.FromResult<object?>(handler(req)) });
    }

    public void Start()
    {
        if (_task != null)
            return;
        _cts = new CancellationTokenSource();
        _task = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _listener?.Stop();
        _listener = null;
        _task = null;
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync(ct);
                    await HandleRequestAsync(client, ct);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }
        catch { }
    }

    private async Task HandleRequestAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[8192];
            int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
            if (read == 0) return;

            string raw = Encoding.ASCII.GetString(buffer, 0, read);

            HttpRequest request = ParseHttpRequest(raw);
            RouteEntry? match = MatchRoute(request.Method, request.Path);
            object? responseBody;

            if (match != null)
            {
                request.PathParams = match.ExtractedParams ?? new Dictionary<string, string>();
                responseBody = await match.Handler(request);
            }
            else
            {
                responseBody = new { error = "Not found", method = request.Method, path = request.Path };
            }

            int statusCode = responseBody switch
            {
                null => 204,
                var x when x.GetType().GetProperty("error") != null &&
                    x.GetType().GetProperty("error")?.GetValue(x)?.ToString() != null &&
                    x.GetType().GetProperty("statusCode")?.GetValue(x) is int sc => sc,
                _ => 200
            };

            string statusText = statusCode switch
            {
                200 => "200 OK",
                201 => "201 Created",
                204 => "204 No Content",
                400 => "400 Bad Request",
                404 => "404 Not Found",
                409 => "409 Conflict",
                500 => "500 Internal Server Error",
                _ => $"{statusCode} Unknown"
            };

            string responseJson = responseBody != null
                ? JsonSerializer.Serialize(responseBody, JsonOpts)
                : "";

            byte[] bodyBytes = Encoding.UTF8.GetBytes(responseJson);
            string header = $"HTTP/1.1 {statusText}\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\nAccess-Control-Allow-Origin: *\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);

            await stream.WriteAsync(headerBytes, 0, headerBytes.Length, ct);
            if (bodyBytes.Length > 0)
                await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length, ct);
        }
        catch { }
    }

    private static HttpRequest ParseHttpRequest(string raw)
    {
        var req = new HttpRequest();
        string[] lines = raw.Split(new[] { "\r\n" }, StringSplitOptions.None);
        if (lines.Length == 0) return req;

        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length >= 2)
        {
            req.Method = requestLine[0].ToUpperInvariant();
            req.RawUrl = requestLine[1];
            string[] urlParts = requestLine[1].Split('?');
            req.Path = urlParts[0].TrimEnd('/');

            if (urlParts.Length > 1)
            {
                foreach (string pair in urlParts[1].Split('&'))
                {
                    string[] kv = pair.Split('=');
                    if (kv.Length == 2)
                        req.Query[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
                }
            }
        }

        int bodyStart = raw.IndexOf("\r\n\r\n");
        if (bodyStart > 0)
        {
            string headerSection = raw[..bodyStart];
            foreach (string line in headerSection.Split("\r\n"))
            {
                if (line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
                    req.ContentType = line["Content-Type:".Length..].Trim();
            }
            req.Body = raw[(bodyStart + 4)..];
        }

        return req;
    }

    private RouteEntry? MatchRoute(string method, string path)
    {
        string[] pathSegments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (RouteEntry route in _routes)
        {
            if (!string.Equals(route.Method, method, StringComparison.OrdinalIgnoreCase))
                continue;

            if (route.Segments.Length != pathSegments.Length)
                continue;

            var extracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool match = true;

            for (int i = 0; i < route.Segments.Length; i++)
            {
                if (route.Segments[i].StartsWith('{') && route.Segments[i].EndsWith('}'))
                {
                    string paramName = route.Segments[i].Trim('{', '}');
                    extracted[paramName] = pathSegments[i];
                }
                else if (!string.Equals(route.Segments[i], pathSegments[i], StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                route.ExtractedParams = extracted;
                return route;
            }
        }

        return null;
    }

    private sealed class RouteEntry
    {
        public string Method { get; set; } = "";
        public string Pattern { get; set; } = "";
        public string[] Segments { get; set; } = [];
        public Func<HttpRequest, Task<object?>> Handler { get; set; } = _ => Task.FromResult<object?>(null);
        public Dictionary<string, string>? ExtractedParams { get; set; }
    }
}
