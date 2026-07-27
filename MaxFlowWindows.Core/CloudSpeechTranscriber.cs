using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MaxFlowWindows.Core;

public sealed class CloudSpeechTranscriber : IDisposable
{
	private readonly HttpClient _client = new HttpClient
	{
		Timeout = TimeSpan.FromMinutes(3.0)
	};

	private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

	public async Task<string> TranscribeAsync(string audioPath, MaxFlowSettings settings, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!File.Exists(audioPath))
		{
			throw new FileNotFoundException("Audio file was missing.", audioPath);
		}
		CloudSttProviderOption cloudSttProviderOption = CloudSttProviderOption.Find(settings.SttCloudProviderId);
		string text = FirstNonEmpty(settings.SttCloudEndpoint, cloudSttProviderOption.DefaultEndpoint);
		string text2 = FirstNonEmpty(settings.SttCloudModel, cloudSttProviderOption.DefaultModel);
		string text3 = FirstNonEmpty(settings.SttCloudApiKeyEnvironmentVariable, cloudSttProviderOption.DefaultApiKeyEnvironmentVariable);
		if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(text2))
		{
			throw new InvalidOperationException("Cloud STT is missing endpoint or model.");
		}
		string text4 = (string.IsNullOrWhiteSpace(text3) ? "" : GetEnvironmentVariableValue(text3));
		if (string.IsNullOrWhiteSpace(text4))
		{
			throw new InvalidOperationException("Cloud STT is missing API key env var " + text3 + ".");
		}
		string result;
		await using (FileStream stream = File.OpenRead(audioPath))
		{
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, BuildTranscriptionsUri(text));
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", text4);
			using MultipartFormDataContent form = new MultipartFormDataContent();
			using StreamContent audio = new StreamContent(stream);
			audio.Headers.ContentType = new MediaTypeHeaderValue(ContentTypeForAudio(audioPath));
			form.Add(audio, "file", Path.GetFileName(audioPath));
			form.Add(new StringContent(text2), "model");
			form.Add(new StringContent("json"), "response_format");
			string text5 = WhisperLanguageFromLocale(settings.LocaleId);
			if (!string.IsNullOrWhiteSpace(text5))
			{
				form.Add(new StringContent(text5), "language");
			}
			request.Content = form;
			using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken);
			string body = await response.Content.ReadAsStringAsync(cancellationToken);
			if (!response.IsSuccessStatusCode)
			{
				throw new InvalidOperationException($"Cloud STT returned {response.StatusCode}: {ExtractError(body)}");
			}
			string text6 = ExtractTranscriptText(body);
			if (string.IsNullOrWhiteSpace(text6))
			{
				throw new InvalidOperationException("Cloud STT returned an empty transcript.");
			}
			result = text6.Trim();
		}
		return result;
	}

	public void Dispose()
	{
		_client.Dispose();
	}

	private static Uri BuildTranscriptionsUri(string endpoint)
	{
		string text = endpoint.Trim().TrimEnd('/');
		Uri uri;
		if (text.EndsWith("/audio/transcriptions", StringComparison.OrdinalIgnoreCase))
		{
			uri = new Uri(text, UriKind.Absolute);
		}
		else
		{
			uri = new Uri(text + "/audio/transcriptions", UriKind.Absolute);
		}
		return EndpointSecurity.RequireHttpsOrLoopback(uri, "Cloud STT");
	}

	private static string ContentTypeForAudio(string audioPath)
	{
		return Path.GetExtension(audioPath).ToLowerInvariant() switch
		{
			".mp3" => "audio/mpeg", 
			".m4a" => "audio/mp4", 
			".mp4" => "audio/mp4", 
			".ogg" => "audio/ogg", 
			".webm" => "audio/webm", 
			".flac" => "audio/flac", 
			".wav" => "audio/wav", 
			_ => "application/octet-stream", 
		};
	}

	private static string ExtractTranscriptText(string body)
	{
		using JsonDocument jsonDocument = JsonDocument.Parse(body);
		if (jsonDocument.RootElement.TryGetProperty("text", out var value))
		{
			return value.GetString() ?? "";
		}
		return "";
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

	private static string WhisperLanguageFromLocale(string locale)
	{
		if (string.IsNullOrWhiteSpace(locale))
		{
			return "en";
		}
		string text = locale.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "en";
		if (!text.Equals("auto", StringComparison.OrdinalIgnoreCase))
		{
			return text;
		}
		return "";
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
