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

            SpeakDataPaths.CopyLegacyLocalDataIfNeeded(directory.Path);

            Assert.False(File.Exists(Path.Combine(
                directory.Path,
                SpeakDataPaths.LegacyMigrationMarkerFileName)));
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
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

            SpeakDataPaths.CopyLegacyLocalDataIfNeeded(
                directory.Path,
                Array.Empty<string>(),
                Array.Empty<string>(),
                explicitDataRoot: false);

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

    [Fact]
    public void ModelDerivedFormerRootIsImportedBeforeFixedDriveFallback()
    {
        using TestDirectory directory = new();
        string modelsRoot = directory.Combine("model-drive", "models");
        string modelDerivedRoot = directory.Combine(
            "model-drive", "OpenClawData", "Speak");
        string fixedDriveRoot = directory.Combine("fixed-drive", "Speak");
        string destination = directory.Combine("destination");
        Directory.CreateDirectory(modelsRoot);
        Directory.CreateDirectory(modelDerivedRoot);
        Directory.CreateDirectory(fixedDriveRoot);
        Directory.CreateDirectory(Path.Combine(destination, "recordings"));
        Directory.CreateDirectory(Path.Combine(destination, "logs"));
        File.WriteAllText(
            Path.Combine(destination, "settings.json"),
            "{\"source\":\"stale-destination\"}");
        File.WriteAllBytes(
            Path.Combine(destination, "recordings", "sample.wav"),
            new byte[] { 0, 0, 0, 0 });
        File.WriteAllText(
            Path.Combine(destination, "logs", "speak.log"),
            "stale log");
        File.WriteAllText(
            Path.Combine(modelDerivedRoot, "settings.json"),
            "{\"source\":\"model-derived\"}");
        Directory.CreateDirectory(Path.Combine(modelDerivedRoot, "recordings"));
        Directory.CreateDirectory(Path.Combine(modelDerivedRoot, "logs"));
        File.WriteAllBytes(
            Path.Combine(modelDerivedRoot, "recordings", "sample.wav"),
            new byte[] { 82, 73, 70, 70 });
        File.WriteAllText(
            Path.Combine(modelDerivedRoot, "logs", "speak.log"),
            "synthetic log");
        File.WriteAllText(
            Path.Combine(fixedDriveRoot, "settings.json"),
            "{\"source\":\"fixed-drive\"}");
        File.WriteAllText(
            Path.Combine(fixedDriveRoot, "history.json"),
            "[{\"source\":\"fixed-drive\"}]");

        IReadOnlyList<string> formerRoots =
            SpeakDataPaths.BuildFormerOpenClawDataRoots(
                modelsRoot,
                fixedDriveRoot);
        SpeakDataPaths.CopyLegacyLocalDataIfNeeded(
            destination,
            formerRoots,
            Array.Empty<string>(),
            explicitDataRoot: false);

        Assert.Equal(modelDerivedRoot, formerRoots[0]);
        Assert.Equal(fixedDriveRoot, formerRoots[1]);
        Assert.Contains(
            "model-derived",
            File.ReadAllText(Path.Combine(destination, "settings.json")));
        Assert.False(File.Exists(Path.Combine(destination, "history.json")));
        Assert.Equal(
            new byte[] { 82, 73, 70, 70 },
            File.ReadAllBytes(Path.Combine(
                destination, "recordings", "sample.wav")));
        Assert.Equal(
            "synthetic log",
            File.ReadAllText(Path.Combine(destination, "logs", "speak.log")));
        Assert.True(File.Exists(Path.Combine(
            destination,
            SpeakDataPaths.LegacyMigrationMarkerFileName)));
    }

    [Fact]
    public void FixedDriveFormerRootIsImportedWhenModelDerivedRootIsMissing()
    {
        using TestDirectory directory = new();
        string modelsRoot = directory.Combine("model-drive", "models");
        string fixedDriveRoot = directory.Combine("fixed-drive", "Speak");
        string destination = directory.Combine("destination");
        Directory.CreateDirectory(modelsRoot);
        Directory.CreateDirectory(fixedDriveRoot);
        File.WriteAllText(
            Path.Combine(fixedDriveRoot, "history.json"),
            "[{\"source\":\"fixed-drive\"}]");

        SpeakDataPaths.CopyLegacyLocalDataIfNeeded(
            destination,
            SpeakDataPaths.BuildFormerOpenClawDataRoots(
                modelsRoot,
                fixedDriveRoot),
            Array.Empty<string>(),
            explicitDataRoot: false);

        Assert.Contains(
            "fixed-drive",
            File.ReadAllText(Path.Combine(destination, "history.json")));
        Assert.True(File.Exists(Path.Combine(
            destination,
            SpeakDataPaths.LegacyMigrationMarkerFileName)));
    }

    [Fact]
    public void PreviousMigrationMarkerIsPromotedWithoutReimportingClearedData()
    {
        using TestDirectory directory = new();
        string formerRoot = directory.Combine("former");
        string fallbackRoot = directory.Combine("fallback");
        string destination = directory.Combine("destination");
        Directory.CreateDirectory(formerRoot);
        Directory.CreateDirectory(fallbackRoot);
        Directory.CreateDirectory(destination);
        File.WriteAllText(
            Path.Combine(formerRoot, "history.json"),
            "[{\"text\":\"former private history\"}]");
        File.WriteAllText(
            Path.Combine(fallbackRoot, "history.json"),
            "[{\"text\":\"fallback private history\"}]");
        File.WriteAllText(
            Path.Combine(
                destination,
                SpeakDataPaths.PreviousLegacyMigrationMarkerFileName),
            "complete");
        Assert.False(SpeakDataPaths.ShouldMigrateLegacyLocalData(destination));

        SpeakDataPaths.CopyLegacyLocalDataIfNeeded(
            destination,
            new[] { formerRoot },
            new[] { fallbackRoot },
            explicitDataRoot: false);

        Assert.False(File.Exists(Path.Combine(destination, "history.json")));
        Assert.True(File.Exists(Path.Combine(
            destination,
            SpeakDataPaths.LegacyMigrationMarkerFileName)));
    }

    [Fact]
    public void CompletedMigrationDoesNotResurrectFormerHistoryAfterClear()
    {
        using TestDirectory directory = new();
        string formerRoot = directory.Combine("former");
        string destination = directory.Combine("destination");
        Directory.CreateDirectory(formerRoot);
        Directory.CreateDirectory(destination);
        File.WriteAllText(
            Path.Combine(formerRoot, "history.json"),
            "[{\"text\":\"old private history\"}]");
        File.WriteAllText(
            Path.Combine(destination, SpeakDataPaths.LegacyMigrationMarkerFileName),
            "complete");

        SpeakDataPaths.CopyLegacyLocalDataIfNeeded(
            destination,
            new[] { formerRoot },
            Array.Empty<string>(),
            explicitDataRoot: false);

        Assert.False(File.Exists(Path.Combine(destination, "history.json")));
    }
}
