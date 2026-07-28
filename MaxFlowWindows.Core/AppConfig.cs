using System;
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
                QwenTtsCustomVoiceModelPath = PortablePathResolver.ExpandPath(config.TTS.QwenTtsCustomVoiceModelPath, modelsRoot),
                QwenTtsBaseModelPath = PortablePathResolver.ExpandPath(config.TTS.QwenTtsBaseModelPath, modelsRoot),
                QwenTtsVoiceDesignModelPath = PortablePathResolver.ExpandPath(config.TTS.QwenTtsVoiceDesignModelPath, modelsRoot),
                TortoiseModelDir = PortablePathResolver.ExpandPath(config.TTS.TortoiseModelDir, modelsRoot)
            },
            CloudSTT = config.CloudSTT,
            LLMPolish = config.LLMPolish,
            Recording = config.Recording,
            UI = config.UI,
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
    public string DefaultModel { get; init; } = "whisper-large-v3";
    public string DefaultDevice { get; init; } = "cuda";
    public int ModelKeepAliveMinutes { get; init; } = 10;
    public string WhisperPythonPath { get; init; } = "";
    public string WhisperWrapperPath { get; init; } = "";
    public string WhisperModelPath { get; init; } = "{ModelsRoot}\\whisper\\large-v3.pt";
    public string WhisperServerScriptPath { get; init; } = "";
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
    public string QwenTtsCustomVoiceModelPath { get; init; } = "{ModelsRoot}\\Qwen3-TTS-12Hz-1.7B-CustomVoice";
    public string QwenTtsBaseModelPath { get; init; } = "{ModelsRoot}\\Qwen3-TTS-12Hz-1.7B-Base";
    public string QwenTtsVoiceDesignModelPath { get; init; } = "";
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

public sealed class LoggingConfig
{
    public string Level { get; init; } = "Information";
    public string File { get; init; } = "logs/speak-.log";
}
