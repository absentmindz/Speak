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

	public bool RequiresModel { get; set; } = true;

	public bool UsesLocalResources { get; set; } = true;

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
			Id = "edge-tts",
			Name = "Microsoft Edge TTS",
			Subtitle = "Fast Microsoft neural voices; cloud-backed and requires an internet connection.",
			Role = "Fast cloud neural voice",
			RuntimePath = ResolveEdgeTtsRuntimePath(Config.TTS.EdgeTtsExecutablePath),
			RequiresModel = false,
			UsesLocalResources = false
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
		},
		new TtsEngineOption
		{
			Id = "higgs-tts-3-4b",
			Name = "Higgs Audio v3 TTS",
			Subtitle = "Boson Higgs Audio v3 4B expressive multilingual TTS; zero-shot cloning; CPU-only (slow).",
			Role = "Expressive premium voice",
			ModelPath = Config.TTS.HiggsTtsModelPath,
			RuntimePath = Config.TTS.HiggsPythonPath
		}
	};

	public static string ResolveEdgeTtsRuntimePath(string configuredPath)
	{
		return ResolveEdgeTtsRuntimePath(
			configuredPath,
			AppContext.BaseDirectory,
			Environment.GetEnvironmentVariable("PATH") ?? "",
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
	}

	internal static string ResolveEdgeTtsRuntimePath(
		string configuredPath,
		string baseDirectory,
		string pathEnvironment,
		string applicationData,
		string localApplicationData)
	{
		List<string> candidates = new();
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

		void AddCandidate(string? candidate)
		{
			string value = (candidate ?? "").Trim().Trim('"');
			if (!string.IsNullOrWhiteSpace(value) && seen.Add(value))
			{
				candidates.Add(value);
			}
		}

		AddCandidate(configuredPath);
		AddCandidate(Path.Combine(baseDirectory, "Runtime", "EdgeTts", "7.2.8", "python.exe"));
		AddCandidate(Path.Combine(baseDirectory, "Runtime", "Whisper", "python.exe"));
		AddCandidate(Path.Combine(baseDirectory, "Runtime", "EdgeTts", "python.exe"));
		AddCandidate(Path.Combine(baseDirectory, "Runtime", "EdgeTts", "edge-tts.exe"));
		AddCandidate(Path.Combine(baseDirectory, "Tools", "edge-tts.exe"));
		AddCandidate(Path.Combine(baseDirectory, "edge-tts.exe"));

		foreach (string entry in pathEnvironment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
		{
			AddCandidate(Path.Combine(entry.Trim().Trim('"'), "edge-tts.exe"));
		}

		foreach (string pythonRoot in new[]
		{
			Path.Combine(applicationData, "Python"),
			Path.Combine(localApplicationData, "Programs", "Python")
		})
		{
			string[] pythonDirectories;
			try
			{
				pythonDirectories = Directory.Exists(pythonRoot)
					? Directory.EnumerateDirectories(pythonRoot, "Python*").OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray()
					: Array.Empty<string>();
			}
			catch
			{
				pythonDirectories = Array.Empty<string>();
			}
			foreach (string pythonDirectory in pythonDirectories)
			{
				AddCandidate(Path.Combine(pythonDirectory, "Scripts", "edge-tts.exe"));
			}
		}

		foreach (string candidate in candidates)
		{
			try
			{
				if (IsUsableEdgeTtsRuntime(candidate))
				{
					return Path.GetFullPath(candidate);
				}
			}
			catch
			{
			}
		}

		return (configuredPath ?? "").Trim().Trim('"');
	}

	internal static bool IsEdgeTtsPythonRuntime(string runtimePath)
	{
		if (string.IsNullOrWhiteSpace(runtimePath)
			|| !Path.GetFileName(runtimePath).StartsWith("python", StringComparison.OrdinalIgnoreCase)
			|| !Path.GetExtension(runtimePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		string? runtimeRoot = Path.GetDirectoryName(runtimePath);
		return !string.IsNullOrWhiteSpace(runtimeRoot)
			&& File.Exists(Path.Combine(runtimeRoot, "Lib", "site-packages", "edge_tts", "__main__.py"));
	}

	private static bool IsPlausibleWindowsExecutable(string path)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(path)
				|| !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			if (stream.Length < 88)
			{
				return false;
			}

			using BinaryReader reader = new BinaryReader(stream);
			if (reader.ReadUInt16() != 0x5A4D)
			{
				return false;
			}

			stream.Position = 0x3C;
			int peHeaderOffset = reader.ReadInt32();
			if (peHeaderOffset < 0x40 || peHeaderOffset > stream.Length - 24)
			{
				return false;
			}

			stream.Position = peHeaderOffset;
			if (reader.ReadUInt32() != 0x00004550)
			{
				return false;
			}

			ushort machine = reader.ReadUInt16();
			ushort sectionCount = reader.ReadUInt16();
			stream.Position += 12;
			ushort optionalHeaderSize = reader.ReadUInt16();
			ushort characteristics = reader.ReadUInt16();
			return machine != 0
				&& sectionCount > 0
				&& optionalHeaderSize > 0
				&& (characteristics & 0x0002) != 0;
		}
		catch
		{
			return false;
		}
	}

	internal static bool IsUsableEdgeTtsRuntime(string runtimePath)
	{
		return IsPlausibleWindowsExecutable(runtimePath)
			&& (!Path.GetFileName(runtimePath).StartsWith("python", StringComparison.OrdinalIgnoreCase)
				|| IsEdgeTtsPythonRuntime(runtimePath));
	}

	public static TtsEngineOption Find(string id)
	{
		return Presets.FirstOrDefault((TtsEngineOption option) => option.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? Presets[0];
	}

	public bool IsRuntimeReady()
	{
		return Id.Equals("edge-tts", StringComparison.OrdinalIgnoreCase)
			? IsUsableEdgeTtsRuntime(RuntimePath)
			: !string.IsNullOrWhiteSpace(RuntimePath) && File.Exists(RuntimePath);
	}

	public bool IsModelReady()
	{
		return !RequiresModel
			|| (!string.IsNullOrWhiteSpace(ModelPath) && (Directory.Exists(ModelPath) || File.Exists(ModelPath)));
	}
}
