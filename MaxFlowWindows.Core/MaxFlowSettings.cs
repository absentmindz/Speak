using System.IO;

namespace MaxFlowWindows.Core;

public sealed class MaxFlowSettings
{
	private static readonly AppConfig _config = LoadConfig();

	private static AppConfig LoadConfig()
	{
		try
		{
			return AppConfig.Load();
		}
		catch
		{
			return new AppConfig();
		}
	}

	// Older appsettings files can contain empty Whisper paths. Only those empty
	// values fall back to the current portable layout; non-empty legacy/user
	// configuration is retained for upgrades.
	private static string ModelsRoot => string.IsNullOrWhiteSpace(_config.Paths.ModelsRoot)
		? PortablePathResolver.ResolveModelsRoot("")
		: _config.Paths.ModelsRoot;

	private static string DefaultTranscriptionModelId => FirstNonEmpty(
		_config.Transcription.DefaultModel,
		"whisper-base");

	private static string DefaultWhisperDeviceId => FirstNonEmpty(
		_config.Transcription.DefaultDevice,
		"auto");

	private static string DefaultWhisperPythonPath => FirstNonEmpty(
		_config.Transcription.WhisperPythonPath,
		PortablePathResolver.ExpandPath("{AppDir}\\runtime\\whisper\\python.exe", ModelsRoot));

	private static string DefaultWhisperWrapperPath => FirstNonEmpty(
		_config.Transcription.WhisperWrapperPath,
		PortablePathResolver.ExpandPath("{AppDir}\\tools\\speak_worker.py", ModelsRoot));

	private static string DefaultWhisperModelPath => FirstNonEmpty(
		_config.Transcription.WhisperModelPath,
		Path.Combine(ModelsRoot, "whisper", "base.pt"));

	private static string FirstNonEmpty(params string[] values)
	{
		foreach (string value in values)
		{
			if (!string.IsNullOrWhiteSpace(value))
			{
				return value;
			}
		}

		return "";
	}

	public string LocaleId { get; set; } = "en-US";

	public string EngineId { get; set; } = "whisper-local";

	public string TranscriptionModelId { get; set; } = DefaultTranscriptionModelId;

	public string WhisperDeviceId { get; set; } = DefaultWhisperDeviceId;

	public int ModelKeepAliveMinutes { get; set; } = _config.Transcription.ModelKeepAliveMinutes;

	public string WhisperPythonPath { get; set; } = DefaultWhisperPythonPath;

	public string WhisperWrapperPath { get; set; } = DefaultWhisperWrapperPath;

	public string WhisperModelPath { get; set; } = DefaultWhisperModelPath;

	public int AudioInputDeviceNumber { get; set; }

	public string ThemeId { get; set; } = _config.UI.Theme;

	public bool KeepHistory { get; set; } = _config.UI.KeepHistory;

	public string DictationShortcut { get; set; } = _config.UI.DictationShortcut;

	public bool ShowShortcutWidget { get; set; } = _config.UI.ShowShortcutWidget;

	public bool MinimizeToTray { get; set; } = _config.UI.MinimizeToTray;

	public bool StartWithWindows { get; set; } = _config.UI.StartWithWindows;

	public string OutputDestinationId { get; set; } = "paste";

	public string LlmPolishProviderId { get; set; } = _config.LLMPolish.DefaultProvider;

	public string LlmPolishEndpoint { get; set; } = _config.LLMPolish.DefaultEndpoint;

	public string LlmPolishModel { get; set; } = _config.LLMPolish.DefaultModel;

	public string LlmPolishApiKeyEnvironmentVariable { get; set; } = _config.LLMPolish.DefaultApiKeyEnvVar;

	public int LlmPolishTimeoutSeconds { get; set; } = _config.LLMPolish.DefaultTimeoutSeconds;

	public string SttCloudProviderId { get; set; } = _config.CloudSTT.DefaultProvider;

	public string SttCloudEndpoint { get; set; } = _config.CloudSTT.DefaultEndpoint;

	public string SttCloudModel { get; set; } = _config.CloudSTT.DefaultModel;

	public string SttCloudApiKeyEnvironmentVariable { get; set; } = _config.CloudSTT.DefaultApiKeyEnvVar;

	public int RecordingRetentionDays { get; set; } = _config.Recording.RetentionDays;

	public bool ShowCompletionToast { get; set; } = _config.UI.ShowCompletionToast;

	public bool AutoLearnCorrections { get; set; } = _config.UI.AutoLearnCorrections;

	public string TtsEngineId { get; set; } = _config.TTS.DefaultEngine;

	public string TtsVoiceId { get; set; } = _config.TTS.DefaultVoice;

	public string TtsOutputRoot { get; set; } = string.IsNullOrWhiteSpace(_config.TTS.OutputRoot)
		? Path.Combine(SpeakDataPaths.ResolveDataRoot(), "tts", "outputs")
		: Path.GetFullPath(_config.TTS.OutputRoot);

	public string TtsLanguage { get; set; } = _config.TTS.DefaultLanguage;

	public string TtsLastOutputPath { get; set; } = "";

	public string QwenTtsCustomVoiceModelPath { get; set; } = _config.TTS.QwenTtsCustomVoiceModelPath;

	public string QwenTtsBaseModelPath { get; set; } = _config.TTS.QwenTtsBaseModelPath;

	public string QwenTtsVoiceDesignModelPath { get; set; } = _config.TTS.QwenTtsVoiceDesignModelPath;

	public string VoiceCloneModelId { get; set; } = "qwen3-base-1.7b";

	public string VoiceCloneEngineId { get; set; } = "qwen3-base-1.7b";

	public string VoiceCloneReferenceAudioPath { get; set; } = "";

	public string VoiceCloneProfileName { get; set; } = "My cloned voice";

	public string VoiceCloneOutputRoot { get; set; } = Path.Combine(SpeakDataPaths.ResolveDataRoot(), "voices", "clones");

	public string VoiceDesignModelId { get; set; } = "";

	public string VoiceDesignPrompt { get; set; } = "Warm, clear, natural assistant voice with calm pacing.";

	public string VoiceDesignOutputRoot { get; set; } = Path.Combine(SpeakDataPaths.ResolveDataRoot(), "voices", "designs");

	public static MaxFlowSettings Default => new MaxFlowSettings();
}
