using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace MaxFlowWindows.Core;

public sealed class AppConfig
{
    public PathsConfig Paths { get; init; } = new();
    public TranscriptionConfig Transcription { get; init; } = new();
    public TtsConfig TTS { get; init; } = new();
    public CloudSttConfig CloudSTT { get; init; } = new();
    public LlmPolishConfig LLMPolish { get; init; } = new();
    public RecordingConfig Recording { get; init; } = new();
    public UiConfig UI { get; init; } = new();
    public CommercialConfig Commercial { get; init; } = new();
    public LoggingConfig Logging { get; init; } = new();

    private static AppConfig? _instance;
    private static readonly object _loadLock = new();

    public static AppConfig Current
    {
        get
        {
            if (_instance == null)
            {
                lock (_loadLock)
                {
                    _instance ??= Load();
                }
            }
            return _instance;
        }
    }

    public static AppConfig Load(string? basePath = null)
    {
        string configurationRoot = System.IO.Path.GetFullPath(
            string.IsNullOrWhiteSpace(basePath)
                ? AppContext.BaseDirectory
                : basePath);

        var builder = new ConfigurationBuilder()
            .SetBasePath(configurationRoot)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

        var config = builder.Build();
        var loadedConfig = new AppConfig();
        config.Bind(loadedConfig);
        AppConfig appConfig = NormalizePaths(loadedConfig);
        lock (_loadLock)
        {
            _instance = appConfig;
        }
        return appConfig;
    }

    private static AppConfig NormalizePaths(AppConfig config)
    {
        string modelsRoot = PortablePathResolver.ResolveModelsRoot(config.Paths.ModelsRoot);
        string toolsRoot = PortablePathResolver.ExpandPath(config.Paths.ToolsRoot, modelsRoot);
        if (string.IsNullOrWhiteSpace(toolsRoot))
        {
            toolsRoot = AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            System.Diagnostics.Debug.WriteLine($"[Speak.Config] ToolsRoot=\"{config.Paths.ToolsRoot}\" failed to resolve; falling back to app directory: {toolsRoot}");
        }

        string cacheRoot = PortablePathResolver.ExpandPath(config.Paths.CacheRoot, modelsRoot);
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            cacheRoot = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Speak", "cache");
            System.Diagnostics.Debug.WriteLine($"[Speak.Config] CacheRoot=\"{config.Paths.CacheRoot}\" failed to resolve — falling back to LocalAppData: {cacheRoot}");
        }

        return new AppConfig
        {
            Paths = new PathsConfig
            {
                ToolsRoot = toolsRoot,
                ModelsRoot = modelsRoot,
                WorkspaceRoot = PortablePathResolver.ExpandPath(config.Paths.WorkspaceRoot, modelsRoot),
                CacheRoot = cacheRoot
            },
            Transcription = new TranscriptionConfig
            {
                DefaultEngine = config.Transcription.DefaultEngine,
                DefaultModel = config.Transcription.DefaultModel,
                DefaultDevice = config.Transcription.DefaultDevice,
                ModelKeepAliveMinutes = config.Transcription.ModelKeepAliveMinutes,
                WhisperPythonPath = PortablePathResolver.ExpandPath(config.Transcription.WhisperPythonPath, modelsRoot),
                WhisperWrapperPath = PortablePathResolver.ExpandPath(config.Transcription.WhisperWrapperPath, modelsRoot),
                WhisperModelPath = PortablePathResolver.ExpandPath(config.Transcription.WhisperModelPath, modelsRoot),
                WhisperServerScriptPath = PortablePathResolver.ExpandPath(config.Transcription.WhisperServerScriptPath, modelsRoot)
            },
            TTS = new TtsConfig
            {
                DefaultEngine = config.TTS.DefaultEngine,
                DefaultVoice = config.TTS.DefaultVoice,
                DefaultLanguage = config.TTS.DefaultLanguage,
                OutputRoot = PortablePathResolver.ExpandPath(config.TTS.OutputRoot, modelsRoot),
                ChatterboxPythonPath = PortablePathResolver.ExpandPath(config.TTS.ChatterboxPythonPath, modelsRoot),
                ComfyUIPythonPath = PortablePathResolver.ExpandPath(config.TTS.ComfyUIPythonPath, modelsRoot),
                TortoisePythonPath = PortablePathResolver.ExpandPath(config.TTS.TortoisePythonPath, modelsRoot),
                EdgeTtsExecutablePath = PortablePathResolver.ExpandPath(config.TTS.EdgeTtsExecutablePath, modelsRoot),
                QwenTtsCustomVoiceModelPath = PortablePathResolver.ExpandPath(config.TTS.QwenTtsCustomVoiceModelPath, modelsRoot),
                QwenTtsBaseModelPath = PortablePathResolver.ExpandPath(config.TTS.QwenTtsBaseModelPath, modelsRoot),
                QwenTtsVoiceDesignModelPath = PortablePathResolver.ExpandPath(config.TTS.QwenTtsVoiceDesignModelPath, modelsRoot),
                HiggsTtsModelPath = PortablePathResolver.ExpandPath(config.TTS.HiggsTtsModelPath, modelsRoot),
                HiggsPythonPath = PortablePathResolver.ExpandPath(config.TTS.HiggsPythonPath, modelsRoot),
                TortoiseModelDir = PortablePathResolver.ExpandPath(config.TTS.TortoiseModelDir, modelsRoot)
            },
            CloudSTT = config.CloudSTT,
            LLMPolish = config.LLMPolish,
            Recording = config.Recording,
            UI = config.UI,
            Commercial = config.Commercial,
            Logging = config.Logging
        };
    }
}

