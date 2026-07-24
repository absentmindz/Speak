using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MaxFlowWindows.Core;

public sealed class BackgroundJanitor : IDisposable
{
    private readonly string _dataRoot;
    private readonly int _recordingRetentionDays;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public BackgroundJanitor(string dataRoot, int recordingRetentionDays)
    {
        _dataRoot = dataRoot;
        _recordingRetentionDays = Math.Max(1, recordingRetentionDays);
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
        _task = null;
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Initial cleanup after short delay
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            Cleanup();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // Periodic cleanup every hour
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(1), ct);
                Cleanup();
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Cleanup()
    {
        try
        {
            CleanOldRecordings();
            CleanTempFiles("tts", "outputs");
            CleanTempFiles("tts", "clone-outputs");
            CleanOldLogs();
        }
        catch (Exception ex)
        {
            AppLog.Warn("BackgroundJanitor cleanup failed.", ex);
        }
    }

    private void CleanOldRecordings()
    {
        string recordingsDir = Path.Combine(_dataRoot, "recordings");
        if (!Directory.Exists(recordingsDir))
            return;

        DateTime cutoff = DateTime.UtcNow.AddDays(-_recordingRetentionDays);
        foreach (string file in Directory.EnumerateFiles(recordingsDir, "*.wav", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
            catch
            {
            }
        }
    }

    private void CleanTempFiles(params string[] subPath)
    {
        string dir = Path.Combine(_dataRoot, Path.Combine(subPath));
        if (!Directory.Exists(dir))
            return;

        DateTime cutoff = DateTime.UtcNow.AddDays(-7);
        foreach (string file in Directory.EnumerateFiles(dir, "*.wav", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
            catch
            {
            }
        }

        foreach (string sub in Directory.EnumerateDirectories(dir))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(sub) < cutoff && !Directory.EnumerateFileSystemEntries(sub).Any())
                    Directory.Delete(sub);
            }
            catch
            {
            }
        }
    }

    private void CleanOldLogs()
    {
        string logsDir = Path.Combine(_dataRoot, "logs");
        if (!Directory.Exists(logsDir))
            return;

        DateTime cutoff = DateTime.UtcNow.AddDays(-90);
        foreach (string file in Directory.EnumerateFiles(logsDir, "*.log", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }
            catch
            {
            }
        }
    }
}