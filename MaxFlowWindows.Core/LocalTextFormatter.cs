using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MaxFlowWindows.Core;

public sealed class LocalTextFormatter
{
	private sealed record FastCommandResult(string Text, bool MakeShorter);

	private const string DeleteLastSentencePattern = "\\b(delete last sentence|delete the last sentence|erase last sentence|scratch that|delete that)\\b";

	private const string DeleteLastWordPattern = "\\b(delete last word|erase last word)\\b";

	private static readonly string[] Fillers = new string[6] { "um", "uh", "erm", "hmm", "you know", "i mean" };

	[System.Diagnostics.CodeAnalysis.SuppressMessage(
		"Performance",
		"CA1822:Mark members as static",
		Justification = "Format is intentionally an instance service API used by the application and tests.")]
	public string Format(string text, DictationMode mode, IEnumerable<VocabularyEntry> vocabulary)
	{
		FastCommandResult fastCommandResult = ApplyFastCommands(text);
		string text2 = NormalizeSpeechCommands(fastCommandResult.Text);
		if (string.Equals(mode.Id, "raw", StringComparison.OrdinalIgnoreCase))
		{
			return ApplyVocabulary(NormalizeWhitespace(text2), vocabulary);
		}
		string text3 = RemoveFillers(NormalizeWhitespace(text2));
		if (fastCommandResult.MakeShorter)
		{
			text3 = MakeShorter(text3);
		}
		string text4 = mode.Id switch
		{
			"message" => text3.Contains('\n') ? ShapeWithExplicitBreaks(text3, MessageShape) : MessageShape(SplitSentences(text3)), 
			"email" => EmailShape(SplitSentences(text3)), 
			"prompt" => PromptShape(SplitSentences(text3)), 
			"notes" => NoteShape(SplitSentences(text3)), 
			_ => text3.Contains('\n') ? ShapeWithExplicitBreaks(text3, SmartShape) : SmartShape(SplitSentences(text3)), 
		};
		if (fastCommandResult.MakeShorter)
		{
			text4 = CompactOutput(text4);
		}
		return ApplyVocabulary(text4, vocabulary).Trim();
	}

	private static FastCommandResult ApplyFastCommands(string text)
	{
		string input = text ?? "";
		return new FastCommandResult(MakeShorter: Regex.IsMatch(input, "\\b(rewrite shorter|make it shorter|shorten this|make this shorter)\\b", 		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), Text: ApplyDeleteCommand(ApplyDeleteCommand(Regex.Replace(input, "\\b(rewrite shorter|make it shorter|shorten this|make this shorter)\\b", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled), "\\b(delete last sentence|delete the last sentence|erase last sentence|scratch that|delete that)\\b", RemoveLastSentence), "\\b(delete last word|erase last word)\\b", RemoveLastWord));
	}

