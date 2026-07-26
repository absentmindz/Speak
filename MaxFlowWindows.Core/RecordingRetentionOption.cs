using System.Collections.Generic;

namespace MaxFlowWindows.Core;

public sealed class RecordingRetentionOption
{
	public int Days { get; set; }

	public string Name { get; set; } = "";

	public static IReadOnlyList<RecordingRetentionOption> Presets { get; } = new List<RecordingRetentionOption>
	{
		new RecordingRetentionOption
		{
			Days = 0,
			Name = "Keep forever"
		},
		new RecordingRetentionOption
		{
			Days = 7,
			Name = "Delete recordings after 7 days"
		},
		new RecordingRetentionOption
		{
			Days = 30,
			Name = "Delete recordings after 30 days"
		},
		new RecordingRetentionOption
		{
			Days = 90,
			Name = "Delete recordings after 90 days"
		}
	};
}
