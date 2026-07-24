using System;
using System.Collections.Generic;
using System.Linq;

namespace MaxFlowWindows.Core;

public sealed class LlmPolishProviderOption
{
	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	public string Subtitle { get; set; } = "";

	public string DefaultEndpoint { get; set; } = "";

	public string DefaultModel { get; set; } = "";

	public string DefaultApiKeyEnvironmentVariable { get; set; } = "";

	public bool RequiresApiKey { get; set; }

	public static IReadOnlyList<LlmPolishProviderOption> Presets { get; } = new List<LlmPolishProviderOption>
	{
		new LlmPolishProviderOption
		{
			Id = "off",
			Name = "Off",
			Subtitle = "Use Speak's fast local formatter only"
		},
		new LlmPolishProviderOption
		{
			Id = "lm-studio",
			Name = "LM Studio",
			Subtitle = "OpenAI-compatible local server at 127.0.0.1:1234",
			DefaultEndpoint = "http://127.0.0.1:1234/v1",
			DefaultModel = "google/gemma-4-e4b"
		},
		new LlmPolishProviderOption
		{
			Id = "local-openai",
			Name = "Local model",
			Subtitle = "Any local OpenAI-compatible endpoint, for example Ollama",
			DefaultEndpoint = "http://127.0.0.1:11434/v1",
			DefaultModel = "gemma4:31b-cloud"
		},
		new LlmPolishProviderOption
		{
			Id = "cloud-openai",
			Name = "Cloud model",
			Subtitle = "OpenAI-compatible cloud endpoint; requires an API key env var",
			DefaultEndpoint = "https://api.openai.com/v1",
			DefaultModel = "gpt-4.1-mini",
			DefaultApiKeyEnvironmentVariable = "OPENAI_API_KEY",
			RequiresApiKey = true
		},
		new LlmPolishProviderOption
		{
			Id = "groq",
			Name = "Groq",
			Subtitle = "Fast OpenAI-compatible cloud endpoint; requires GROQ_API_KEY",
			DefaultEndpoint = "https://api.groq.com/openai/v1",
			DefaultModel = "openai/gpt-oss-120b",
			DefaultApiKeyEnvironmentVariable = "GROQ_API_KEY",
			RequiresApiKey = true
		}
	};

	public static LlmPolishProviderOption Find(string? id)
	{
		return Presets.FirstOrDefault((LlmPolishProviderOption option) => option.Id.Equals(id ?? "", StringComparison.OrdinalIgnoreCase)) ?? Presets[0];
	}
}
