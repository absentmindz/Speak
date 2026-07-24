using System.Collections.Generic;

namespace MaxFlowWindows.Core;

public sealed class RecognitionLocaleOption
{
	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	public string Subtitle { get; set; } = "";

	public static IReadOnlyList<RecognitionLocaleOption> Presets { get; } = new List<RecognitionLocaleOption>
	{
		new RecognitionLocaleOption
		{
			Id = "en-US",
			Name = "English US",
			Subtitle = "Fastest and safest local MVP"
		},
		new RecognitionLocaleOption
		{
			Id = "en-GB",
			Name = "English UK",
			Subtitle = "Alternative English recognizer"
		},
		new RecognitionLocaleOption
		{
			Id = "ur-PK",
			Name = "Urdu Pakistan",
			Subtitle = "Experimental with local speech"
		},
		new RecognitionLocaleOption
		{
			Id = "hi-IN",
			Name = "Hindi India",
			Subtitle = "Useful for mixed South Asian speech"
		}
	};
}
