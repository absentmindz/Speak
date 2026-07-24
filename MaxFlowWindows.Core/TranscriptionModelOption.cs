using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MaxFlowWindows.Core;

public sealed class TranscriptionModelOption
{
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

	private static string ModelRoot => Config.Transcription.WhisperModelPath is { Length: > 0 }
		? Path.GetDirectoryName(Config.Transcription.WhisperModelPath) ?? Path.Combine(Config.Paths.ModelsRoot, "whisper")
		: Path.Combine(Config.Paths.ModelsRoot, "whisper");

	public static string DefaultModelRoot => ModelRoot;

	private static readonly string[] KnownModelOrder = new string[9] { "large-v3", "large-v3-turbo", "large", "large-v2", "large-v1", "medium", "small", "base", "tiny" };

	private static readonly string[] KnownModelNames = new string[14]
	{
		"tiny.en", "tiny", "base.en", "base", "small.en", "small", "medium.en", "medium", "large-v1", "large-v2",
		"large-v3", "large", "large-v3-turbo", "turbo"
	};

	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	public string Subtitle { get; set; } = "";

	public string WhisperArgument { get; set; } = "";

	public string ModelPath { get; set; } = "";

	public string Backend { get; set; } = "python-whisper";

	public string ServerPath { get; set; } = "";

	public static IReadOnlyList<TranscriptionModelOption> Presets { get; } = new List<TranscriptionModelOption>
	{
		new TranscriptionModelOption
		{
			Id = "whisper-large-v3",
			Name = "Whisper large-v3 Python",
			Subtitle = "Local checkpoint, best quality",
			WhisperArgument = "large-v3",
			ModelPath = Path.Combine(ModelRoot, "large-v3.pt")
		},
		new TranscriptionModelOption
		{
			Id = "whisper-large-v3-turbo",
			Name = "Whisper large-v3 turbo",
			Subtitle = "Local checkpoint, faster large model",
			WhisperArgument = "large-v3-turbo",
			ModelPath = Path.Combine(ModelRoot, "large-v3-turbo.pt")
		},
		new TranscriptionModelOption
		{
			Id = "whisper-base",
			Name = "Whisper base",
			Subtitle = "Faster fallback if selected manually",
			WhisperArgument = "base",
			ModelPath = Path.Combine(ModelRoot, "base.pt")
		},
		new TranscriptionModelOption
		{
			Id = "whisper-tiny",
			Name = "Whisper tiny",
			Subtitle = "Fast local sanity check model",
			WhisperArgument = "tiny",
			ModelPath = Path.Combine(ModelRoot, "tiny.pt")
		}
	};

	public static IReadOnlyList<TranscriptionModelOption> LoadAvailableLocalModels(string? modelRoot = null)
	{
		modelRoot ??= ModelRoot;
		Dictionary<string, TranscriptionModelOption> dictionary = new Dictionary<string, TranscriptionModelOption>(StringComparer.OrdinalIgnoreCase);
		if (Directory.Exists(modelRoot))
		{
			foreach (string item in Directory.EnumerateFiles(modelRoot, "*.pt"))
			{
				TranscriptionModelOption transcriptionModelOption = FromCheckpoint(item);
				dictionary[transcriptionModelOption.Id] = transcriptionModelOption;
			}
		}
		if (dictionary.Count == 0)
		{
			foreach (TranscriptionModelOption preset in Presets)
			{
				dictionary[preset.Id] = preset;
			}
		}
		return dictionary.Values.OrderBy((TranscriptionModelOption option) => ModelSortRank(option.WhisperArgument)).ThenBy<TranscriptionModelOption, string>((TranscriptionModelOption option) => option.Name, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static TranscriptionModelOption FromCheckpoint(string path)
	{
		string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
		bool flag = KnownModelNames.Contains<string>(fileNameWithoutExtension, StringComparer.OrdinalIgnoreCase);
		string whisperArgument = (flag ? fileNameWithoutExtension : path);
		return new TranscriptionModelOption
		{
			Id = "whisper-" + StableId(fileNameWithoutExtension),
			Name = "Whisper " + FriendlyModelName(fileNameWithoutExtension),
			Subtitle = (flag ? "Local checkpoint found in Speak's model folder" : "Custom local checkpoint found in Speak's model folder"),
			WhisperArgument = whisperArgument,
			ModelPath = path
		};
	}

	private static string FriendlyModelName(string stem)
	{
		return stem.Replace("_", " ").Trim();
	}

	private static string StableId(string value)
	{
		char[] value2 = (from ch in value.ToLowerInvariant()
			select (!char.IsLetterOrDigit(ch)) ? '-' : ch).ToArray();
		return string.Join("-", new string(value2).Split('-', StringSplitOptions.RemoveEmptyEntries));
	}

	private static int ModelSortRank(string argument)
	{
		string name = Path.GetFileNameWithoutExtension(argument);
		int num = Array.FindIndex(KnownModelOrder, (string item) => item.Equals(name, StringComparison.OrdinalIgnoreCase));
		if (num < 0)
		{
			return KnownModelOrder.Length;
		}
		return num;
	}
}
