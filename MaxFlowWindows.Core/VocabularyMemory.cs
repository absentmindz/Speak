using System;
using System.Collections.Generic;
using System.Linq;

namespace MaxFlowWindows.Core;

public static class VocabularyMemory
{
	public const string ManualSource = "manual";

	public const string AutoSource = "auto";

	public static bool TrySaveCorrection(ICollection<VocabularyEntry> vocabulary, string spoken, string written, out VocabularyEntry savedEntry)
	{
		savedEntry = new VocabularyEntry();
		string cleanSpoken = CleanPhrase(spoken);
		string text = CleanPhrase(written);
		if (string.IsNullOrWhiteSpace(cleanSpoken) || string.IsNullOrWhiteSpace(text))
		{
			return false;
		}
		VocabularyEntry vocabularyEntry = vocabulary.FirstOrDefault((VocabularyEntry entry) => entry.Spoken.Trim().Equals(cleanSpoken, StringComparison.OrdinalIgnoreCase));
		if (vocabularyEntry == null)
		{
			savedEntry = new VocabularyEntry
			{
				Spoken = cleanSpoken,
				Written = text,
				Source = "manual",
				CreatedAt = DateTimeOffset.Now,
				UpdatedAt = DateTimeOffset.Now
			};
			vocabulary.Add(savedEntry);
			return true;
		}
		vocabularyEntry.Written = text;
		vocabularyEntry.Source = "manual";
		vocabularyEntry.PreviousWritten = "";
		vocabularyEntry.UpdatedAt = DateTimeOffset.Now;
		savedEntry = vocabularyEntry;
		return true;
	}

	public static bool TrySaveLearnedCorrection(ICollection<VocabularyEntry> vocabulary, LearnedCorrection correction, out LearnedCorrection savedCorrection)
	{
		string spoken = CleanPhrase(correction.Spoken);
		string text = CleanPhrase(correction.Written);
		savedCorrection = new LearnedCorrection(spoken, text);
		if (string.IsNullOrWhiteSpace(spoken) || string.IsNullOrWhiteSpace(text) || spoken.Equals(text, StringComparison.Ordinal) || !ExternalEditLearner.IsSafeAutoLearnCorrection(spoken, text))
		{
			return false;
		}
		VocabularyEntry vocabularyEntry = vocabulary.FirstOrDefault((VocabularyEntry entry) => entry.Spoken.Trim().Equals(spoken, StringComparison.OrdinalIgnoreCase));
		if (vocabularyEntry == null)
		{
			vocabulary.Add(new VocabularyEntry
			{
				Spoken = spoken,
				Written = text,
				Source = "auto",
				CreatedAt = DateTimeOffset.Now,
				UpdatedAt = DateTimeOffset.Now,
				LearnedCount = 1
			});
			return true;
		}
		if (vocabularyEntry.Written.Trim().Equals(text, StringComparison.Ordinal))
		{
			return false;
		}
		if (!vocabularyEntry.Source.Equals("auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(vocabularyEntry.PreviousWritten))
		{
			vocabularyEntry.PreviousWritten = vocabularyEntry.Written.Trim();
		}
		vocabularyEntry.Written = text;
		vocabularyEntry.Source = "auto";
		vocabularyEntry.UpdatedAt = DateTimeOffset.Now;
		vocabularyEntry.LearnedCount = Math.Max(0, vocabularyEntry.LearnedCount) + 1;
		return true;
	}

	public static void ApproveLearnedCorrection(VocabularyEntry entry)
	{
		entry.Source = "manual";
		entry.PreviousWritten = "";
		entry.UpdatedAt = DateTimeOffset.Now;
	}

	public static bool UndoLearnedCorrection(ICollection<VocabularyEntry> vocabulary, VocabularyEntry entry)
	{
		if (!entry.Source.Equals("auto", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (!string.IsNullOrWhiteSpace(entry.PreviousWritten))
		{
			entry.Written = entry.PreviousWritten;
			entry.PreviousWritten = "";
			entry.Source = "manual";
			entry.UpdatedAt = DateTimeOffset.Now;
			return true;
		}
		return vocabulary.Remove(entry);
	}

	public static string CleanPhrase(string phrase)
	{
		return string.Join(" ", (phrase ?? "").ReplaceLineEndings(" ").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
	}
}
