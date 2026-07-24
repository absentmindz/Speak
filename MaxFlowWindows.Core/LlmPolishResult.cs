namespace MaxFlowWindows.Core;

public sealed record LlmPolishResult(bool WasApplied, bool Failed, string Text, string Detail)
{
	public static LlmPolishResult Skipped(string text)
	{
		return new LlmPolishResult(WasApplied: false, Failed: false, text, "LLM polish off");
	}

	public static LlmPolishResult Completed(string text, string provider)
	{
		return new LlmPolishResult(WasApplied: true, Failed: false, text, "LLM polished with " + provider);
	}

	public static LlmPolishResult Fallback(string fallbackText, string detail)
	{
		return new LlmPolishResult(WasApplied: false, Failed: true, fallbackText, detail);
	}
}
