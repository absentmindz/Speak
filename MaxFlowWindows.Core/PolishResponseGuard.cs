using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MaxFlowWindows.Core;

public static class PolishResponseGuard
{
	private static readonly string[] AssistantOpeners = new string[23]
	{
		"sure", "sure thing", "of course", "absolutely", "certainly", "happy to", "here is", "heres", "i can", "i can help",
		"i would", "i will", "ill", "let me", "okay", "yes", "as an ai", "the answer is", "it looks like", "please provide",
		"can you clarify", "sorry", "im sorry"
	};

	private static readonly string[] StrongAssistantPhrases = new string[17]
	{
		"as an ai", "i can help", "id be happy to", "i would be happy to", "please send me", "please provide", "can you clarify", "here is a polished", "heres a polished", "here is the polished",
		"heres the polished", "i have rewritten", "i rewrote", "the answer is", "you can do this by", "i cannot", "i cant"
	};

	private static readonly string[] RequestOpeners = new string[21]
	{
		"can you", "could you", "would you", "will you", "please", "tell me", "explain", "what is", "what are", "whats",
		"why", "how", "when", "where", "who", "do you", "does", "did", "is there", "should i",
		"should we"
	};

	private static readonly string[] InformationQuestionOpeners = new string[14]
	{
		"what is", "what are", "whats", "why", "how", "when", "where", "who", "do you", "does",
		"did", "is there", "should i", "should we"
	};

	public static string SafePolishedText(string rawTranscript, string locallyFormatted, string polishedText)
	{
		if (!ShouldRejectAssistantAnswer(rawTranscript, polishedText))
		{
			return polishedText.Trim();
		}
		return (string.IsNullOrWhiteSpace(locallyFormatted) ? rawTranscript : locallyFormatted).Trim();
	}

	public static bool ShouldRejectAssistantAnswer(string rawTranscript, string polishedText)
	{
		string text = Normalize(rawTranscript);
		string text2 = Normalize(StripResponseWrapper(polishedText));
		if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(text2))
		{
			return false;
		}
		if (LooksLikeInformationQuestion(rawTranscript) && !LooksLikeQuestionOrRequest(polishedText))
		{
			return true;
		}
		if (IsMeaningfullySame(text, text2))
		{
			return false;
		}
		if (StartsWithAny(text2, AssistantOpeners) && !StartsWithAny(text, AssistantOpeners))
		{
			return true;
		}
		if (ContainsAny(text2, StrongAssistantPhrases) && !ContainsAny(text, StrongAssistantPhrases))
		{
			return true;
		}
		return false;
	}

	private static bool LooksLikeQuestionOrRequest(string text)
	{
		string value = Normalize(text);
		if (!text.Contains('?', StringComparison.Ordinal))
		{
			return StartsWithAny(value, RequestOpeners);
		}
		return true;
	}

	private static bool LooksLikeInformationQuestion(string text)
	{
		return StartsWithAny(Normalize(text), InformationQuestionOpeners);
	}

	private static bool IsMeaningfullySame(string raw, string polished)
	{
		if (raw.Equals(polished, StringComparison.Ordinal))
		{
			return true;
		}
		if (polished.StartsWith(raw + " ", StringComparison.Ordinal) || raw.StartsWith(polished + " ", StringComparison.Ordinal))
		{
			return true;
		}
		string[] array = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		string[] array2 = polished.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (array.Length == 0 || array2.Length == 0)
		{
			return false;
		}
		HashSet<string> hashSet = array.ToHashSet<string>(StringComparer.Ordinal);
		double num = (double)array2.Count(hashSet.Contains) / (double)Math.Max(hashSet.Count, array2.Distinct<string>(StringComparer.Ordinal).Count());
		double num2 = (double)Math.Min(raw.Length, polished.Length) / (double)Math.Max(raw.Length, polished.Length);
		if (num >= 0.82)
		{
			return num2 >= 0.72;
		}
		return false;
	}

	private static string StripResponseWrapper(string text)
	{
		string text2 = text.Trim();
		string[] array = new string[5] { "polished text:", "final text:", "rewritten text:", "transcription:", "output:" };
		foreach (string text3 in array)
		{
			if (text2.StartsWith(text3, StringComparison.OrdinalIgnoreCase))
			{
				string text4 = text2;
				int length = text3.Length;
				return text4.Substring(length, text4.Length - length).Trim();
			}
		}
		return text2;
	}

	private static bool StartsWithAny(string value, IEnumerable<string> phrases)
	{
		foreach (string phrase in phrases)
		{
			string text = Normalize(phrase);
			if (value.Equals(text, StringComparison.Ordinal) || value.StartsWith(text + " ", StringComparison.Ordinal))
			{
				return true;
			}
		}
		return false;
	}

	private static bool ContainsAny(string value, IEnumerable<string> phrases)
	{
		foreach (string phrase in phrases)
		{
			string text = Normalize(phrase);
			if (value.Equals(text, StringComparison.Ordinal) || value.Contains(" " + text + " ", StringComparison.Ordinal) || value.StartsWith(text + " ", StringComparison.Ordinal) || value.EndsWith(" " + text, StringComparison.Ordinal))
			{
				return true;
			}
		}
		return false;
	}

	private static string Normalize(string text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return "";
		}
		StringBuilder stringBuilder = new StringBuilder(text.Length);
		string text2 = text.Trim().ToLowerInvariant();
		foreach (char c in text2)
		{
			if (char.IsLetterOrDigit(c))
			{
				stringBuilder.Append(c);
			}
			else
			{
				stringBuilder.Append(' ');
			}
		}
		return string.Join(' ', stringBuilder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
	}
}
