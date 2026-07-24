using System.Collections.Generic;

namespace MaxFlowWindows.Core;

public sealed class EngineProfile
{
	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	public string Subtitle { get; set; } = "";

	public bool IsAvailable { get; set; }

	public static IReadOnlyList<EngineProfile> Presets { get; } = new List<EngineProfile>
	{
		new EngineProfile
		{
			Id = "whisper-local",
			Name = "Local Whisper",
			Subtitle = "Record on Windows, transcribe locally, then format with Speak",
			IsAvailable = true
		},
		new EngineProfile
		{
			Id = "cloud-stt",
			Name = "Cloud STT",
			Subtitle = "Record locally, transcribe through the selected API provider, then format with Speak",
			IsAvailable = true
		},
		new EngineProfile
		{
			Id = "manual",
			Name = "Manual Text",
			Subtitle = "Type or paste text, then format locally",
			IsAvailable = true
		},
		new EngineProfile
		{
			Id = "apple-local",
			Name = "Apple Local",
			Subtitle = "On-device iPhone speech recognition, used in the iOS build",
			IsAvailable = false
		},
		new EngineProfile
		{
			Id = "whisperkit-small",
			Name = "WhisperKit Small",
			Subtitle = "Bundled Core ML model, next build step on Mac",
			IsAvailable = false
		}
	};
}
