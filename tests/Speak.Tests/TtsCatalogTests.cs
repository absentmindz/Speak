using MaxFlowWindows.Core;
using System.Diagnostics;
using Xunit;

namespace Speak.Tests;

public sealed class TtsCatalogTests
{
    [Fact]
    public void EdgeTtsIsAdditiveAndPreservesExistingDefault()
    {
        var config = new AppConfig();

        Assert.Equal("qwen3-customvoice-1.7b", config.TTS.DefaultEngine);
        Assert.Equal("Aiden", config.TTS.DefaultVoice);

        TtsEngineOption edge = TtsEngineOption.Find("edge-tts");

        Assert.Equal("Microsoft Edge TTS", edge.Name);
        Assert.True(edge.SupportsDirectSpeech);
        Assert.False(edge.RequiresModel);
        Assert.False(edge.UsesLocalResources);
        Assert.True(edge.IsModelReady());
    }

    [Fact]
    public void EdgeTtsRuntimeResolverUsesExplicitPlausibleLegacyExecutable()
    {
        using var directory = new TestDirectory();
        string executable = directory.Combine("external", "edge-tts.exe");
        WritePlausibleExecutable(executable);

        Assert.Equal(
            Path.GetFullPath(executable),
            ResolveIsolated(executable, directory.Path),
            ignoreCase: true);
    }

    [Fact]
    public void VersionedBundledEdgeTtsPythonIsSelectedOnlyWhenModuleExists()
    {
        using var directory = new TestDirectory();
        string root = directory.Combine("Runtime", "EdgeTts", "7.2.8");
        string python = Path.Combine(root, "python.exe");
        WritePlausibleExecutable(python);

        Assert.Equal("", ResolveIsolated("", directory.Path));

        WriteEdgeTtsModule(python);

        Assert.Equal(Path.GetFullPath(python), ResolveIsolated("", directory.Path), ignoreCase: true);
        Assert.True(LocalTtsSynthesizer.UsesBundledEdgeTtsPythonModule(python));
        Assert.True(TtsEngineOption.IsUsableEdgeTtsRuntime(python));
    }

    [Fact]
    public void SharedWhisperPythonIsSelectedOnlyWhenEdgeTtsModuleExists()
    {
        using var directory = new TestDirectory();
        string bundledPython = directory.Combine("Runtime", "EdgeTts", "7.2.8", "python.exe");
        string sharedPython = directory.Combine("Runtime", "Whisper", "python.exe");
        WritePlausibleExecutable(bundledPython);
        WritePlausibleExecutable(sharedPython);

        Assert.Equal("", ResolveIsolated("", directory.Path));

        WriteEdgeTtsModule(sharedPython);

        Assert.Equal(Path.GetFullPath(sharedPython), ResolveIsolated("", directory.Path), ignoreCase: true);
    }