	private static string ApplyDeleteCommand(string text, string pattern, Func<string, string> removePrevious)
	{
		string text2 = text;
		int num = 0;
		while (num++ < 20)
		{
			Match match = Regex.Match(text2, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
			if (!match.Success)
			{
				break;
			}
			string text3 = removePrevious(text2.Substring(0, match.Index).TrimEnd());
			string text4 = text2;
			int num2 = match.Index + match.Length;
			string text5 = text4.Substring(num2, text4.Length - num2).TrimStart();
			text2 = NormalizeWhitespace(text3 + " " + text5);
		}
		return text2;
	}

	private static string RemoveLastSentence(string text)
	{
		string text2 = text.TrimEnd();
		if (string.IsNullOrWhiteSpace(text2))
		{
			return "";
		}
		int num = text2.Length - 1;
		while (num >= 0 && ".!?".Contains(text2[num]))
		{
			num--;
		}
		for (int num2 = num; num2 >= 0; num2--)
		{
			if (".!?\n".Contains(text2[num2]))
			{
				return text2.Substring(0, num2 + 1).TrimEnd();
			}
		}
		return "";
	}

	private static string RemoveLastWord(string text)
	{
		string text2 = text.TrimEnd();
		if (string.IsNullOrWhiteSpace(text2))
		{
			return "";
		}
		Match match = Regex.Match(text2, "\\b[\\w']+\\W*$", RegexOptions.CultureInvariant);
		if (!match.Success)
		{
			return "";
		}
		return text2.Substring(0, match.Index).TrimEnd();
	}

	private static string NormalizeSpeechCommands(string text)
	{
		string input = text ?? "";
		input = Regex.Replace(input, "\\b(new paragraph|next paragraph)\\b", Environment.NewLine + Environment.NewLine, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
		input = Regex.Replace(input, "\\b(new bullet|next bullet|bullet point)\\b", Environment.NewLine + "- ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
		input = Regex.Replace(input, "\\b(new line|next line)\\b", Environment.NewLine, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
		(string, string)[] array = new(string, string)[8]
		{
			("full stop", "."),
			("period", "."),
			("comma", ","),
			("question mark", "?"),
			("exclamation mark", "!"),
			("colon", ":"),
			("semicolon", ";"),
			("dash", "-")
		};
		for (int i = 0; i < array.Length; i++)
		{
			(string, string) tuple = array[i];
			string item = tuple.Item1;
			string item2 = tuple.Item2;
			input = Regex.Replace(input, "\\b" + Regex.Escape(item) + "\\b", item2, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		}
		return input;
	}

	public static string ApplyVocabulary(string text, IEnumerable<VocabularyEntry> vocabulary)
	{
		string text2 = text;
		foreach (VocabularyEntry item in from entry in vocabulary
			where !string.IsNullOrWhiteSpace(entry.Spoken) && !string.IsNullOrWhiteSpace(entry.Written)
			orderby entry.Spoken.Trim().Length descending
			select entry)
		{
			string replacement = item.Written.Trim();
			// Use a match evaluator so user vocabulary containing '$' or
			// backslashes is inserted literally instead of being interpreted as
			// a regular-expression replacement expression.
			text2 = Regex.Replace(text2, BuildVocabularyPattern(item.Spoken), _ => replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		}
		return text2;
	}

	private static string BuildVocabularyPattern(string spoken)
	{
		IEnumerable<string> values = (from token in Regex.Split(spoken.Trim(), "\\s+")
			where !string.IsNullOrWhiteSpace(token)
			select token).Select(VocabularyTokenPattern);
		string separator = "(?:\\s+|(?=[._@+\\-/])|(?<=[._@+\\-/]))";
		return "(?<![A-Za-z0-9_])" + string.Join(separator, values) + "(?![A-Za-z0-9_])";
	}

	private static string VocabularyTokenPattern(string token)
	{
		switch (token.Trim().ToLowerInvariant())
		{
		case "underscore":
			return "(?:underscore|_)";
		case "dot":
			return "(?:dot|\\.)";
		case "period":
			return "(?:period|\\.)";
		case "at":
			return "(?:at|@)";
		case "hyphen":
		case "dash":
			return "(?:dash|hyphen|-)";
		case "slash":
			return "(?:slash|/)";
		case "plus":
			return "(?:plus|\\+)";
		default:
			return Regex.Escape(token.Trim());
		}
	}

	private static string NormalizeWhitespace(string text)
	{
		string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
		normalized = Regex.Replace(normalized, "[ \\t]+", " ");
		normalized = Regex.Replace(normalized, " *\\n *", "\n");
		normalized = Regex.Replace(normalized, "\\n{3,}", "\n\n");
		normalized = Regex.Replace(normalized, "\\s+([,.!?;:])", "$1");
		// Do not blindly insert spaces after punctuation. That corrupts URLs,
		// drive-qualified paths, email addresses, version numbers, and code
		// (for example https://, C:\Temp, and 1.2.3).
		normalized = Regex.Replace(normalized, "-\\s+", "- ");
		return normalized.Trim();
	}

	private static string RemoveFillers(string text)
	{
		string text2 = text;
		string[] fillers = Fillers;
		foreach (string str in fillers)
		{
			text2 = Regex.Replace(text2, "(^|[\\s,.;:!?-])" + Regex.Escape(str) + "(?=($|[\\s,.;:!?-]))[, ]*", (Match match) => match.Groups[1].Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		}
		return NormalizeWhitespace(text2);
	}

	private static string MakeShorter(string text)
	{
		string input = text;
		string[] array = new string[9] { "basically", "actually", "really", "very", "just", "kind of", "sort of", "a little bit", "if possible" };
		foreach (string str in array)
		{
			input = Regex.Replace(input, "\\b" + Regex.Escape(str) + "\\b", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		}
		input = Regex.Replace(input, "\\b(can you|could you|would you)\\s+please\\s+", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		input = Regex.Replace(input, "\\bplease\\s+", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		return NormalizeWhitespace(input);
	}

	private static string CompactOutput(string text)
	{
		return Regex.Replace(Regex.Replace(text, "[ \\t]{2,}", " "), "\\n{3,}", "\n\n").Trim();
	}

	private static IReadOnlyList<string> SplitSentences(string text)
	{
		string text2 = NormalizeWhitespace(text).Replace("\n", " ");
		if (string.IsNullOrWhiteSpace(text2))
		{
			return Array.Empty<string>();
		}
		List<string> list = (from part in Regex.Split(text2, "(?<=[.!?])\\s+")
			select NormalizeSentence(part) into part
			where !string.IsNullOrWhiteSpace(part)
			select part).ToList();
		if (list.Count != 0)
		{
			return list;
		}
		return new string[1] { NormalizeSentence(text2) };
	}

	private static string ShapeWithExplicitBreaks(string text, Func<IReadOnlyList<string>, string> lineFormatter)
	{
		IEnumerable<string> values = from paragraph in Regex.Split(NormalizeWhitespace(text), "\\n{2,}").Select(delegate(string paragraph)
			{
				IEnumerable<string> values2 = from line in paragraph.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
					select lineFormatter(SplitSentences(line)) into line
					where !string.IsNullOrWhiteSpace(line)
					select line;
				return string.Join(Environment.NewLine, values2);
			})
			where !string.IsNullOrWhiteSpace(paragraph)
			select paragraph;
		return string.Join(Environment.NewLine + Environment.NewLine, values);
	}

	private static string NormalizeSentence(string sentence)
	{
		string text = NormalizeWhitespace(sentence);
		if (string.IsNullOrWhiteSpace(text))
		{
			return "";
		}
		string text2 = text;
		char num;
		if (!".!?".Contains(text2[text2.Length - 1]))
		{
			num = PreferredTerminator(text);
		}
		else
		{
			string text3 = text;
			num = text3[text3.Length - 1];
		}
		char c = num;
		text = text.TrimEnd('.', '!', '?', ' ');
		// Preserve intentional casing in acronyms, product names, paths, URLs,
		// and code. The previous whole-sentence ToLowerInvariant call corrupted
		// values such as API, PowerShell, C:\Temp, and case-sensitive tokens.
		text = Regex.Replace(text, "\\bi\\b", _ => "I", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		text = Regex.Replace(text, "\\bi'm\\b", _ => "I'm", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		text = Regex.Replace(text, "\\bi've\\b", _ => "I've", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		text = Regex.Replace(text, "\\bi'll\\b", _ => "I'll", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		text = Regex.Replace(text, "\\bi'd\\b", _ => "I'd", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
		char reference;
		for (int i = 0; i < text.Length; i++)
		{
			if (char.IsLetter(text[i]))
			{
				ReadOnlySpan<char> readOnlySpan = text.AsSpan(0, i);
				reference = char.ToUpperInvariant(text[i]);
				string readOnlySpan2 = new string(reference, 1);
				string text4 = text;
				int num2 = i + 1;
				text = string.Concat(readOnlySpan, readOnlySpan2, text4.AsSpan(num2));
				break;
			}
		}
		ReadOnlySpan<char> readOnlySpan3 = text;
		reference = c;
		return string.Concat(readOnlySpan3, new string(reference, 1));
	}

	private static char PreferredTerminator(string text)
	{
		string text2 = text.Trim().ToLowerInvariant();
		if (!text2.StartsWith("can you ", StringComparison.Ordinal) && !text2.StartsWith("could you ", StringComparison.Ordinal) && !text2.StartsWith("would you ", StringComparison.Ordinal) && !text2.StartsWith("should ", StringComparison.Ordinal) && !text2.StartsWith("what ", StringComparison.Ordinal) && !text2.StartsWith("why ", StringComparison.Ordinal) && !text2.StartsWith("how ", StringComparison.Ordinal) && !text2.StartsWith("when ", StringComparison.Ordinal) && !text2.StartsWith("where ", StringComparison.Ordinal))
		{
			return '.';
		}
		return '?';
	}

	private static string SmartShape(IReadOnlyList<string> sentences)
	{
		if (sentences.Count <= 3)
		{
			return string.Join(" ", sentences);
		}
		return string.Join(Environment.NewLine + Environment.NewLine, from chunk in Chunk(sentences, 3)
			select string.Join(" ", chunk));
	}

	private static string MessageShape(IReadOnlyList<string> sentences)
	{
		if (sentences.Count <= 2)
		{
			return string.Join(" ", sentences);
		}
		return string.Join(Environment.NewLine, sentences.Select(TrimFinalPeriod));
	}

	private static string EmailShape(IReadOnlyList<string> sentences)
	{
		if (sentences.Count == 0)
		{
			return "";
		}
		string value = ((sentences.Count <= 2) ? string.Join(" ", sentences) : string.Join(Environment.NewLine + Environment.NewLine, from chunk in Chunk(sentences, 2)
			select string.Join(" ", chunk)));
		return $"Hi,{Environment.NewLine}{Environment.NewLine}{value}{Environment.NewLine}{Environment.NewLine}Thanks.";
	}

	private static string PromptShape(IReadOnlyList<string> sentences)
	{
		if (sentences.Count == 0)
		{
			return "";
		}
		string text = TrimFinalPeriod(sentences[0]);
		List<string> list = sentences.Skip(1).ToList();
		if (list.Count == 0)
		{
			return "Task:" + Environment.NewLine + text;
		}
		List<string> values = list.Select((string part, int index) => $"{index + 1}. {TrimFinalPeriod(part)}").ToList();
		return $"Task:{Environment.NewLine}{text}{Environment.NewLine}{Environment.NewLine}Requirements:{Environment.NewLine}{string.Join(Environment.NewLine, values)}";
	}

	private static string NoteShape(IReadOnlyList<string> sentences)
	{
		return string.Join(Environment.NewLine, sentences.Select(delegate(string part)
		{
			string text = TrimFinalPeriod(part);
			return (!text.StartsWith("- ", StringComparison.Ordinal)) ? ("- " + text) : text;
		}));
	}

	private static IEnumerable<IReadOnlyList<string>> Chunk(IReadOnlyList<string> sentences, int size)
	{
		for (int index = 0; index < sentences.Count; index += size)
		{
			yield return sentences.Skip(index).Take(size).ToList();
		}
	}

	private static string TrimFinalPeriod(string text)
	{
		return text.Trim().TrimEnd('.');
	}
}