public sealed class PathsConfig
{
    public string ToolsRoot { get; init; } = "{AppDir}";
    public string ModelsRoot { get; init; } = "";
    public string WorkspaceRoot { get; init; } = "";
    public string CacheRoot { get; init; } = "{LocalAppData}\\Speak\\cache";
}

public sealed class TranscriptionConfig
{
    public string DefaultEngine { get; init; } = "whisper-local";
    public string DefaultModel { get; init; } = "whisper-base";
    public string DefaultDevice { get; init; } = "auto";
    public int ModelKeepAliveMinutes { get; init; } = 10;
    public string WhisperPythonPath { get; init; } = "{AppDir}\\runtime\\whisper\\python.exe";
    public string WhisperWrapperPath { get; init; } = "{AppDir}\\tools\\speak_worker.py";
    public string WhisperModelPath { get; init; } = "{ModelsRoot}\\whisper\\base.pt";
    public string WhisperServerScriptPath { get; init; } = "{AppDir}\\tools\\whisper_resident_server.py";
}

public sealed class TtsConfig
{
    public string DefaultEngine { get; init; } = "qwen3-customvoice-1.7b";
    public string DefaultVoice { get; init; } = "Aiden";
    public string DefaultLanguage { get; init; } = "English";
    public string OutputRoot { get; init; } = "";
    public string ChatterboxPythonPath { get; init; } = "";
    public string ComfyUIPythonPath { get; init; } = "";
    public string TortoisePythonPath { get; init; } = "";
    public string EdgeTtsExecutablePath { get; init; } = "";
    public string QwenTtsCustomVoiceModelPath { get; init; } = "{ModelsRoot}\\Qwen3-TTS-12Hz-1.7B-CustomVoice";
    public string QwenTtsBaseModelPath { get; init; } = "{ModelsRoot}\\Qwen3-TTS-12Hz-1.7B-Base";
    public string QwenTtsVoiceDesignModelPath { get; init; } = "";
    public string HiggsTtsModelPath { get; init; } = "{ModelsRoot}\\higgs-tts-3-4b";
    public string HiggsPythonPath { get; init; } = "";
    public string TortoiseModelDir { get; init; } = "{ModelsRoot}\\tortoise-tts\\models";
}

public sealed class CloudSttConfig
{
    public string DefaultProvider { get; init; } = "groq";
    public string DefaultEndpoint { get; init; } = "https://api.groq.com/openai/v1";
    public string DefaultModel { get; init; } = "whisper-large-v3-turbo";
    public string DefaultApiKeyEnvVar { get; init; } = "GROQ_API_KEY";
}

