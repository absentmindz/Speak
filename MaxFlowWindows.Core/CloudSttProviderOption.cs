using System;
using System.Collections.Generic;
using System.Linq;

namespace MaxFlowWindows.Core;

public sealed class CloudSttProviderOption
{
	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	public string Subtitle { get; set; } = "";

	public string DefaultEndpoint { get; set; } = "";

	public string DefaultModel { get; set; } = "";

	public string DefaultApiKeyEnvironmentVariable { get; set; } = "";

	public static IReadOnlyList<CloudSttProviderOption> Presets { get; } = new List<CloudSttProviderOption>
	{
		new CloudSttProviderOption
		{
			Id = "groq",
			Name = "Groq STT",
			Subtitle = "Fast OpenAI-compatible speech-to-text",
			DefaultEndpoint = "https://api.groq.com/openai/v1",
			DefaultModel = "whisper-large-v3-turbo",
			DefaultApiKeyEnvironmentVariable = "GROQ_API_KEY"
		},
		new CloudSttProviderOption
		{
			Id = "cloud-openai",
			Name = "OpenAI STT",
			Subtitle = "OpenAI audio transcription models",
			DefaultEndpoint = "https://api.openai.com/v1",
			DefaultModel = "gpt-4o-transcribe",
			DefaultApiKeyEnvironmentVariable = "OPENAI_API_KEY"
		}
	};

	public static CloudSttProviderOption Find(string? id)
	{
		return Presets.FirstOrDefault((CloudSttProviderOption option) => option.Id.Equals(id ?? "", StringComparison.OrdinalIgnoreCase)) ?? Presets[0];
	}
}
