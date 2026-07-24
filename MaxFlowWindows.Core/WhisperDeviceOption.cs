using System.Collections.Generic;

namespace MaxFlowWindows.Core;

public sealed class WhisperDeviceOption
{
	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	public string Subtitle { get; set; } = "";

	public static IReadOnlyList<WhisperDeviceOption> Presets { get; } = new List<WhisperDeviceOption>
	{
		new WhisperDeviceOption
		{
			Id = "auto",
			Name = "Auto (GPU if available)",
			Subtitle = "Use CUDA when the Whisper Python environment supports it, otherwise CPU"
		},
		new WhisperDeviceOption
		{
			Id = "cuda",
			Name = "GPU (CUDA)",
			Subtitle = "Require the model to load on the NVIDIA GPU"
		},
		new WhisperDeviceOption
		{
			Id = "cpu",
			Name = "CPU",
			Subtitle = "Use system CPU/RAM only"
		}
	};
}
