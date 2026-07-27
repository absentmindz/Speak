using System.Reflection;
using MaxFlowWindows.Core;
using Xunit;

namespace Speak.Tests;

public sealed class BackgroundJanitorTests
{
    [Fact]
    public void KeepForeverDoesNotDeleteOldRecordings()
    {
        using var directory = new TestDirectory();
        string recordings = directory.Combine("recordings", "nested");
        Directory.CreateDirectory(recordings);
        string oldRecording = System.IO.Path.Combine(recordings, "old.wav");
        File.WriteAllBytes(oldRecording, new byte[] { 1, 2, 3 });
        File.SetLastWriteTimeUtc(oldRecording, DateTime.UtcNow.AddYears(-5));

        using var janitor = new BackgroundJanitor(directory.Path, recordingRetentionDays: 0);

        InvokeRecordingCleanup(janitor);

        Assert.True(
            File.Exists(oldRecording),
            "A zero-day retention setting means Keep forever.");
    }

    [Fact]
    public void PositiveRetentionStillDeletesExpiredRecordings()
    {
        using var directory = new TestDirectory();
        string recordings = directory.Combine("recordings");
        Directory.CreateDirectory(recordings);
        string oldRecording = System.IO.Path.Combine(recordings, "expired.wav");
        string recentRecording = System.IO.Path.Combine(recordings, "recent.wav");
        File.WriteAllBytes(oldRecording, new byte[] { 1 });
        File.WriteAllBytes(recentRecording, new byte[] { 2 });
        File.SetLastWriteTimeUtc(oldRecording, DateTime.UtcNow.AddDays(-31));
        File.SetLastWriteTimeUtc(recentRecording, DateTime.UtcNow);

        using var janitor = new BackgroundJanitor(directory.Path, recordingRetentionDays: 30);

        InvokeRecordingCleanup(janitor);

        Assert.False(File.Exists(oldRecording));
        Assert.True(File.Exists(recentRecording));
    }

    [Fact]
    public void RetentionCanBeChangedToKeepForeverWithoutRestarting()
    {
        using var directory = new TestDirectory();
        string recordings = directory.Combine("recordings");
        Directory.CreateDirectory(recordings);
        string oldRecording = System.IO.Path.Combine(recordings, "old.wav");
        File.WriteAllBytes(oldRecording, new byte[] { 1 });
        File.SetLastWriteTimeUtc(oldRecording, DateTime.UtcNow.AddYears(-1));
        using var janitor = new BackgroundJanitor(
            directory.Path,
            recordingRetentionDays: 7);

        janitor.UpdateRecordingRetentionDays(0);
        InvokeRecordingCleanup(janitor);

        Assert.True(File.Exists(oldRecording));
    }

    [Fact]
    public void RetentionCanBeEnabledWithoutRestarting()
    {
        using var directory = new TestDirectory();
        string recordings = directory.Combine("recordings", "nested");
        Directory.CreateDirectory(recordings);
        string oldRecording = System.IO.Path.Combine(recordings, "old.wav");
        File.WriteAllBytes(oldRecording, new byte[] { 1 });
        File.SetLastWriteTimeUtc(oldRecording, DateTime.UtcNow.AddYears(-1));
        using var janitor = new BackgroundJanitor(
            directory.Path,
            recordingRetentionDays: 0);

        janitor.UpdateRecordingRetentionDays(7);
        InvokeRecordingCleanup(janitor);

        Assert.False(File.Exists(oldRecording));
    }

    [Fact]
    public void CleanupNeverDeletesGeneratedTtsOutputs()
    {
        using var directory = new TestDirectory();
        string ttsOutput = directory.Combine("tts", "outputs", "speech.wav");
        string cloneOutput = directory.Combine(
            "tts",
            "clone-outputs",
            "clone.wav");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ttsOutput)!);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cloneOutput)!);
        File.WriteAllBytes(ttsOutput, new byte[] { 1 });
        File.WriteAllBytes(cloneOutput, new byte[] { 2 });
        File.SetLastWriteTimeUtc(ttsOutput, DateTime.UtcNow.AddYears(-1));
        File.SetLastWriteTimeUtc(cloneOutput, DateTime.UtcNow.AddYears(-1));
        using var janitor = new BackgroundJanitor(
            directory.Path,
            recordingRetentionDays: 7);

        MethodInfo method = typeof(BackgroundJanitor).GetMethod(
            "Cleanup",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(BackgroundJanitor).FullName,
                "Cleanup");
        method.Invoke(janitor, parameters: null);

        Assert.True(File.Exists(ttsOutput));
        Assert.True(File.Exists(cloneOutput));
    }

    private static void InvokeRecordingCleanup(BackgroundJanitor janitor)
    {
        MethodInfo method = typeof(BackgroundJanitor).GetMethod(
            "CleanOldRecordings",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                typeof(BackgroundJanitor).FullName,
                "CleanOldRecordings");

        method.Invoke(janitor, parameters: null);
    }
}
