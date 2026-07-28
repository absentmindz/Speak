using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MaxFlowWindows.Core;

public sealed class LlmModelDiscovery : IDisposable
{
	private readonly HttpClient _client = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(20.0)
	};

	private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

	public async Task<LlmModelDiscoveryResult> LoadModelsAsync(MaxFlowSettings settings, CancellationToken cancellationToken = default(CancellationToken))
	{
		LlmPolishProviderOption provider = LlmPolishProviderOption.Find(settings.LlmPolishProviderId);
		IReadOnlyList<string> fallback = FallbackModels(provider.Id);
		if (provider.Id.Equals("off", StringComparison.OrdinalIgnoreCase))
		{
			return new LlmModelDiscoveryResult(fallback, "LLM polish is off.", UsedFallback: true);
		}
		string text = FirstNonEmpty(settings.LlmPolishEndpoint, provider.DefaultEndpoint);
		if (string.IsNullOrWhiteSpace(text))
		{
			return new LlmModelDiscoveryResult(fallback, "Provider endpoint is empty.", UsedFallback: true);
		}
		string text2 = "";
		if (provider.RequiresApiKey)
		{
			string text3 = FirstNonEmpty(settings.LlmPolishApiKeyEnvironmentVariable, provider.DefaultApiKeyEnvironmentVariable);
			text2 = (string.IsNullOrWhiteSpace(text3) ? "" : GetEnvironmentVariableValue(text3));
			if (string.IsNullOrWhiteSpace(text2))
			{
				return new LlmModelDiscoveryResult(fallback, "Missing API key env var " + text3 + ".", UsedFallback: true);
			}
		}
		try
		{
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, BuildModelsUri(text));
			if (!string.IsNullOrWhiteSpace(text2))
			{
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", text2);
			}
			using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);
			string body = await response.Content.ReadAsStringAsync(cancellationToken);
			if (!response.IsSuccessStatusCode)
			{
				return new LlmModelDiscoveryResult(fallback, $"Models request returned {response.StatusCode}: {ExtractError(body)}", UsedFallback: true);
			}
			List<string> list = ExtractModelIds(body).Where(IsUsefulTextModel).Distinct<string>(StringComparer.OrdinalIgnoreCase).OrderBy<string, string>((string model) => ModelSortKey(provider.Id, model), StringComparer.OrdinalIgnoreCase)
				.ThenBy<string, string>((string model) => model, StringComparer.OrdinalIgnoreCase)
				.ToList();
			if (list.Count == 0)
			{
				return new LlmModelDiscoveryResult(fallback, "No text models found in provider response.", UsedFallback: true);
			}
			return new LlmModelDiscoveryResult(list, $"Loaded {list.Count} models from {provider.Name}.", UsedFallback: false);
		}
		catch (Exception ex) when (((ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException || ex is InvalidOperationException) ? 1 : 0) != 0)
		{
			return new LlmModelDiscoveryResult(fallback, "Model discovery failed: " + ex.Message, UsedFallback: true);
		}
	}

	public async Task<LlmModelDiscoveryResult> LoadSpeechModelsAsync(MaxFlowSettings settings, CancellationToken cancellationToken = default(CancellationToken))
	{
		CloudSttProviderOption provider = CloudSttProviderOption.Find(settings.SttCloudProviderId);
		IReadOnlyList<string> fallback = FallbackSpeechModels(provider.Id);
		string text = FirstNonEmpty(settings.SttCloudEndpoint, provider.DefaultEndpoint);
		if (string.IsNullOrWhiteSpace(text))
		{
			return new LlmModelDiscoveryResult(fallback, "Cloud STT endpoint is empty.", UsedFallback: true);
		}
		string text2 = FirstNonEmpty(settings.SttCloudApiKeyEnvironmentVariable, provider.DefaultApiKeyEnvironmentVariable);
		string text3 = (string.IsNullOrWhiteSpace(text2) ? "" : GetEnvironmentVariableValue(text2));
		if (string.IsNullOrWhiteSpace(text3))
		{
			return new LlmModelDiscoveryResult(fallback, "Missing API key env var " + text2 + ".", UsedFallback: true);
		}
		try
		{
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, BuildModelsUri(text));
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", text3);
			using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);
			string body = await response.Content.ReadAsStringAsync(cancellationToken);
			if (!response.IsSuccessStatusCode)
			{
				return new LlmModelDiscoveryResult(fallback, $"STT models request returned {response.StatusCode}: {ExtractError(body)}", UsedFallback: true);
			}
			List<string> list = ExtractModelIds(body).Where(IsUsefulSpeechModel).Distinct<string>(StringComparer.OrdinalIgnoreCase).OrderBy<string, string>(SpeechModelSortKey, StringComparer.OrdinalIgnoreCase)
				.ThenBy<string, string>((string model) => model, StringComparer.OrdinalIgnoreCase)
				.ToList();
			if (list.Count == 0)
			{
				return new LlmModelDiscoveryResult(fallback, "No speech-to-text models found in provider response.", UsedFallback: true);
			}
			return new LlmModelDiscoveryResult(list, $"Loaded {list.Count} STT models from {provider.Name}.", UsedFallback: false);
		}
		catch (Exception ex) when (((ex is HttpRequestException || ex is TaskCanceledException || ex is JsonException || ex is InvalidOperationException) ? 1 : 0) != 0)
		{
			return new LlmModelDiscoveryResult(fallback, "STT model discovery failed: " + ex.Message, UsedFallback: true);
		}
	}

	public void Dispose()
	{
		_client.Dispose();
	}

	private static Uri BuildModelsUri(string endpoint)
	{
		string text = endpoint.Trim().TrimEnd('/');
		if (text.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
		{
			string text2 = text;
			int length = "/chat/completions".Length;
			text = text2.Substring(0, text2.Length - length);
		}
		if (text.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
		{
			return EndpointSecurity.RequireHttpsOrLoopback(new Uri(text, UriKind.Absolute), "Model discovery");
		}
		return EndpointSecurity.RequireHttpsOrLoopback(new Uri(text + "/models", UriKind.Absolute), "Model discovery");
	}

	private static IReadOnlyList<string> ExtractModelIds(string body)
	{
		using JsonDocument jsonDocument = JsonDocument.Parse(body);
		if (!jsonDocument.RootElement.TryGetProperty("data", out var value) || value.ValueKind != JsonValueKind.Array)
		{
			return Array.Empty<string>();
		}
		List<string> list = new List<string>();
		foreach (JsonElement item in value.EnumerateArray())
		{
			if ((!item.TryGetProperty("active", out var value2) || value2.ValueKind != JsonValueKind.False) && item.TryGetProperty("id", out var value3))
			{
				string text = value3.GetString();
				if (!string.IsNullOrWhiteSpace(text))
				{
					list.Add(text.Trim());
				}
			}
		}
		return list;
	}

	private static bool IsUsefulTextModel(string model)
	{
		string text = model.ToLowerInvariant();
		if (!text.Contains("whisper", StringComparison.Ordinal) && !text.Contains("tts", StringComparison.Ordinal) && !text.Contains("guard", StringComparison.Ordinal) && !text.Contains("safeguard", StringComparison.Ordinal) && !text.Contains("orpheus", StringComparison.Ordinal) && !text.Contains("canopylabs", StringComparison.Ordinal) && !text.Contains("embedding", StringComparison.Ordinal))
		{
			return !text.Contains("moderation", StringComparison.Ordinal);
		}
		return false;
	}

	private static bool IsUsefulSpeechModel(string model)
	{
		string text = model.ToLowerInvariant();
		if ((text.Contains("whisper", StringComparison.Ordinal) || text.Contains("transcribe", StringComparison.Ordinal)) && !text.Contains("tts", StringComparison.Ordinal) && !text.Contains("speech-preview", StringComparison.Ordinal) && !text.Contains("guard", StringComparison.Ordinal) && !text.Contains("embedding", StringComparison.Ordinal))
		{
			return !text.Contains("moderation", StringComparison.Ordinal);
		}
		return false;
	}

	private static string ModelSortKey(string providerId, string model)
	{
		if (providerId.Equals("lm-studio", StringComparison.OrdinalIgnoreCase))
		{
			return LmStudioModelSortKey(model);
		}
		return CloudTextModelSortKey(model);
	}

	private static string LmStudioModelSortKey(string model)
	{
		string text = model.ToLowerInvariant();
		if (text.Equals("google/gemma-4-e4b", StringComparison.Ordinal))
		{
			return "00";
		}
		if (text.Equals("google/gemma-4-12b-qat", StringComparison.Ordinal))
		{
			return "01";
		}
		if (text.Equals("qwen/qwen3.5-9b", StringComparison.Ordinal))
		{
			return "02";
		}
		if (text.Equals("openai/gpt-oss-20b", StringComparison.Ordinal))
		{
			return "03";
		}
		if (text.Contains("gpt-oss-20b", StringComparison.Ordinal))
		{
			return "04";
		}
		if (text.Contains("qwen3.6-35b", StringComparison.Ordinal))
		{
			return "05";
		}
		return "50";
	}

	private static string CloudTextModelSortKey(string model)
	{
		string text = model.ToLowerInvariant();
		if (text.Contains("gpt-oss-120b", StringComparison.Ordinal))
		{
			return "00";
		}
		if (text.Contains("qwen3", StringComparison.Ordinal))
		{
			return "01";
		}
		if (text.Contains("compound", StringComparison.Ordinal))
		{
			return "02";
		}
		if (text.Contains("gpt-oss-20b", StringComparison.Ordinal))
		{
			return "03";
		}
		if (text.Contains("allam", StringComparison.Ordinal))
		{
			return "04";
		}
		if (text.Contains("kimi", StringComparison.Ordinal))
		{
			return "05";
		}
		if (text.Contains("llama-4", StringComparison.Ordinal))
		{
			return "06";
		}
		if (text.Contains("llama-3.3", StringComparison.Ordinal))
		{
			return "07";
		}
		if (text.Contains("llama-3.1", StringComparison.Ordinal))
		{
			return "08";
		}
		return "50";
	}

	private static string[] FallbackModels(string providerId)
	{
		return providerId switch
		{
			"groq" => new string[9] { "openai/gpt-oss-120b", "qwen/qwen3-32b", "groq/compound", "groq/compound-mini", "openai/gpt-oss-20b", "allam-2-7b", "meta-llama/llama-4-scout-17b-16e-instruct", "llama-3.3-70b-versatile", "llama-3.1-8b-instant" }, 
			"cloud-openai" => new string[4] { "gpt-4.1-mini", "gpt-4.1", "gpt-4o-mini", "gpt-4o" }, 
			"lm-studio" => new string[6] { "google/gemma-4-e4b", "google/gemma-4-12b-qat", "qwen/qwen3.5-9b", "openai/gpt-oss-20b", "openai-gpt-oss-20b-abliterated-uncensored-neo-imatrix", "qwen3.6-35b-a3b-uncensored-hauhaucs-aggressive" }, 
			"local-openai" => new string[3] { "gemma4:31b-cloud", "qwen3:latest", "llama3.1:8b" }, 
			_ => Array.Empty<string>(), 
		};
	}

	private static string SpeechModelSortKey(string model)
	{
		string text = model.ToLowerInvariant();
		if (text.Contains("large-v3-turbo", StringComparison.Ordinal))
		{
			return "00";
		}
		if (text.Contains("gpt-4o-transcribe", StringComparison.Ordinal) && !text.Contains("mini", StringComparison.Ordinal) && !text.Contains("diarize", StringComparison.Ordinal))
		{
			return "01";
		}
		if (text.Contains("gpt-4o-mini-transcribe", StringComparison.Ordinal))
		{
			return "02";
		}
		if (text.Contains("large-v3", StringComparison.Ordinal))
		{
			return "03";
		}
		if (text.Contains("whisper-1", StringComparison.Ordinal))
		{
			return "04";
		}
		if (text.Contains("diarize", StringComparison.Ordinal))
		{
			return "05";
		}
		return "50";
	}

	private static string[] FallbackSpeechModels(string providerId)
	{
		if (!(providerId == "groq"))
		{
			if (providerId == "cloud-openai")
			{
				return new string[4] { "gpt-4o-transcribe", "gpt-4o-mini-transcribe", "whisper-1", "gpt-4o-transcribe-diarize" };
			}
			return Array.Empty<string>();
		}
		return new string[2] { "whisper-large-v3-turbo", "whisper-large-v3" };
	}

	private static string ExtractError(string body)
	{
		if (string.IsNullOrWhiteSpace(body))
		{
			return "empty response";
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(body);
			if (jsonDocument.RootElement.TryGetProperty("error", out var value))
			{
				if (value.ValueKind == JsonValueKind.String)
				{
					return value.GetString() ?? body;
				}
				if (value.TryGetProperty("message", out var value2))
				{
					return value2.GetString() ?? body;
				}
			}
		}
		catch
		{
			return (body.Length <= 220) ? body : body.Substring(0, 220);
		}
		if (body.Length > 220)
		{
			return body.Substring(0, 220);
		}
		return body;
	}

	private static string FirstNonEmpty(params string[] values)
	{
		return values.FirstOrDefault((string value) => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
	}

	private static string GetEnvironmentVariableValue(string name)
	{
		return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process) ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine) ?? "";
	}
}
