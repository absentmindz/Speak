using System.Collections.Generic;

namespace MaxFlowWindows.Core;

public sealed class LlmPolishTimeoutOption
{
	public int Seconds { get; set; }

	public string Name { get; set; } = "";

	public static IReadOnlyList<LlmPolishTimeoutOption> Presets { get; } = new List<LlmPolishTimeoutOption>
	{
		new LlmPolishTimeoutOption
		{
			Seconds = 6,
			Name = "6 seconds"
		},
		new LlmPolishTimeoutOption
		{
			Seconds = 12,
			Name = "12 seconds"
		},
		new LlmPolishTimeoutOption
		{
			Seconds = 20,
			Name = "20 seconds"
		},
		new LlmPolishTimeoutOption
		{
			Seconds = 30,
			Name = "30 seconds"
		}
	};
}
