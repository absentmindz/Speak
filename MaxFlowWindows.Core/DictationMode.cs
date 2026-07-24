using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MaxFlowWindows.Core;

public sealed class DictationMode
{
	private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true
	};

	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	public string Subtitle { get; set; } = "";

	public string TintHex { get; set; } = "";

	public string IconGlyph { get; set; } = "";

	public string Badge { get; set; } = "";

	public string Instruction { get; set; } = "";

	public bool IsCustom { get; set; }

	private static IReadOnlyList<DictationMode> BuiltInPresets { get; } = new List<DictationMode>
	{
		new DictationMode
		{
			Id = "smart",
			Name = "Smart",
			Subtitle = "Clean paragraphs without changing intent",
			TintHex = "#6F6F6F",
			IconGlyph = "\ue8d4",
			Badge = "Smart",
			Instruction = "Clean filler words, repair spoken punctuation, keep the tone natural, and preserve meaning."
		},
		new DictationMode
		{
			Id = "message",
			Name = "Message",
			Subtitle = "Short, warm chat-ready lines",
			TintHex = "#707070",
			IconGlyph = "\ue8bd",
			Badge = "Chat",
			Instruction = "Make it quick to send in chat: short lines, natural punctuation, no email framing."
		},
		new DictationMode
		{
			Id = "email",
			Name = "Email",
			Subtitle = "Polished reply with greeting and close",
			TintHex = "#787878",
			IconGlyph = "\ue715",
			Badge = "Mail",
			Instruction = "Turn speech into a clean professional email with concise paragraphs, greeting, and close."
		},
		new DictationMode
		{
			Id = "prompt",
			Name = "Prompt",
			Subtitle = "Task plus numbered requirements",
			TintHex = "#646464",
			IconGlyph = "\ue943",
			Badge = "Tech",
			Instruction = "Preserve technical detail and convert rambling speech into a task and numbered requirements."
		},
		new DictationMode
		{
			Id = "notes",
			Name = "Notes",
			Subtitle = "Scannable bullets",
			TintHex = "#737373",
			IconGlyph = "\ue70b",
			Badge = "Notes",
			Instruction = "Turn speech into short, searchable bullets without adding email or chat tone."
		},
		new DictationMode
		{
			Id = "raw",
			Name = "Raw",
			Subtitle = "Verbatim with vocabulary fixes only",
			TintHex = "#5D5D5D",
			IconGlyph = "\ue720",
			Badge = "Raw",
			Instruction = "Keep the transcript as close to the original speech as possible and apply dictionary replacements only."
		}
	};

	private static List<DictationMode>? _customModes;
	private static readonly object _modesLock = new();

	public static IReadOnlyList<DictationMode> Presets
	{
		get
		{
			lock (_modesLock)
			{
				if (_customModes == null)
				{
					_customModes = new List<DictationMode>();
					LoadCustomModes();
				}

				var all = new List<DictationMode>(BuiltInPresets);
				all.AddRange(_customModes);
				return all.AsReadOnly();
			}
		}
	}

	public static void ReloadCustomModes()
	{
		lock (_modesLock)
		{
			_customModes = new List<DictationMode>();
			LoadCustomModes();
		}
	}

	private static void LoadCustomModes()
	{
		try
		{
			string modesPath = Path.Combine(SpeakDataPaths.ResolveDataRoot(), "modes.json");
			if (!File.Exists(modesPath))
				return;

			string json = File.ReadAllText(modesPath);
			var custom = JsonSerializer.Deserialize<List<DictationMode>>(json, _jsonOptions);
			if (custom == null || custom.Count == 0)
				return;

			var builtInIds = new HashSet<string>(BuiltInPresets.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
			foreach (var mode in custom)
			{
				if (!string.IsNullOrWhiteSpace(mode.Id) && builtInIds.Add(mode.Id))
				{
					mode.IsCustom = true;
					_customModes!.Add(mode);
				}
			}
		}
		catch (Exception ex)
		{
			AppLog.Warn("Could not load custom dictation modes.", ex);
		}
	}
}
