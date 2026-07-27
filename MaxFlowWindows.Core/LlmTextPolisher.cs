using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MaxFlowWindows.Core;

public sealed class LlmTextPolisher : IDisposable
{
	private readonly HttpClient _client = new HttpClient();

	private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

	public async Task<LlmPolishResult> PolishAsync(string rawTranscript, string locallyFormatted, DictationMode mode, MaxFlowSettings settings, IEnumerable<VocabularyEntry> vocabulary, CancellationToken cancellationToken = default(CancellationToken))
	{
		LlmPolishProviderOption provider = LlmPolishProviderOption.Find(settings.LlmPolishProviderId);
		if (provider.Id.Equals("off", StringComparison.OrdinalIgnoreCase))
		{
			return LlmPolishResult.Skipped(locallyFormatted);
		}
		string text = FirstNonEmpty(settings.LlmPolishEndpoint, provider.DefaultEndpoint);
		string text2 = FirstNonEmpty(settings.LlmPolishModel, provider.DefaultModel);
		if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(text2))
		{
			return LlmPolishResult.Fallback(locallyFormatted, "LLM polish is missing an endpoint or model.");
		}
		string text3 = "";
		if (provider.RequiresApiKey)
		{
			string text4 = FirstNonEmpty(settings.LlmPolishApiKeyEnvironmentVariable, provider.DefaultApiKeyEnvironmentVariable);
			text3 = (string.IsNullOrWhiteSpace(text4) ? "" : GetEnvironmentVariableValue(text4));
			if (string.IsNullOrWhiteSpace(text3))
			{
				return LlmPolishResult.Fallback(locallyFormatted, "Cloud polish is missing API key env var " + text4 + ".");
			}
		}
		try
		{
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.LlmPolishTimeoutSeconds, 3, 60)));
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, BuildChatCompletionsUri(text));
			if (!string.IsNullOrWhiteSpace(text3))
			{
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", text3);
			}
			var value = new
			{
				model = text2,
				temperature = 0.2,
				max_tokens = Math.Clamp(Math.Max(1200, rawTranscript.Length), 1200, 4000),
				stream = false,
				messages = new[]
				{
					new
					{
						role = "system",
						content = "You are Speak's dictation polisher. Rewrite the dictated text using the selected mode. Make the result clean, punctuated, and easy to paste. Preserve the speaker's meaning, names, technical terms, URLs, numbers, code, and dictionary spellings. Use the selected mode actively: Smart is clean natural text, Message is chat-ready, Email is a concise email, Prompt is organized tasks and requirements, Notes is bullets, and Raw stays close. Return only the finished polished text."
					},
					new
					{
						role = "user",
						content = $"Mode: {mode.Name}\nMode instruction: {mode.Instruction}\n\nMode-specific rules:\n{BuildModeGuidance(mode)}\n\nRaw transcript:\n{rawTranscript.Trim()}\n\nSpeak local formatted draft:\n{locallyFormatted.Trim()}\n\n" + "Rewrite the dictated text using the selected mode. Use the local draft as a helpful starting point, not as a limit."
					}
				}
			};
			request.Content = new StringContent(JsonSerializer.Serialize(value, _jsonOptions), Encoding.UTF8, "application/json");
			using HttpResponseMessage response = await _client.SendAsync(request, timeout.Token);
			string body = await response.Content.ReadAsStringAsync(timeout.Token);
			if (!response.IsSuccessStatusCode)
			{
				return LlmPolishResult.Fallback(locallyFormatted, $"LLM polish returned {response.StatusCode}: {ExtractError(body)}");
			}
			(string content, string finishReason) = ExtractMessage(body);
			string text5 = content.Trim();
			if (string.IsNullOrWhiteSpace(text5))
			{
				return LlmPolishResult.Fallback(locallyFormatted, "LLM polish returned empty text.");
			}
			if (finishReason.Equals("length", StringComparison.OrdinalIgnoreCase))
			{
				return LlmPolishResult.Fallback(locallyFormatted, "LLM polish hit its output limit, so Speak kept the complete local draft.");
			}
			string text6 = PolishResponseGuard.SafePolishedText(rawTranscript, locallyFormatted, text5);
			if (!string.Equals(text6, text5.Trim(), StringComparison.Ordinal))
			{
				text6 = LocalTextFormatter.ApplyVocabulary(text6, vocabulary).Trim();
				return LlmPolishResult.Fallback(text6, "Polish model looked like a reply, so Speak kept the dictated text.");
			}
			text5 = text6;
			text5 = LocalTextFormatter.ApplyVocabulary(text5, vocabulary).Trim();
			return LlmPolishResult.Completed(text5, provider.Name);
		}
		catch (OperationCanceledException)
		{
			return LlmPolishResult.Fallback(locallyFormatted, "LLM polish timed out.");
		}
		catch (Exception ex2)
		{
			return LlmPolishResult.Fallback(locallyFormatted, ex2.Message);
		}
	}

	public void Dispose()
	{
		_client.Dispose();
	}

	private static Uri BuildChatCompletionsUri(string endpoint)
	{
		string text = endpoint.Trim().TrimEnd('/');
		Uri uri;
		if (text.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
		{
			uri = new Uri(text, UriKind.Absolute);
		}
		else
		{
			uri = new Uri(text + "/chat/completions", UriKind.Absolute);
		}
		return EndpointSecurity.RequireHttpsOrLoopback(uri, "LLM polish");
	}

	private static (string Content, string FinishReason) ExtractMessage(string body)
	{
		using JsonDocument jsonDocument = JsonDocument.Parse(body);
		JsonElement property = jsonDocument.RootElement.GetProperty("choices");
		if (property.GetArrayLength() == 0)
		{
			return ("", "");
		}
		JsonElement jsonElement = property[0];
		string finishReason = jsonElement.TryGetProperty("finish_reason", out JsonElement finish)
			? finish.GetString() ?? ""
			: "";
		if (jsonElement.TryGetProperty("message", out var value) && value.TryGetProperty("content", out var value2))
		{
			return (value2.GetString() ?? "", finishReason);
		}
		if (jsonElement.TryGetProperty("text", out var value3))
		{
			return (value3.GetString() ?? "", finishReason);
		}
		return ("", finishReason);
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
			return (body.Length <= 240) ? body : body.Substring(0, 240);
		}
		if (body.Length > 240)
		{
			return body.Substring(0, 240);
		}
		return body;
	}

	private static string FirstNonEmpty(params string[] values)
	{
		return values.FirstOrDefault((string value) => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
	}

	private static string BuildModeGuidance(DictationMode mode)
	{
		return mode.Id.ToLowerInvariant() switch
		{
			"message" => "- Write like a concise human chat reply.\n- Keep it warm but not formal.\n- Keep the speaker's wording natural and easy to paste.\n- Do not add greetings, signatures, or subject lines.", 
			"email" => "- Use a clean greeting, short paragraphs, and a simple close.\n- Keep it professional and direct.\n- Organize the speaker's points without inventing extra detail.\n- Do not over-expand the message.", 
			"prompt" => "- Preserve all requirements and technical terms.\n- Use a clear task followed by numbered requirements when useful.\n- Do not remove constraints, model names, paths, or commands.", 
			"notes" => "- Convert ideas into short scannable bullets.\n- Preserve action items and names.\n- Keep the original meaning and sequence.\n- Avoid email or chat wording.", 
			"raw" => "- Keep wording close to the transcript.\n- Only fix obvious vocabulary, spacing, and punctuation.\n- Do not rewrite style.", 
			_ => "- Clean filler words and spoken punctuation.\n- Use natural paragraphs for longer dictation and make the output easy to paste.\n- Preserve tone, meaning, and important terms.", 
		};
	}

	private static string GetEnvironmentVariableValue(string name)
	{
		return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process) ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine) ?? "";
	}
}