    [Fact]
    public void EdgeTtsRuntimeResolverSkipsConfiguredInvalidFiles()
    {
        using var directory = new TestDirectory();
        string emptyExecutable = directory.Combine("configured-empty", "edge-tts.exe");
        string malformedExecutable = directory.Combine("configured-malformed", "edge-tts.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(emptyExecutable)!);
        Directory.CreateDirectory(Path.GetDirectoryName(malformedExecutable)!);
        File.WriteAllBytes(emptyExecutable, Array.Empty<byte>());
        File.WriteAllBytes(malformedExecutable, new byte[] { 1, 2, 3, 4 });
        string bundledPython = directory.Combine("Runtime", "EdgeTts", "7.2.8", "python.exe");
        WritePlausibleExecutable(bundledPython);
        WriteEdgeTtsModule(bundledPython);

        Assert.False(TtsEngineOption.IsUsableEdgeTtsRuntime(emptyExecutable));
        Assert.False(TtsEngineOption.IsUsableEdgeTtsRuntime(malformedExecutable));
        Assert.False(new TtsEngineOption { Id = "edge-tts", RuntimePath = emptyExecutable }.IsRuntimeReady());
        Assert.False(new TtsEngineOption { Id = "edge-tts", RuntimePath = malformedExecutable }.IsRuntimeReady());
        Assert.Equal(Path.GetFullPath(bundledPython), ResolveIsolated(emptyExecutable, directory.Path), ignoreCase: true);
        Assert.Equal(Path.GetFullPath(bundledPython), ResolveIsolated(malformedExecutable, directory.Path), ignoreCase: true);
    }

    [Fact]
    public void EdgeTtsRuntimeResolverFindsPlausibleLegacyExecutableOnPath()
    {
        using var directory = new TestDirectory();
        string pathDirectory = directory.Combine("path");
        string executable = Path.Combine(pathDirectory, "edge-tts.exe");
        WritePlausibleExecutable(executable);

        string resolved = TtsEngineOption.ResolveEdgeTtsRuntimePath(
            directory.Combine("missing", "edge-tts.exe"),
            directory.Combine("application"),
            pathDirectory,
            directory.Combine("missing-appdata"),
            directory.Combine("missing-localappdata"));

        Assert.Equal(Path.GetFullPath(executable), resolved, ignoreCase: true);
    }

    [Fact]
    public void BundledPythonLaunchUsesIsolatedEdgeTtsModuleAndUtf8NoBytecodeSettings()
    {
        using var directory = new TestDirectory();
        string python = directory.Combine("Runtime", "EdgeTts", "7.2.8", "python.exe");
        WritePlausibleExecutable(python);
        WriteEdgeTtsModule(python);
        string input = directory.Combine("input.txt");
        string output = directory.Combine("output.mp3");

        ProcessStartInfo startInfo = LocalTtsSynthesizer.CreateEdgeTtsStartInfo(
            python,
            directory.Path,
            input,
            "en-US-BrianNeural",
            output);

        Assert.Equal(python, startInfo.FileName);
        Assert.Collection(
            startInfo.ArgumentList.Take(4),
            value => Assert.Equal("-B", value),
            value => Assert.Equal("-I", value),
            value => Assert.Equal("-m", value),
            value => Assert.Equal("edge_tts", value));
        Assert.Equal("--file", startInfo.ArgumentList[4]);
        Assert.Equal(input, startInfo.ArgumentList[5]);
        Assert.Equal("1", startInfo.Environment["PYTHONUTF8"]);
        Assert.Equal("1", startInfo.Environment["PYTHONDONTWRITEBYTECODE"]);
        Assert.Equal("utf-8", startInfo.Environment["PYTHONIOENCODING"]);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Fact]
    public void LegacyExecutableLaunchRemainsDirectWithoutPythonModuleArguments()
    {
        using var directory = new TestDirectory();
        string executable = directory.Combine("external", "edge-tts.exe");
        WritePlausibleExecutable(executable);

        Assert.True(TtsEngineOption.IsUsableEdgeTtsRuntime(executable));
        Assert.True(new TtsEngineOption { Id = "edge-tts", RuntimePath = executable }.IsRuntimeReady());

        ProcessStartInfo startInfo = LocalTtsSynthesizer.CreateEdgeTtsStartInfo(
            executable,
            directory.Path,
            directory.Combine("input.txt"),
            "en-US-AidenNeural",
            directory.Combine("output.mp3"));

        Assert.Equal(executable, startInfo.FileName);
        Assert.Equal("--file", startInfo.ArgumentList[0]);
        Assert.DoesNotContain("-m", startInfo.ArgumentList);
        Assert.DoesNotContain("edge_tts", startInfo.ArgumentList);
    }

    [Fact]
    public void EdgeTtsVoiceCatalogContainsApprovedVoicesWithBrianFirst()
    {
        IReadOnlyList<TtsVoiceOption> voices = TtsVoiceOption.ForEngine("edge-tts");

        Assert.NotEmpty(voices);
        Assert.Equal("en-US-BrianNeural", voices[0].Id);
        Assert.Contains(voices, voice => voice.Id == "en-US-AndrewNeural");
        Assert.Contains(voices, voice => voice.Id == "en-US-AvaNeural");
        Assert.All(voices, voice => Assert.Equal("edge-tts", voice.EngineId));
        Assert.Equal(voices.Count, voices.Select(voice => voice.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static string ResolveIsolated(string configuredPath, string baseDirectory)
    {
        return TtsEngineOption.ResolveEdgeTtsRuntimePath(
            configuredPath,
            baseDirectory,
            "",
            Path.Combine(baseDirectory, "missing-appdata"),
            Path.Combine(baseDirectory, "missing-localappdata"));
    }

    private static void WriteEdgeTtsModule(string pythonPath)
    {
        string runtimeRoot = Path.GetDirectoryName(pythonPath)!;
        string packageRoot = Path.Combine(runtimeRoot, "Lib", "site-packages", "edge_tts");
        Directory.CreateDirectory(packageRoot);
        File.WriteAllText(Path.Combine(packageRoot, "__main__.py"), "");
    }

    private static void WritePlausibleExecutable(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] image = new byte[512];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(image, 0x3C);
        image[0x80] = (byte)'P';
        image[0x81] = (byte)'E';
        BitConverter.GetBytes((ushort)0x8664).CopyTo(image, 0x84);
        BitConverter.GetBytes((ushort)1).CopyTo(image, 0x86);
        BitConverter.GetBytes((ushort)0xF0).CopyTo(image, 0x94);
        BitConverter.GetBytes((ushort)0x0002).CopyTo(image, 0x96);
        File.WriteAllBytes(path, image);
    }
}
