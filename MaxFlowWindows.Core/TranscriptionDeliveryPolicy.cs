namespace MaxFlowWindows.Core;

public static class TranscriptionDeliveryPolicy
{
	public static string ResolveText(TranscriptCard? historyCard, string formattedOutputText)
	{
		if (!string.IsNullOrWhiteSpace(historyCard?.FormattedText))
		{
			return historyCard.FormattedText.Trim();
		}
		return (formattedOutputText ?? string.Empty).Trim();
	}

	public static bool ShouldDeliver(string text)
	{
		return !string.IsNullOrWhiteSpace(text);
	}
}
