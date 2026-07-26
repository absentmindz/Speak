using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MaxFlowWindows.Core;

public sealed class BackgroundJanitor : IDisposable
{
	private readonly string _dataRoot;
	private int _recordingRetentionDays;
    private CancellationTokenSource? _cts;
    private Task? _task;

	public BackgroundJanitor(string dataRoot, int recordingRetentionDays)
	{
		_dataRoot = dataRoot;
		UpdateRecordingRetentionDays(recordingRetentionDays);
	}

	public void UpdateRecordingRetentionDays(int recordingRetentionDays)
	{
		Volatile.Write(ref _recordingRetentionDays, Math.Max(0, recordingRetentionDays));
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
			CleanOldLogs();
        }
        catch (Exception ex)
        {
            AppLog.Warn("BackgroundJanitor cleanup failed.", ex);
        }
    }

    private void CleanOldRecordings()
    {
        // Zero is the documented "Keep forever" value.
		int recordingRetentionDays = Volatile.Read(ref _recordingRetentionDays);
		if (recordingRetentionDays == 0)
			return;

        string recordingsDir = Path.Combine(_dataRoot, "recordings");
        if (!Directory.Exists(recordingsDir))
            return;

		DateTime cutoff = DateTime.UtcNow.AddDays(-recordingRetentionDays);
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
