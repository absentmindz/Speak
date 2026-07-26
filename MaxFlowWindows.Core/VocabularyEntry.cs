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
			Spoken = "read me",
			Written = "README"
		},
		new VocabularyEntry
		{
			Spoken = "git hub",
			Written = "GitHub"
		},
		new VocabularyEntry
		{
			Spoken = "type script",
			Written = "TypeScript"
		},
		new VocabularyEntry
		{
			Spoken = "java script",
			Written = "JavaScript"
		},
		new VocabularyEntry
		{
			Spoken = "chat gpt",
			Written = "ChatGPT"
		},
		new VocabularyEntry
		{
			Spoken = "power shell",
			Written = "PowerShell"
		},
		new VocabularyEntry
		{
			Spoken = "postgre sequel",
			Written = "PostgreSQL"
		},
		new VocabularyEntry
		{
			Spoken = "api",
			Written = "API"
		},
		new VocabularyEntry
		{
			Spoken = "json",
			Written = "JSON"
		},
		new VocabularyEntry
		{
			Spoken = "sql",
			Written = "SQL"
		},
		new VocabularyEntry
		{
			Spoken = "dot net",
			Written = ".NET"
		},
		new VocabularyEntry
		{
			Spoken = "whisper",
			Written = "Whisper"
		},
		new VocabularyEntry
		{
			Spoken = "qwen",
			Written = "Qwen"
		},
		new VocabularyEntry
		{
			Spoken = "cuda",
			Written = "CUDA"
		},
		new VocabularyEntry
		{
			Spoken = "codex",
			Written = "Codex"
		}
	};
}