public sealed class LlmPolishConfig
{
    public string DefaultProvider { get; init; } = "off";
    public string DefaultEndpoint { get; init; } = "";
    public string DefaultModel { get; init; } = "";
    public string DefaultApiKeyEnvVar { get; init; } = "";
    public int DefaultTimeoutSeconds { get; init; } = 12;
}

public sealed class RecordingConfig
{
    public int RetentionDays { get; init; } = 30;
}

public sealed class UiConfig
{
    public string Theme { get; init; } = "dark";
    public bool KeepHistory { get; init; } = true;
    public bool ShowCompletionToast { get; init; } = true;
    public bool AutoLearnCorrections { get; init; }
    public bool ShowShortcutWidget { get; init; } = true;
    public bool MinimizeToTray { get; init; } = true;
    public bool StartWithWindows { get; init; }
    public string DictationShortcut { get; init; } = "Ctrl+Win";
}

public sealed class CommercialConfig
{
    public string WebsiteUrl { get; init; } = "https://absentmindz.github.io/Speak/";
    public string PricingUrl { get; init; } = "https://absentmindz.github.io/Speak/#pricing";
    public string CheckoutUrl { get; init; } = "https://absentmindz.github.io/Speak/#founding";
    public string SupportUrl { get; init; } = "https://github.com/absentmindz/Speak/issues";
    public string LicensePortalUrl { get; init; } = "";
}

public sealed class LoggingConfig
{
    public string Level { get; init; } = "Information";
    public string File { get; init; } = "logs/speak-.log";
}

public sealed record WhisperReadinessCheck(
    string Id,
    string Label,
    bool IsReady,
    string Detail,
    string Location);

public sealed class WhisperReadinessReport
{
    private readonly IReadOnlyList<WhisperReadinessCheck> _checks;

    public WhisperReadinessReport(IEnumerable<WhisperReadinessCheck> checks)
    {
        _checks = checks.ToArray();
    }

    public IReadOnlyList<WhisperReadinessCheck> Checks => _checks;

    public bool IsReady => _checks.Count > 0 && _checks.All(check => check.IsReady);

