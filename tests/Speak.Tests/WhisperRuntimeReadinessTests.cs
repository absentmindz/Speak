using MaxFlowWindows.Core;
using Xunit;

namespace Speak.Tests;

public sealed class WhisperRuntimeReadinessTests
{
    [Fact]
    public void Inspect_ReportsCompleteBundledWhisperLayoutAsReady()
    {
        using var directory = new TestDirectory();
        string pythonPath = directory.Combine("runtime", "whisper", "python.exe");
        string workerPath = directory.Combine("tools", "speak_worker.py");
        string modelPath = directory.Combine("models", "whisper", "base.pt");
        string ffmpegPath = directory.Combine("tools", "ffmpeg", "bin", "ffmpeg.exe");

        WriteFile(pythonPath, "placeholder python runtime");
        WriteFile(workerPath, "placeholder worker");
        WriteFile(modelPath, "placeholder model");
        WriteFile(ffmpegPath, "placeholder ffmpeg");

        WhisperReadinessReport report = WhisperRuntimeReadiness.Inspect(CreateConfig(
            directory.Path,
            pythonPath,
            workerPath,
            modelPath));

        Assert.True(report.IsReady);
        Assert.All(report.Checks, check => Assert.True(check.IsReady));
        Assert.Equal(Path.GetFullPath(pythonPath), report.Find("python")!.Location, ignoreCase: true);
        Assert.Equal(Path.GetFullPath(workerPath), report.Find("worker")!.Location, ignoreCase: true);
        Assert.Equal(Path.GetFullPath(modelPath), report.Find("model")!.Location, ignoreCase: true);
        Assert.Equal(Path.GetFullPath(ffmpegPath), report.Find("ffmpeg")!.Location, ignoreCase: true);
    }

    [Fact]
    public async Task CheckAsync_BlocksReadinessWhenRequiredRuntimeFilesAreMissing()
    {
        using var directory = new TestDirectory();
        WhisperReadinessReport report = await WhisperRuntimeReadiness.CheckAsync(CreateConfig(
            directory.Path,
            directory.Combine("runtime", "whisper", "python.exe"),
            directory.Combine("tools", "speak_worker.py"),
            directory.Combine("models", "whisper", "base.pt")));

        Assert.False(report.IsReady);
        Assert.False(report.Find("python")!.IsReady);
        Assert.False(report.Find("worker")!.IsReady);
        Assert.False(report.Find("model")!.IsReady);
        Assert.Contains("missing", report.Find("python")!.Detail, StringComparison.OrdinalIgnoreCase);

        WhisperReadinessReport selfTest = await WhisperRuntimeReadiness.RunSelfTestAsync(CreateConfig(
            directory.Path,
            directory.Combine("runtime", "whisper", "python.exe"),
            directory.Combine("tools", "speak_worker.py"),
            directory.Combine("models", "whisper", "base.pt")));
        Assert.False(selfTest.Find("self-test")!.IsReady);
        Assert.Contains("blocked", selfTest.Find("self-test")!.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inspect_RejectsAnEmptyModelFile()
    {
        using var directory = new TestDirectory();
        string pythonPath = directory.Combine("runtime", "whisper", "python.exe");
        string workerPath = directory.Combine("tools", "speak_worker.py");
        string modelPath = directory.Combine("models", "whisper", "base.pt");
        string ffmpegPath = directory.Combine("tools", "ffmpeg", "bin", "ffmpeg.exe");

        WriteFile(pythonPath, "placeholder python runtime");
        WriteFile(workerPath, "placeholder worker");
        WriteFile(modelPath, "");
        WriteFile(ffmpegPath, "placeholder ffmpeg");

        WhisperReadinessReport report = WhisperRuntimeReadiness.Inspect(CreateConfig(
            directory.Path,
            pythonPath,
            workerPath,
            modelPath));

        Assert.False(report.IsReady);
        Assert.False(report.Find("model")!.IsReady);
        Assert.Contains("empty", report.Find("model")!.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static AppConfig CreateConfig(
        string root,
        string pythonPath,
        string workerPath,
        string modelPath)
    {
        return new AppConfig
        {
            Paths = new PathsConfig
            {
                ToolsRoot = root,
                ModelsRoot = Path.Combine(root, "models"),
                WorkspaceRoot = "",
                CacheRoot = Path.Combine(root, "cache")
            },
            Transcription = new TranscriptionConfig
            {
                DefaultEngine = "whisper-local",
                DefaultModel = "whisper-base",
                DefaultDevice = "auto",
                ModelKeepAliveMinutes = 10,
                WhisperPythonPath = pythonPath,
                WhisperWrapperPath = workerPath,
                WhisperModelPath = modelPath,
                WhisperServerScriptPath = Path.Combine(root, "tools", "whisper_resident_server.py")
            }
        };
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
