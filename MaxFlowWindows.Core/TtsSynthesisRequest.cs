namespace MaxFlowWindows.Core;

public sealed class TtsSynthesisRequest
{
	public string Text { get; set; } = "";

	public string EngineId { get; set; } = "qwen3-customvoice-1.7b";

	public string VoiceId { get; set; } = "default";

	public string OutputRoot { get; set; } = "";

	public string Language { get; set; } = "English";

	public string VoicePromptPath { get; set; } = "";

	public int ModelKeepAliveMinutes { get; set; } = 10;
}
