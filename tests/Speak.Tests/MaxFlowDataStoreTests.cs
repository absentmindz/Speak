using System.Text;
using MaxFlowWindows.Core;
using Xunit;

namespace Speak.Tests;

public sealed class MaxFlowDataStoreTests
{
    [Fact]
    public void SaveHistoryAtomicallyPromotesPreviousVersionToBackup()
    {
        using var directory = new TestDirectory();
        var store = new MaxFlowDataStore(directory.Path);
        TranscriptCard first = Card("first");
        TranscriptCard second = Card("second");

        store.SaveHistory(new[] { first });
        store.SaveHistory(new[] { second });

        string historyPath = directory.Combine("history.json");
        Assert.Equal("second", Assert.Single(store.LoadHistory()).FormattedText);
        Assert.True(File.Exists(historyPath + ".bak1"));
        Assert.Contains(
            "\"FormattedText\": \"first\"",
            File.ReadAllText(historyPath + ".bak1", Encoding.UTF8));
        Assert.True(File.Exists(historyPath + ".schema"));
        Assert.Empty(
            Directory.EnumerateFiles(
                directory.Path,
                "history.json.*.tmp",
                SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void LoadHistoryRecoversFromNewestValidBackupWhenPrimaryIsCorrupt()
    {
        using var directory = new TestDirectory();
        using var dataRoot = new SpeakDataRootScope(directory.Combine("app-data"));
        var store = new MaxFlowDataStore(directory.Path);
        store.SaveHistory(new[] { Card("first") });
        store.SaveHistory(new[] { Card("second") });
        store.SaveHistory(new[] { Card("third") });
        string historyPath = directory.Combine("history.json");
        File.WriteAllText(historyPath, "{not valid JSON", Encoding.UTF8);

        List<TranscriptCard> recovered = store.LoadHistory();

        Assert.Equal("second", Assert.Single(recovered).FormattedText);
    }

    [Fact]
    public void PurgeSettingsRecoveryCopiesPreservesOnlySanitizedPrimary()
    {
        using var directory = new TestDirectory();
        var store = new MaxFlowDataStore(directory.Path);
        var original = MaxFlowSettings.Default;
        original.SttCloudApiKeyEnvironmentVariable =
            "raw-secret-placeholder-that-must-be-removed";
        store.SaveSettings(original);
        var sanitized = MaxFlowSettings.Default;
        sanitized.SttCloudApiKeyEnvironmentVariable = "GROQ_API_KEY";
        store.SaveSettings(sanitized);
        string settingsPath = directory.Combine("settings.json");
        File.WriteAllText(settingsPath + ".tmp", "raw-secret-placeholder");
        File.WriteAllText(
            settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp",
            "raw-secret-placeholder");

        store.PurgeSettingsRecoveryCopies();

        Assert.True(File.Exists(settingsPath));
        Assert.Equal(
            "GROQ_API_KEY",
            store.LoadSettings().SttCloudApiKeyEnvironmentVariable);
        Assert.Empty(Directory.EnumerateFiles(
            directory.Path,
            "settings.json*.tmp",
            SearchOption.TopDirectoryOnly));
        Assert.Empty(Directory.EnumerateFiles(
            directory.Path,
            "settings.json.bak*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void ClearHistoryErasesPrimaryBackupsSchemaTempsAndReferencedInternalAudio()
    {
        using var directory = new TestDirectory();
        var store = new MaxFlowDataStore(directory.Path);
        string recording = CreateAudio(directory.Combine("recordings", "day", "capture.wav"));
        string archivedCopy = CreateAudio(
            directory.Combine("recordings-archive", "older", "capture.wav"));
        string unrelatedRecording = CreateAudio(
            directory.Combine("recordings", "keep.wav"));

        using var outsideDirectory = new TestDirectory();
        string outsideAudio = CreateAudio(outsideDirectory.Combine("capture.wav"));

        store.SaveHistory(new[] { Card("one", recording) });
        store.SaveHistory(new[] { Card("two", recording) });
        store.SaveHistory(new[] { Card("three", recording) });

        string historyPath = directory.Combine("history.json");
        File.WriteAllText(historyPath + ".tmp", "legacy temp", Encoding.UTF8);
        File.WriteAllText(
            historyPath + "." + Guid.NewGuid().ToString("N") + ".tmp",
            "interrupted write",
            Encoding.UTF8);

        store.ClearHistoryData(new[] { recording, outsideAudio });

        Assert.Empty(
            Directory.EnumerateFiles(
                directory.Path,
                "history.json*",
                SearchOption.TopDirectoryOnly));
        Assert.False(File.Exists(recording));
        Assert.True(
            File.Exists(archivedCopy),
            "Only the exact audio path referenced by history may be erased.");
        Assert.True(File.Exists(unrelatedRecording));
        Assert.True(
            File.Exists(outsideAudio),
            "History erasure must never delete an audio path outside the data root.");
        Assert.Empty(store.LoadHistory());
    }

    [Fact]
    public void ClearHistoryRejectsSiblingPrefixAndExternalNameCollisions()
    {
        using var directory = new TestDirectory();
        var store = new MaxFlowDataStore(directory.Path);
        string sibling = CreateAudio(
            directory.Combine("recordings-evil", "collision.wav"));
        string internalCollision = CreateAudio(
            directory.Combine("recordings", "collision.wav"));
        string traversal = System.IO.Path.Combine(
            directory.Path,
            "recordings",
            "..",
            "recordings-evil",
            "collision.wav");

        store.ClearHistoryData(new[] { sibling, traversal });

        Assert.True(File.Exists(sibling));
        Assert.True(File.Exists(internalCollision));
    }

    private static TranscriptCard Card(string text, string audioPath = "")
    {
        return new TranscriptCard
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            RawText = text,
            FormattedText = text,
            AudioPath = audioPath
        };
    }

    private static string CreateAudio(string path)
    {
        Directory.CreateDirectory(
            System.IO.Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Audio fixture needs a parent directory."));
        File.WriteAllBytes(path, new byte[] { 0x52, 0x49, 0x46, 0x46 });
        return path;
    }
}
