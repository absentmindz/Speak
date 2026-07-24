namespace MaxFlowWindows.Core;

public sealed class TtsSynthesisResult
{
	public string OutputPath { get; set; } = "";

	public string EngineName { get; set; } = "";

	public string VoiceName { get; set; } = "";

	public double ElapsedSeconds { get; set; }

	public string Summary => string.IsNullOrWhiteSpace(OutputPath)
		? $"{EngineName} did not return an output file."
		: $"{EngineName} generated {System.IO.Path.GetFileName(OutputPath)} in {ElapsedSeconds:0.0}s.";
}
