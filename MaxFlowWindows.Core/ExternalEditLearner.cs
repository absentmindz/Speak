using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MaxFlowWindows.Core;

public static class ExternalEditLearner
{
	private const int MaxLearnedPhraseLength = 80;

	private const int MaxLearnedWordCount = 8;

	public static bool IsSafeAutoLearnCorrection(string spoken, string written)
	{
		if (!ContainsUnsafeLearningContent(spoken))
		{
			return !ContainsUnsafeLearningContent(written);
		}
		return false;
	}

	public static IReadOnlyList<LearnedCorrection> Extract(string baseline, string edited)
	{
		string text = NormalizeEditableText(baseline);
		string text2 = NormalizeEditableText(edited);
		if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(text2) || text.Equals(text2, StringComparison.Ordinal))
		{
			return Array.Empty<LearnedCorrection>();
		}
		int num = CommonPrefixLength(text, text2);
		int num2 = CommonSuffixLength(text, text2, num);
		int num3 = text.Length - num2;
		int num4 = text2.Length - num2;
		if (num >= num3 || num >= num4)
		{
			return Array.Empty<LearnedCorrection>();
		}
		(int, int) tuple = ExpandToPhraseBoundary(text, num, num3);
		(int, int) tuple2 = ExpandToPhraseBoundary(text2, num, num4);
		int item = tuple.Item1;
		string spoken = text.Substring(item, tuple.Item2 - item).Trim();
		item = tuple2.Item1;
		string written = text2.Substring(item, tuple2.Item2 - item).Trim();
		if (!IsLearnablePhrase(spoken, written, text, text2))
		{
			return Array.Empty<LearnedCorrection>();
		}
		return new LearnedCorrection[1]
		{
			new LearnedCorrection(spoken, written)
		};
	}

	private static string NormalizeEditableText(string text)
	{
		return Regex.Replace(Regex.Replace((text ?? "").Replace("\r\n", "\n").Replace('\r', '\n'), "[ \\t]+", " "), " *\\n *", "\n").Trim();
	}

	private static int CommonPrefixLength(string before, string after)
	{
		int num = Math.Min(before.Length, after.Length);
		int i;
		for (i = 0; i < num && before[i] == after[i]; i++)
		{
		}
		return i;
	}

	private static int CommonSuffixLength(string before, string after, int prefixLength)
	{
		int num = Math.Min(before.Length, after.Length) - prefixLength;
		int i;
		for (i = 0; i < num && before[before.Length - i - 1] == after[after.Length - i - 1]; i++)
		{
		}
		return i;
	}

	private static (int Start, int End) ExpandToPhraseBoundary(string text, int start, int end)
	{
		start = Math.Clamp(start, 0, text.Length);
		end = Math.Clamp(end, start, text.Length);
		while (start > 0 && !IsPhraseBoundary(text[start - 1]))
		{
			start--;
		}
		while (end < text.Length && !IsPhraseBoundary(text[end]))
		{
			end++;
		}
		return (Start: start, End: end);
	}

	private static bool IsPhraseBoundary(char value)
	{
		bool flag = char.IsWhiteSpace(value);
		if (!flag)
		{
			bool flag2;
			switch (value)
			{
			case '"':
			case '\'':
			case '(':
			case ')':
			case '[':
			case ']':
			case '{':
			case '}':
				flag2 = true;
				break;
			default:
				flag2 = false;
				break;
			}
			flag = flag2;
		}
		return flag;
	}

	private static bool IsLearnablePhrase(string spoken, string written, string baseline, string edited)
	{
		if (string.IsNullOrWhiteSpace(spoken) || string.IsNullOrWhiteSpace(written) || spoken.Equals(written, StringComparison.Ordinal))
		{
			return false;
		}
		int num = CountWords(spoken);
		int num2 = CountWords(written);
		if (spoken.Contains('\n') || written.Contains('\n') || spoken.Length > 80 || written.Length > 80 || num > 8 || num2 > 8)
		{
			return false;
		}
		if ((double)spoken.Length >= (double)baseline.Length * 0.75 && (double)written.Length >= (double)edited.Length * 0.75 && Math.Max(num, num2) > 3)
		{
			return false;
		}
		if (ContainsLetterOrDigit(spoken) && ContainsLetterOrDigit(written))
		{
			return IsSafeAutoLearnCorrection(spoken, written);
		}
		return false;
	}

	private static int CountWords(string text)
	{
		return Regex.Matches(text.Trim(), "[^\\s]+").Count;
	}

	private static bool ContainsLetterOrDigit(string text)
	{
		return text.Any(char.IsLetterOrDigit);
	}

	private static bool ContainsUnsafeLearningContent(string text)
	{
		string text2 = (text ?? "").Trim();
		if (string.IsNullOrWhiteSpace(text2))
		{
			return false;
		}
		if (text2.Contains("://", StringComparison.Ordinal) || text2.Contains('/') || text2.Contains('\\') || text2.Contains('@') || Regex.IsMatch(text2, "(?i)\\bwww\\.") || Regex.IsMatch(text2, "(?i)\\b[a-z0-9][a-z0-9-]*(?:\\.[a-z0-9][a-z0-9-]*)+\\.[a-z]{2,}\\b") || Regex.IsMatch(text2, "\\b\\d{8,}\\b"))
		{
			return true;
		}
		int num = text2.Count(char.IsLetter);
		int num2 = text2.Count(char.IsDigit);
		int num3 = text2.Count((char character) => !char.IsLetterOrDigit(character) && !char.IsWhiteSpace(character));
		if (num != 0 && num2 <= num)
		{
			return num3 > Math.Max(1, num / 2);
		}
		return true;
	}
}
