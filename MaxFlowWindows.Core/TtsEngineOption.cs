using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MaxFlowWindows.Core;

public sealed class TtsEngineOption
{
	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	public string Subtitle { get; set; } = "";

	public string Role { get; set; } = "";

	public string ModelPath { get; set; } = "";

	public string RuntimePath { get; set; } = "";

	public bool SupportsDirectSpeech { get; set; } = true;

	private static readonly Lazy<AppConfig> _config = new(() =>
	{
		try
		{
			return AppConfig.Load();
		}
		catch
		{
			return new AppConfig();
		}
	});

	private static AppConfig Config => _config.Value;

	public static IReadOnlyList<TtsEngineOption> Presets { get; } = new List<TtsEngineOption>
	{
		new TtsEngineOption
		{
			Id = "qwen3-customvoice-1.7b",
			Name = "Qwen3 1.7B CustomVoice",
			Subtitle = "Premium local preset voices using the installed 1.7B CustomVoice model.",
			Role = "Premium natural voice",
			ModelPath = Config.TTS.QwenTtsCustomVoiceModelPath,
			RuntimePath = Config.TTS.ComfyUIPythonPath
		},
		new TtsEngineOption
		{
			Id = "tortoise-ultra-fast",
			Name = "Tortoise TTS",
			Subtitle = "Slow advanced voice synthesis; useful for deliberate voice tests.",
			Role = "Advanced experimental voice",
			ModelPath = Config.TTS.TortoiseModelDir,
			RuntimePath = Config.TTS.TortoisePythonPath
		},
		new TtsEngineOption
		{
			Id = "qwen3-base-1.7b",
			Name = "Qwen3 1.7B Base",
			Subtitle = "Base model for reference-audio voice cloning.",
			Role = "Voice cloning base",
			ModelPath = Config.TTS.QwenTtsBaseModelPath,
			RuntimePath = Config.TTS.ComfyUIPythonPath,
			SupportsDirectSpeech = false
		}
	};

	public static TtsEngineOption Find(string id)
	{
		return Presets.FirstOrDefault((TtsEngineOption option) => option.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? Presets[0];
	}

	public bool IsRuntimeReady()
	{
		return !string.IsNullOrWhiteSpace(RuntimePath) && File.Exists(RuntimePath);
	}

	public bool IsModelReady()
	{
		return !string.IsNullOrWhiteSpace(ModelPath) && (Directory.Exists(ModelPath) || File.Exists(ModelPath));
	}
}
