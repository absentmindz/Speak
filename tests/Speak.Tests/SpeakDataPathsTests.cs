using MaxFlowWindows.Core;
using Xunit;

namespace Speak.Tests;

public sealed class SpeakDataPathsTests
{
    [Fact]
    public void ExplicitDataRootDisablesImplicitLegacyMigration()
    {
        using TestDirectory directory = new();
        string? previous = Environment.GetEnvironmentVariable("SPEAK_DATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("SPEAK_DATA_ROOT", @"D:\isolated-speak-data");
            Assert.False(SpeakDataPaths.ShouldMigrateLegacyLocalData(directory.Path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SPEAK_DATA_ROOT", previous);
            SpeakDataPaths.ResetCache();
        }
    }

    [Fact]
    public void ExistingDefaultDataRecordsMigrationCompletionWithoutRefillingFiles()
    {
        using TestDirectory directory = new();
        string? previous = Environment.GetEnvironmentVariable("SPEAK_DATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("SPEAK_DATA_ROOT", null);
            File.WriteAllText(directory.Combine("settings.json"), "{}");

            SpeakDataPaths.CopyLegacyLocalDataIfNeeded(directory.Path);

            Assert.True(File.Exists(
                directory.Combine(SpeakDataPaths.LegacyMigrationMarkerFileName)));
            Assert.False(File.Exists(directory.Combine("history.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SPEAK_DATA_ROOT", previous);
            SpeakDataPaths.ResetCache();
        }
    }

    [Fact]
    public void ClearingHistoryKeepsLegacyMigrationCompletionMarker()
    {
        using TestDirectory directory = new();
        string markerPath = directory.Combine(SpeakDataPaths.LegacyMigrationMarkerFileName);
        string historyPath = directory.Combine("history.json");
        File.WriteAllText(markerPath, "complete");
        File.WriteAllText(historyPath, "[]");
        MaxFlowDataStore store = new(directory.Path);

        store.ClearHistoryData();

        Assert.False(File.Exists(historyPath));
        Assert.True(File.Exists(markerPath));
    }

    [Fact]
    public void DefaultDataRootAllowsOneTimeLegacyMigration()
    {
        using TestDirectory directory = new();
        string? previous = Environment.GetEnvironmentVariable("SPEAK_DATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("SPEAK_DATA_ROOT", null);
            Assert.True(SpeakDataPaths.ShouldMigrateLegacyLocalData(directory.Path));

            File.WriteAllText(
                directory.Combine(SpeakDataPaths.LegacyMigrationMarkerFileName),
                "complete");

            Assert.False(SpeakDataPaths.ShouldMigrateLegacyLocalData(directory.Path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SPEAK_DATA_ROOT", previous);
            SpeakDataPaths.ResetCache();
        }
    }
}
