using System.Collections.Generic;

namespace MaxFlowWindows.Core;

public sealed class OutputDestinationOption
{
	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	public string Subtitle { get; set; } = "";

	public static IReadOnlyList<OutputDestinationOption> Presets { get; } = new List<OutputDestinationOption>
	{
		new OutputDestinationOption
		{
			Id = "clipboard",
			Name = "Copy to clipboard",
			Subtitle = "Leave the text ready to paste anywhere"
		},
		new OutputDestinationOption
		{
			Id = "paste",
			Name = "Paste into active app",
			Subtitle = "Copy, return to the captured text field, and send Ctrl+V"
		}
	};
}