    public WhisperReadinessCheck? Find(string id)
    {
        return _checks.FirstOrDefault(check => check.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public WhisperReadinessReport With(WhisperReadinessCheck replacement)
    {
        bool replaced = false;
        var checks = new List<WhisperReadinessCheck>(_checks.Count + 1);
        foreach (WhisperReadinessCheck check in _checks)
        {
            if (check.Id.Equals(replacement.Id, StringComparison.OrdinalIgnoreCase))
            {
                checks.Add(replacement);
                replaced = true;
            }
            else
            {
                checks.Add(check);
            }
        }

        if (!replaced)
        {
            checks.Add(replacement);
        }

        return new WhisperReadinessReport(checks);
    }

    public string ToDisplayText()
    {
        return string.Join(
            Environment.NewLine,
            _checks.Select(check => $"{(check.IsReady ? "Ready" : "Needs attention")}: {check.Label} — {check.Detail}"));
    }
}

/// <summary>
/// Performs the same local dependency checks that Speak needs before it can
/// start its resident Whisper worker. It deliberately does not download
/// anything or fall back to a cloud provider.
/// </summary>
public static class WhisperRuntimeReadiness
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WorkerStartupTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ModelLoadTimeout = TimeSpan.FromSeconds(120);
    private static readonly string[] PythonVersionArguments = ["--version"];
    private static readonly string[] FfmpegVersionArguments = ["-version"];

    public static WhisperReadinessReport Inspect(AppConfig? config = null)
    {
        config ??= AppConfig.Current;
        RuntimePaths paths = ResolvePaths(config);

        bool pythonExists = File.Exists(paths.PythonPath);
        bool workerExists = File.Exists(paths.WorkerPath);
        bool modelExists = HasNonEmptyFile(paths.ModelPath);
        bool ffmpegExists = File.Exists(paths.FfmpegPath);

        return new WhisperReadinessReport(new[]
        {
            new WhisperReadinessCheck(
                "python",
                "Python runtime",
                pythonExists,
                pythonExists ? "runtime file found" : "runtime file is missing",
                paths.PythonPath),
            new WhisperReadinessCheck(
                "worker",
                "Whisper worker",
                workerExists,
                workerExists ? "worker script found" : "worker script is missing",
                paths.WorkerPath),
            new WhisperReadinessCheck(
                "model",
                "Whisper base model",
                modelExists,
                modelExists ? "model file found" : "model file is missing or empty",
                paths.ModelPath),
            new WhisperReadinessCheck(
                "ffmpeg",
                "FFmpeg",
                ffmpegExists,
                ffmpegExists ? "ffmpeg.exe found" : "ffmpeg.exe was not found in Speak or PATH",
                paths.FfmpegPath)
        });
    }

    /// <summary>
    /// Runs lightweight executable probes. This verifies that the configured
    /// Python and FFmpeg files can actually start, not merely that their paths
    /// exist. The worker probe exercises its real command-line entry point
    /// without loading a model.
    /// </summary>
    public static async Task<WhisperReadinessReport> CheckAsync(
        AppConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        config ??= AppConfig.Current;
        RuntimePaths paths = ResolvePaths(config);
        WhisperReadinessReport report = Inspect(config);

        WhisperReadinessCheck? python = report.Find("python");
        if (python?.IsReady == true)
        {
            ProcessProbeResult probe = await RunProcessAsync(
                paths.PythonPath,
                PythonVersionArguments,
                ProbeTimeout,
                cancellationToken);
            report = report.With(new WhisperReadinessCheck(
                "python",
                python.Label,
                probe.Success,
                probe.Success ? DescribeProbe("Python responded", probe) : DescribeProbe("Python did not start", probe),
                python.Location));
        }

        WhisperReadinessCheck? worker = report.Find("worker");
        WhisperReadinessCheck? verifiedPython = report.Find("python");
        if (worker?.IsReady == true && verifiedPython?.IsReady == true)
        {
            var workerArguments = new List<string> { paths.WorkerPath };
            if (paths.UsesUnifiedWorker)
            {
                workerArguments.Add("whisper");
            }
            workerArguments.Add("--help");

            ProcessProbeResult probe = await RunProcessAsync(
                paths.PythonPath,
                workerArguments,
                ProbeTimeout,
                cancellationToken);
            report = report.With(new WhisperReadinessCheck(
                "worker",
                worker.Label,
                probe.Success,
                probe.Success ? "worker command responded" : DescribeProbe("worker command failed", probe),
                worker.Location));
        }
        else if (worker?.IsReady == true)
        {
            report = report.With(new WhisperReadinessCheck(
                "worker",
                worker.Label,
                false,
                "not started because the Python runtime is not usable",
                worker.Location));
        }

        WhisperReadinessCheck? ffmpeg = report.Find("ffmpeg");
        if (ffmpeg?.IsReady == true)
        {
            ProcessProbeResult probe = await RunProcessAsync(
                paths.FfmpegPath,
                FfmpegVersionArguments,
                ProbeTimeout,
                cancellationToken);
            report = report.With(new WhisperReadinessCheck(
                "ffmpeg",
                ffmpeg.Label,
                probe.Success,
                probe.Success ? DescribeProbe("FFmpeg responded", probe) : DescribeProbe("FFmpeg did not start", probe),
                ffmpeg.Location));
        }

        return report;
    }

    /// <summary>
    /// Starts Speak's installed worker on a short-lived authenticated loopback
    /// port, confirms its health endpoint, and loads the configured base model.
    /// It performs no recording and sends no data off the device.
    /// </summary>
    public static async Task<WhisperReadinessReport> RunSelfTestAsync(
        AppConfig? config = null,
        CancellationToken cancellationToken = default)
    {
        config ??= AppConfig.Current;
        WhisperReadinessReport report = await CheckAsync(config, cancellationToken);
        if (!report.IsReady)
        {
            return report.With(new WhisperReadinessCheck(
                "self-test",
                "Local worker self-test",
                false,
                "blocked until Python, the worker, the base model, and FFmpeg are ready",
                ""));
        }

        RuntimePaths paths = ResolvePaths(config);
        ProcessProbeResult selfTest = await RunWorkerSelfTestAsync(paths, config, cancellationToken);
        return report.With(new WhisperReadinessCheck(
            "self-test",
            "Local worker self-test",
            selfTest.Success,
            selfTest.Success
                ? "worker health endpoint responded and the base model loaded"
                : DescribeProbe("worker self-test failed", selfTest),
            paths.WorkerPath));
    }

    public static string ResolveFfmpegPath(AppConfig? config = null)
    {
        config ??= AppConfig.Current;
        return ResolvePaths(config).FfmpegPath;
    }

    private static RuntimePaths ResolvePaths(AppConfig config)
    {
        string modelsRoot = ResolveModelsRoot(config);
        string pythonPath = ResolveConfiguredPath(config.Transcription.WhisperPythonPath, modelsRoot);
        string workerPath = ResolveWorkerPath(config, modelsRoot, out bool usesUnifiedWorker);
        string modelPath = ResolveConfiguredPath(config.Transcription.WhisperModelPath, modelsRoot);
        string ffmpegPath = ResolveFfmpegPath(config, modelsRoot, pythonPath);
        string cacheRoot = ResolveConfiguredPath(config.Paths.CacheRoot, modelsRoot);

        return new RuntimePaths(
            pythonPath,
            workerPath,
            usesUnifiedWorker,
            modelPath,
            System.IO.Path.GetDirectoryName(modelPath) ?? "",
            ffmpegPath,
            cacheRoot);
    }

    private static string ResolveModelsRoot(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Paths.ModelsRoot))
        {
            return PortablePathResolver.ExpandPath(config.Paths.ModelsRoot);
        }

