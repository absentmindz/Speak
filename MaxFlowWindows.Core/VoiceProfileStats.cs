using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MaxFlowWindows.Core;

public sealed record VoiceProfileStats(int SavedCount, int SpokenWordCount, int TodaySpokenWordCount, int TodaySessionCount, int AverageWordsPerTranscript, int ActiveDayStreak, int SavedCorrections, int AutoLearnedCorrections, int AccuracyPercent)
{
	public string AccuracyLabel
	{
		get
		{
			if (SavedCount != 0)
			{
				return $"{AccuracyPercent}%";
			}
			return "No history";
		}
	}

	public string SavedCorrectionsLabel
	{
		get
		{
			if (SavedCorrections != 1)
			{
				return $"{SavedCorrections:N0} saved corrections";
			}
			return "1 saved correction";
		}
	}

	public string AutoLearnedLabel
	{
		get
		{
			if (AutoLearnedCorrections != 1)
			{
				return $"{AutoLearnedCorrections:N0} learned automatically";
			}
			return "1 learned automatically";
		}
	}

	public string TodayLabel
	{
		get
		{
			if (TodaySpokenWordCount != 1)
			{
				return $"{TodaySpokenWordCount:N0} words today";
			}
			return "1 word today";
		}
	}

	public string SessionLabel
	{
		get
		{
			if (TodaySessionCount != 1)
			{
				return $"{TodaySessionCount:N0} sessions today";
			}
			return "1 session today";
		}
	}

	public string StreakLabel
	{
		get
		{
			if (ActiveDayStreak != 1)
			{
				return $"{ActiveDayStreak:N0} day streak";
			}
			return "1 day streak";
		}
	}

	public static VoiceProfileStats From(IEnumerable<TranscriptCard> history, IEnumerable<VocabularyEntry> vocabulary, DateTimeOffset? now = null)
	{
		List<TranscriptCard> history2 = history.ToList();
		TranscriptStats transcriptStats = TranscriptStats.FromHistory(history2, now);
		List<VocabularyEntry> list = vocabulary.Where((VocabularyEntry entry) => !string.IsNullOrWhiteSpace(entry.Spoken) && !string.IsNullOrWhiteSpace(entry.Written)).ToList();
		int count = list.Count;
		int autoLearnedCorrections = list.Count((VocabularyEntry entry) => entry.Source.Equals("auto", StringComparison.OrdinalIgnoreCase) && entry.LearnedCount > 0);
		return new VoiceProfileStats(transcriptStats.SavedCount, transcriptStats.SpokenWordCount, transcriptStats.TodaySpokenWordCount, transcriptStats.TodaySessionCount, transcriptStats.AverageWordsPerTranscript, transcriptStats.ActiveDayStreak, count, autoLearnedCorrections, CalculateAccuracyPercent(history2));
	}

	private static int CalculateAccuracyPercent(IEnumerable<TranscriptCard> history)
	{
		int num = 0;
		int num2 = 0;
		foreach (TranscriptCard item in history)
		{
			List<string> list = Tokenize(item.RawText);
			if (list.Count == 0 || string.IsNullOrWhiteSpace(item.FormattedText))
			{
				continue;
			}
			HashSet<string> hashSet = Tokenize(item.FormattedText).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
			string text = string.Concat(hashSet);
			foreach (string item2 in list)
			{
				num++;
				if (hashSet.Contains(item2) || text.Contains(item2, StringComparison.OrdinalIgnoreCase))
				{
					num2++;
				}
			}
		}
		if (num != 0)
		{
			return Math.Clamp((int)Math.Round((double)num2 * 100.0 / (double)num), 0, 100);
		}
		return 0;
	}

	private static List<string> Tokenize(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return new List<string>();
		}
		return (from match in Regex.Matches(text.ToLowerInvariant(), "[\\p{L}\\p{N}]+(?:[\\'-][\\p{L}\\p{N}]+)*", RegexOptions.CultureInvariant)
			select match.Value into value
			where value.Length > 1
			select value).ToList();
	}
}
