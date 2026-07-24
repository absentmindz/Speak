using System.Collections.Generic;

namespace MaxFlowWindows.Core;

public sealed class AppearanceTheme
{
	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	public static IReadOnlyList<AppearanceTheme> Presets { get; } = new List<AppearanceTheme>
	{
		new AppearanceTheme
		{
			Id = "system",
			Name = "System"
		},
		new AppearanceTheme
		{
			Id = "light",
			Name = "Light"
		},
		new AppearanceTheme
		{
			Id = "dark",
			Name = "Dark"
		}
	};
}
