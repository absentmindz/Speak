using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MaxFlowWindows.Core;

public sealed record TranscriptStats(int SavedCount, int SpokenWordCount, int TodaySpokenWordCount, int TodaySessionCount, int AverageWordsPerTranscript, int ActiveDayStreak)
{
	public string SpokenWordLabel
	{
		get
		{
			if (SpokenWordCount != 1)
			{
				return $"{SpokenWordCount:N0} spoken words";
			}
			return "1 spoken word";
		}
	}

	public string TodaySpokenWordLabel
	{
		get
		{
			if (TodaySpokenWordCount != 1)
			{
				return $"{TodaySpokenWordCount:N0} today";
			}
			return "1 today";
		}
	}

	public string TodaySessionLabel
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

	public string AverageWordsLabel
	{
		get
		{
			if (AverageWordsPerTranscript != 1)
			{
				return $"{AverageWordsPerTranscript:N0} avg words";
			}
			return "1 avg word";
		}
	}

	public string ActiveStreakLabel
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

	public string VoiceStatsLabel => $"{TodaySessionLabel} / {AverageWordsLabel} / {ActiveStreakLabel}";

	public static TranscriptStats FromHistory(IEnumerable<TranscriptCard> history, DateTimeOffset? now = null)
	{
		List<TranscriptCard> list = history.ToList();
		DateTime today = (now ?? DateTimeOffset.Now).Date;
		int num = list.Sum((TranscriptCard card) => CountSpokenWords(card.RawText));
		int todaySpokenWordCount = list.Where((TranscriptCard card) => card.CreatedAt.Date == today).Sum((TranscriptCard card) => CountSpokenWords(card.RawText));
		int todaySessionCount = list.Count((TranscriptCard card) => card.CreatedAt.Date == today);
		int averageWordsPerTranscript = ((list.Count != 0) ? ((int)Math.Round((double)num / (double)list.Count, MidpointRounding.AwayFromZero)) : 0);
		int activeDayStreak = CountActiveDayStreak(list, today);
		return new TranscriptStats(list.Count, num, todaySpokenWordCount, todaySessionCount, averageWordsPerTranscript, activeDayStreak);
	}

	private static int CountActiveDayStreak(IReadOnlyCollection<TranscriptCard> cards, DateTime today)
	{
		HashSet<DateTime> hashSet = (from card in cards
			where CountSpokenWords(card.RawText) > 0
			select card.CreatedAt.Date).ToHashSet();
		int num = 0;
		DateTime item = today;
		while (hashSet.Contains(item))
		{
			num++;
			item = item.AddDays(-1.0);
		}
		return num;
	}

	public static int CountSpokenWords(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return 0;
		}
		return Regex.Matches(text, "[\\p{L}\\p{N}]+(?:[\\'-][\\p{L}\\p{N}]+)*", RegexOptions.CultureInvariant).Count;
	}
}