        return PortablePathResolver.ResolveModelsRoot("");
    }

    private static string ResolveConfiguredPath(string value, string modelsRoot)
    {
        return PortablePathResolver.ExpandPath(value, modelsRoot);
    }

    private static string ResolveWorkerPath(AppConfig config, string modelsRoot, out bool usesUnifiedWorker)
    {
        string configuredWorker = ResolveConfiguredPath(config.Transcription.WhisperWrapperPath, modelsRoot);
        if (!string.IsNullOrWhiteSpace(configuredWorker))
        {
            usesUnifiedWorker = IsUnifiedWorker(configuredWorker);
            return configuredWorker;
        }

        string configuredServer = ResolveConfiguredPath(config.Transcription.WhisperServerScriptPath, modelsRoot);
        if (!string.IsNullOrWhiteSpace(configuredServer))
        {
            usesUnifiedWorker = IsUnifiedWorker(configuredServer);
            return configuredServer;
        }

        string unifiedWorker = System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "speak_worker.py");
        if (File.Exists(unifiedWorker))
        {
            usesUnifiedWorker = true;
            return unifiedWorker;
        }

        usesUnifiedWorker = false;
        return System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "whisper_resident_server.py");
    }

    private static bool IsUnifiedWorker(string workerPath)
    {
        return System.IO.Path.GetFileName(workerPath)
            .Equals("speak_worker.py", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveFfmpegPath(AppConfig config, string modelsRoot, string pythonPath)
    {
        foreach (string candidate in FfmpegCandidates(config, modelsRoot, pythonPath))
        {
            string executable = FfmpegExecutableFromCandidate(candidate);
            if (!string.IsNullOrWhiteSpace(executable))
            {
                return executable;
            }
        }

        return "";
    }

    private static IEnumerable<string> FfmpegCandidates(AppConfig config, string modelsRoot, string pythonPath)
    {
        string configuredFfmpegDirectory = Environment.GetEnvironmentVariable("SPEAK_FFMPEG_BIN") ?? "";
        if (!string.IsNullOrWhiteSpace(configuredFfmpegDirectory))
        {
            yield return ResolveConfiguredPath(configuredFfmpegDirectory, modelsRoot);
        }

        string? pythonDirectory = System.IO.Path.GetDirectoryName(pythonPath);
        if (!string.IsNullOrWhiteSpace(pythonDirectory))
        {
            yield return pythonDirectory;
        }

        yield return System.IO.Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin");
        yield return System.IO.Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg", "bin");
        yield return System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "tools", "ffmpeg", "bin"));

        string toolsRoot = ResolveConfiguredPath(config.Paths.ToolsRoot, modelsRoot);
        if (!string.IsNullOrWhiteSpace(toolsRoot))
        {
            yield return System.IO.Path.Combine(toolsRoot, "tools", "ffmpeg", "bin");
            yield return System.IO.Path.Combine(toolsRoot, "ffmpeg", "bin");
        }

        string systemRoot = System.IO.Path.GetPathRoot(Environment.SystemDirectory) ?? "";
        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            yield return System.IO.Path.Combine(systemRoot, "ffmpeg", "bin");
        }

        foreach (string pathEntry in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return pathEntry.Trim();
        }
    }

    private static string FfmpegExecutableFromCandidate(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return "";
        }

        try
        {
            if (System.IO.File.Exists(candidate)
                && System.IO.Path.GetFileName(candidate).Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
            {
                return System.IO.Path.GetFullPath(candidate);
            }

            string executable = System.IO.Path.Combine(candidate, "ffmpeg.exe");
            return System.IO.File.Exists(executable)
                ? System.IO.Path.GetFullPath(executable)
                : "";
        }
        catch
        {
            return "";
        }
    }

    private static async Task<ProcessProbeResult> RunWorkerSelfTestAsync(
        RuntimePaths paths,
        AppConfig config,
        CancellationToken cancellationToken)
    {
        int port = ReserveLoopbackPort();
        string authToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var client = CreateLoopbackClient(authToken, ModelLoadTimeout);
        using var process = new Process
        {
            StartInfo = CreateWorkerStartInfo(paths, config, port, authToken),
            EnableRaisingEvents = true
        };
        bool started = false;

        try
        {
            started = process.Start();
            if (!started)
            {
                return ProcessProbeResult.Failed("could not start the worker process");
            }

            bool healthy = await WaitForWorkerHealthAsync(client, port, process, cancellationToken);
            if (!healthy)
            {
                return ProcessProbeResult.Failed(process.HasExited
                    ? "worker exited before its health endpoint responded"
                    : "worker health endpoint did not respond in time");
            }

            string modelArgument = ResolveModelArgument(config, paths.ModelPath);
            using var content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model = modelArgument,
                    modelDir = paths.ModelDirectory,
                    device = config.Transcription.DefaultDevice,
                    keepAliveMinutes = 1
                }),
                Encoding.UTF8,
                "application/json");
            using HttpResponseMessage response = await client.PostAsync(
                $"http://127.0.0.1:{port}/load",
                content,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ProcessProbeResult.Failed($"worker returned HTTP {(int)response.StatusCode} while loading the base model");
            }

            return ProcessProbeResult.Passed("authenticated health check and model load succeeded");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ProcessProbeResult.Failed(TrimDetail(exception.Message));
        }
        finally
        {
            if (started)
            {
                await StopWorkerAsync(client, port, process);
            }
        }
    }

    private static ProcessStartInfo CreateWorkerStartInfo(
        RuntimePaths paths,
        AppConfig config,
        int port,
        string authToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = paths.PythonPath,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(paths.WorkerPath);
        if (paths.UsesUnifiedWorker)
        {
            startInfo.ArgumentList.Add("whisper");
        }
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--idle-minutes");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--model-dir");
        startInfo.ArgumentList.Add(paths.ModelDirectory);

        LocalTtsSynthesizer.SanitizeChildProcessEnvironment(startInfo);
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["SPEAK_WORKER_TOKEN"] = authToken;
        if (!string.IsNullOrWhiteSpace(paths.CacheRoot))
        {
            startInfo.Environment["XDG_CACHE_HOME"] = paths.CacheRoot;
        }

        string? ffmpegDirectory = System.IO.Path.GetDirectoryName(paths.FfmpegPath);
        if (!string.IsNullOrWhiteSpace(ffmpegDirectory))
        {
            string inheritedPath = startInfo.Environment.TryGetValue("PATH", out string? existingPath)
                ? existingPath
                : Environment.GetEnvironmentVariable("PATH") ?? "";
            startInfo.Environment["PATH"] = ffmpegDirectory + System.IO.Path.PathSeparator + inheritedPath;
        }

        return startInfo;
    }

    private static HttpClient CreateLoopbackClient(string authToken, TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false
        };
        var client = new HttpClient(handler)
        {
            Timeout = timeout
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
        return client;
    }

    private static async Task<bool> WaitForWorkerHealthAsync(
        HttpClient client,
        int port,
        Process process,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + WorkerStartupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                return false;
            }

            try
            {
                using HttpResponseMessage response = await client.GetAsync(
                    $"http://127.0.0.1:{port}/health",
                    cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    if (document.RootElement.TryGetProperty("ok", out JsonElement ok)
                        && ok.ValueKind == JsonValueKind.True)
                    {
                        return true;
                    }
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(200, cancellationToken);
        }

        return false;
    }

    private static async Task StopWorkerAsync(HttpClient client, int port, Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                using var content = new StringContent("{}", Encoding.UTF8, "application/json");
                using HttpResponseMessage ignored = await client.PostAsync(
                    $"http://127.0.0.1:{port}/stop",
                    content,
                    CancellationToken.None);
            }
        }
        catch
        {
        }

        try
        {
            if (!process.HasExited)
            {
                Task waitForExit = process.WaitForExitAsync();
                if (await Task.WhenAny(waitForExit, Task.Delay(TimeSpan.FromSeconds(3))) != waitForExit)
                {
                    TryKill(process);
                }
                else
                {
                    await waitForExit;
                }
            }
        }
        catch
        {
            TryKill(process);
        }
    }

    private static async Task<ProcessProbeResult> RunProcessAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return ProcessProbeResult.Failed("process did not start");
            }

            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            Task waitForExit = process.WaitForExitAsync(cancellationToken);
            if (await Task.WhenAny(waitForExit, Task.Delay(timeout, CancellationToken.None)) != waitForExit)
            {
                TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None);
                return ProcessProbeResult.Failed("timed out");
            }

            await waitForExit;
            string detail = FirstNonEmpty((await standardOutput).Trim(), (await standardError).Trim());
            return process.ExitCode == 0
                ? ProcessProbeResult.Passed(TrimDetail(detail))
                : ProcessProbeResult.Failed(FirstNonEmpty(TrimDetail(detail), $"exited with code {process.ExitCode}"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (Exception exception)
        {
            return ProcessProbeResult.Failed(TrimDetail(exception.Message));
        }
    }

    private static string ResolveModelArgument(AppConfig config, string modelPath)
    {
        string model = config.Transcription.DefaultModel.Trim();
        if (model.StartsWith("whisper-", StringComparison.OrdinalIgnoreCase))
        {
            model = model["whisper-".Length..];
        }

        return string.IsNullOrWhiteSpace(model)
            ? System.IO.Path.GetFileNameWithoutExtension(modelPath)
            : model;
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool HasNonEmptyFile(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path)
                && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static string DescribeProbe(string prefix, ProcessProbeResult result)
    {
        return string.IsNullOrWhiteSpace(result.Detail)
            ? prefix
            : prefix + ": " + result.Detail;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }

    private static string TrimDetail(string value)
    {
        string flattened = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return flattened.Length <= 240
            ? flattened
            : flattened[..237] + "...";
    }

    private sealed record RuntimePaths(
        string PythonPath,
        string WorkerPath,
        bool UsesUnifiedWorker,
        string ModelPath,
        string ModelDirectory,
        string FfmpegPath,
        string CacheRoot);

    private sealed record ProcessProbeResult(bool Success, string Detail)
    {
        public static ProcessProbeResult Passed(string detail) => new(true, detail);

        public static ProcessProbeResult Failed(string detail) => new(false, detail);
    }
}
