using System;
using System.Collections.Generic;

namespace MaxFlowWindows.Core;

public sealed class VocabularyEntry
{
	public Guid Id { get; set; } = Guid.NewGuid();

	public string Spoken { get; set; } = "";

	public string Written { get; set; } = "";

	public string Source { get; set; } = "manual";

	public string PreviousWritten { get; set; } = "";

	public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

	public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

	public int LearnedCount { get; set; }

	public string ReviewTitle
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(Spoken) || !string.IsNullOrWhiteSpace(Written))
			{
				return Spoken + " -> " + Written;
			}
			return "Empty correction";
		}
	}

	public string ReviewSubtitle
	{
		get
		{
			string text = ((LearnedCount == 1) ? "1 time" : $"{LearnedCount} times");
			if (!Source.Equals("auto", StringComparison.OrdinalIgnoreCase))
			{
				return "Trusted dictionary term";
			}
			return "Auto-learned " + text;
		}
	}

	public static IReadOnlyList<VocabularyEntry> Defaults { get; } = new List<VocabularyEntry>
	{
		new VocabularyEntry
		{
			Spoken = "speak",
			Written = "Speak"
		},
		new VocabularyEntry
		{
			Spoken = "max flow",
			Written = "MaxFlow"
		},
		new VocabularyEntry
		{
			Spoken = "open claw",
			Written = "OpenClaw"
		},
		new VocabularyEntry
		{
			Spoken = "super whisper",
			Written = "SuperWhisper"
		},
		new VocabularyEntry
		{
			Spoken = "whisper flow",
			Written = "Wispr Flow"
		},
		new VocabularyEntry
		{
			Spoken = "chat gpt",
			Written = "ChatGPT"
		},
		new VocabularyEntry
		{
			Spoken = "groq",
			Written = "Groq"
		},
		new VocabularyEntry
		{
			Spoken = "test flight",
			Written = "TestFlight"
		},
		new VocabularyEntry
		{
			Spoken = "iphone",
			Written = "iPhone"
		},
		new VocabularyEntry
		{
			Spoken = "i o s",
			Written = "iOS"
		},
		new VocabularyEntry
		{
			Spoken = "x code",
			Written = "Xcode"
		},
		new VocabularyEntry
		{
			Spoken = "x code gen",
			Written = "XcodeGen"
		},
		new VocabularyEntry
		{
			Spoken = "whisper kit",
			Written = "WhisperKit"
		},
		new VocabularyEntry
		{
			Spoken = "x a u u s d",
			Written = "XAUUSD"
		},
		new VocabularyEntry
		{
			Spoken = "m t five",
			Written = "MT5"
		},
		new VocabularyEntry
		{
			Spoken = "codex",
			Written = "Codex"
		}
	};
}
