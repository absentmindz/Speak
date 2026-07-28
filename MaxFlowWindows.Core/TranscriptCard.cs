using System;
using System.Linq;

namespace MaxFlowWindows.Core;

public sealed class TranscriptCard
{
	public Guid Id { get; set; } = Guid.NewGuid();

	public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

	public string ModeId { get; set; } = "smart";

	public string EngineId { get; set; } = "";

	public string TranscriptionModelId { get; set; } = "";

	public string TranscriptionModelName { get; set; } = "";

	public string CloudSttProviderId { get; set; } = "";

	public string CloudSttModel { get; set; } = "";

	public string AudioPath { get; set; } = "";

	public string RawText { get; set; } = "";

	public string FormattedText { get; set; } = "";

	public string Tags { get; set; } = "";

	public string SourceLabel
	{
		get
		{
			if (EngineId.Equals("cloud-stt", StringComparison.OrdinalIgnoreCase))
			{
				string text = (string.IsNullOrWhiteSpace(CloudSttModel) ? "cloud STT" : CloudSttModel.Trim());
				return "Cloud STT - " + text;
			}
			string text2 = FirstNonEmpty(TranscriptionModelName, TranscriptionModelId);
			if (!string.IsNullOrWhiteSpace(text2))
			{
				return "Local Whisper - " + text2;
			}
			return "Manual or legacy";
		}
	}

	public string AudioStatus
	{
		get
		{
			if (!string.IsNullOrWhiteSpace(AudioPath))
			{
				return "Audio saved";
			}
			return "No audio saved";
		}
	}

	public string Preview
	{
		get
		{
			string text = FormattedText.Trim();
			if (text.Length > 140)
			{
				return string.Concat(text.AsSpan(0, 140), "...".AsSpan());
			}
			return text;
		}
	}

	private static string FirstNonEmpty(params string[] values)
	{
		return values.FirstOrDefault((string value) => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
	}
}
