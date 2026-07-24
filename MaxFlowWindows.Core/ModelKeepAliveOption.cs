using System.Collections.Generic;

namespace MaxFlowWindows.Core;

public sealed class ModelKeepAliveOption
{
	public int Minutes { get; set; }

	public string Name { get; set; } = "";

	public static IReadOnlyList<ModelKeepAliveOption> Presets { get; } = new List<ModelKeepAliveOption>
	{
		new ModelKeepAliveOption
		{
			Minutes = 5,
			Name = "5 minutes"
		},
		new ModelKeepAliveOption
		{
			Minutes = 10,
			Name = "10 minutes"
		},
		new ModelKeepAliveOption
		{
			Minutes = 30,
			Name = "30 minutes"
		},
		new ModelKeepAliveOption
		{
			Minutes = 60,
			Name = "1 hour"
		}
	};
}
