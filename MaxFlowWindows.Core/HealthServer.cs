using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MaxFlowWindows.Core;

public sealed class HealthServer : IDisposable
{
    private readonly int _port;
    private readonly Func<HealthReport> _reportFactory;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public bool IsRunning => _task != null && !_task.IsCompleted;

    public HealthServer(int port, Func<HealthReport> reportFactory)
    {
        _port = port;
        _reportFactory = reportFactory;
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
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private async Task HandleRequestAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[4096];
            int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);

            if (read == 0)
                return;

            string request = Encoding.ASCII.GetString(buffer, 0, read);

            bool healthRequest = request.StartsWith("GET /health", StringComparison.OrdinalIgnoreCase)
                || request.StartsWith("GET /status", StringComparison.OrdinalIgnoreCase);

            string responseBody = healthRequest
                ? JsonSerializer.Serialize(_reportFactory(), new JsonSerializerOptions { WriteIndented = true })
                : "{\"error\": \"Not found\"}";

            byte[] bodyBytes = Encoding.UTF8.GetBytes(responseBody);
            string statusLine = healthRequest ? "200 OK" : "404 Not Found";
            string contentType = "application/json";

            string header = $"HTTP/1.1 {statusLine}\r\nContent-Type: {contentType}\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\nAccess-Control-Allow-Origin: *\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);

            await stream.WriteAsync(headerBytes, 0, headerBytes.Length, ct);
            await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length, ct);
        }
        catch
        {
        }
    }
}

public sealed class HealthReport
{
    public string Status { get; set; } = "ok";
    public string Version { get; set; } = "";
    public DateTimeOffset Uptime { get; set; }
    public string Timespan { get; set; } = "";
    public bool WhisperServerRunning { get; set; }
    public bool WhisperModelLoaded { get; set; }
    public bool TtsWorkerRunning { get; set; }
    public string AudioInputDevice { get; set; } = "";
    public string SelectedModel { get; set; } = "";
    public string StorageUsedMb { get; set; } = "";
    public int HistoryCount { get; set; }
    public int VocabularyCount { get; set; }
    public string Error { get; set; } = "";
}