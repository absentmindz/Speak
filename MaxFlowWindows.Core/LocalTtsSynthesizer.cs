using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MaxFlowWindows.Core;

public sealed class LocalTtsSynthesizer
{
	private static readonly AppConfig _config = LoadConfig();
	private static string WorkspaceRoot => _config.Paths.WorkspaceRoot;

	private static AppConfig LoadConfig()
	{
		try
		{
			return AppConfig.Load();
		}
		catch
		{
			return new AppConfig();
		}
	}

	private static readonly Dictionary<string, int> WorkerPorts = new(StringComparer.OrdinalIgnoreCase)
	{
		["qwen3-customvoice-1.7b"] = EndpointSecurity.ResolveLoopbackPort(
			"SPEAK_QWEN_CUSTOMVOICE_WORKER_PORT", 8766),
		["qwen3-base-1.7b"] = EndpointSecurity.ResolveLoopbackPort(
			"SPEAK_QWEN_BASE_WORKER_PORT", 8767),
	};

	private const string UnifiedWorkerScript = "tools\\speak_worker.py";

	private static readonly Dictionary<string, string> LegacyWorkerScripts = new(StringComparer.OrdinalIgnoreCase)
	{
		["qwen3-customvoice-1.7b"] = "tools\\qwen3-tts\\qwen3_tts_worker.py",
		["qwen3-base-1.7b"] = "tools\\qwen3-tts\\qwen3_tts_worker.py",
	};

	private static readonly Dictionary<string, string> ChatterboxModelNames = new(StringComparer.OrdinalIgnoreCase)
	{
		["chatterbox-turbo"] = "turbo",
		["chatterbox-multilingual-v3"] = "multilingual-v3",
	};

	private static readonly HttpClient _workerHttp = new()
	{
		Timeout = TimeSpan.FromMinutes(45)
	};
	private static readonly SemaphoreSlim _workerGate = new(1, 1);
	private static TtsWorkerContext? _activeWorker;

	private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
	private readonly Func<Process, bool>? _processRegistrar;

	public LocalTtsSynthesizer(Func<Process, bool>? processRegistrar = null)
	{
		_processRegistrar = processRegistrar;
	}

	private sealed record TtsWorkerContext(
		string EngineId,
		Process Process,
		int Port,
		int KeepAliveMinutes,
		TaskCompletionSource<bool> ReadySource,
		CancellationTokenSource ShutdownCts,
		string AuthToken
	);

	public async Task<TtsSynthesisResult> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(request.Text))
		{
			throw new InvalidOperationException("There is no text to speak.");
		}

		TtsEngineOption engine = TtsEngineOption.Find(request.EngineId);
		request.EngineId = engine.Id;
		if (!engine.SupportsDirectSpeech)
		{
			throw new InvalidOperationException(engine.Name + " is installed but reserved for cloning/design. Use Qwen3 CustomVoice, Qwen3 Base, or Tortoise to speak text.");
		}
		if (!engine.IsRuntimeReady())
		{
			throw new FileNotFoundException(engine.Name + " runtime is not configured or missing.", string.IsNullOrWhiteSpace(engine.RuntimePath) ? "(empty)" : engine.RuntimePath);
		}
		if (!engine.IsModelReady())
		{
			throw new DirectoryNotFoundException("TTS model not found: " + engine.ModelPath);
		}
		if (!HasSynthesisScript(engine.Id))
		{
			throw new FileNotFoundException(engine.Name + " helper script is not installed.", ExpectedSynthesisScript(engine.Id));
		}

		string outputRoot = string.IsNullOrWhiteSpace(request.OutputRoot)
			? Path.Combine(SpeakDataPaths.ResolveDataRoot(), "tts", "outputs")
			: request.OutputRoot;
		Directory.CreateDirectory(outputRoot);

		Stopwatch stopwatch = Stopwatch.StartNew();
		bool useWorker = CanWarmUp(request.EngineId)
			&& await WarmUpAsync(request.EngineId, request.ModelKeepAliveMinutes, cancellationToken);
		string output;
		if (useWorker)
		{
			output = await SynthesizeViaWorkerAsync(request, outputRoot, cancellationToken);
		}
		else
		{
			output = engine.Id switch
			{
				"qwen3-customvoice-1.7b" => await SynthesizeQwenAsync(request, outputRoot, engine.ModelPath, cancellationToken),
				"qwen3-base-1.7b" => await SynthesizeQwenAsync(request, outputRoot, engine.ModelPath, cancellationToken),
				"tortoise-ultra-fast" => await SynthesizeTortoiseAsync(request, outputRoot, cancellationToken),
				_ => await SynthesizeChatterboxAsync(request, outputRoot, cancellationToken)
			};
		}
		stopwatch.Stop();

		if (!File.Exists(output))
		{
			throw new FileNotFoundException("TTS completed but no output file was found.", output);
		}

		return new TtsSynthesisResult
		{
			OutputPath = output,
			EngineName = engine.Name,
			VoiceName = request.VoiceId,
			ElapsedSeconds = stopwatch.Elapsed.TotalSeconds
		};
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage(
		"Performance",
		"CA1822:Mark members as static",
		Justification = "This method is part of the instance-oriented TTS service API.")]
	public string DescribeAvailability(string engineId)
	{
		TtsEngineOption engine = TtsEngineOption.Find(engineId);
		string runtime = engine.IsRuntimeReady() ? "runtime ready" : "runtime missing";
		string model = engine.IsModelReady() ? "model ready" : "model missing";
		string script = HasSynthesisScript(engine.Id) ? "helper ready" : "helper missing";
		string mode = engine.SupportsDirectSpeech ? "can speak now" : "installed for later";
		if (string.IsNullOrWhiteSpace(engine.ModelPath))
		{
			return $"{engine.Name}: {runtime}, {script}; {mode}.";
		}
		return $"{engine.Name}: {runtime}, {model}, {script}; {mode}.";
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage(
		"Performance",
		"CA1822:Mark members as static",
		Justification = "This method is part of the instance-oriented TTS service API.")]
	public bool CanWarmUp(string engineId)
	{
		return WorkerPorts.ContainsKey(engineId)
			&& LegacyWorkerScripts.TryGetValue(engineId, out string? script)
			&& File.Exists(ResolvePreferredQwenScript(script, out _));
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage(
		"Performance",
		"CA1822:Mark members as static",
		Justification = "This method is part of the instance-oriented TTS service API.")]
	public bool CanSynthesize(string engineId)
	{
		TtsEngineOption engine = TtsEngineOption.Find(engineId);
		return engine.SupportsDirectSpeech
			&& engine.IsRuntimeReady()
			&& engine.IsModelReady()
			&& HasSynthesisScript(engine.Id);
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage(
		"Performance",
		"CA1822:Mark members as static",
		Justification = "This method is part of the instance-oriented TTS service API.")]
	public bool IsEngineWarm(string engineId)
	{
		TtsWorkerContext? worker = _activeWorker;
		return worker != null
			&& worker.EngineId.Equals(engineId, StringComparison.OrdinalIgnoreCase)
			&& worker.ReadySource.Task.IsCompletedSuccessfully
			&& !worker.Process.HasExited;
	}

	public async Task<bool> WarmUpAsync(string engineId, CancellationToken cancellationToken)
	{
		return await WarmUpAsync(engineId, _config.Transcription.ModelKeepAliveMinutes, cancellationToken);
	}

	public async Task<bool> WarmUpAsync(string engineId, int keepAliveMinutes, CancellationToken cancellationToken)
	{
		keepAliveMinutes = Math.Max(1, keepAliveMinutes);
		if (!WorkerPorts.TryGetValue(engineId, out int port))
		{
			return false;
		}
		if (!LegacyWorkerScripts.TryGetValue(engineId, out string scriptRelPath))
		{
			return false;
		}
		await _workerGate.WaitAsync(cancellationToken);
		try
		{
			if (IsEngineWarm(engineId) && _activeWorker?.KeepAliveMinutes == keepAliveMinutes)
			{
				return true;
			}
			await StopWorkerAsync();
			string scriptPath = ResolvePreferredQwenScript(scriptRelPath, out bool useUnifiedWorker);
			if (!File.Exists(scriptPath))
			{
				return false;
			}
			TtsEngineOption engine = TtsEngineOption.Find(engineId);
			string pythonPath = engine.RuntimePath;
			if (!File.Exists(pythonPath))
			{
				return false;
			}
			TaskCompletionSource<bool> readySource = new TaskCompletionSource<bool>();
			CancellationTokenSource shutdownCts = new CancellationTokenSource();
			string authToken = CreateWorkerToken();
			ProcessStartInfo psi = new ProcessStartInfo
			{
				FileName = pythonPath,
				WorkingDirectory = WorkspaceRoot,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true
			};
			SanitizeChildProcessEnvironment(psi);
			psi.ArgumentList.Add(scriptPath);
			if (useUnifiedWorker)
			{
				psi.ArgumentList.Add("tts");
				psi.ArgumentList.Add("serve");
			}
			psi.ArgumentList.Add("--host");
			psi.ArgumentList.Add("127.0.0.1");
			psi.ArgumentList.Add("--port");
			psi.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
			psi.ArgumentList.Add("--startup-timeout-seconds");
			psi.ArgumentList.Add("600");
			psi.ArgumentList.Add("--idle-minutes");
			psi.ArgumentList.Add(keepAliveMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
			if (engineId.StartsWith("qwen3-", StringComparison.OrdinalIgnoreCase))
			{
				psi.ArgumentList.Add("--model");
				psi.ArgumentList.Add(string.IsNullOrWhiteSpace(engine.ModelPath)
					? _config.TTS.QwenTtsCustomVoiceModelPath
					: engine.ModelPath);
				SetQwenEnvironment(psi);
			}
			psi.ArgumentList.Add("--device");
			psi.ArgumentList.Add("auto");
			psi.Environment["PYTHONUTF8"] = "1";
			psi.Environment["PYTHONIOENCODING"] = "utf-8";
			psi.Environment["SPEAK_WORKER_TOKEN"] = authToken;
			Process process = new Process
			{
				StartInfo = psi,
				EnableRaisingEvents = true
			};
			process.Exited += delegate
			{
				if (!shutdownCts.IsCancellationRequested)
				{
					readySource.TrySetResult(false);
				}
			};
			process.OutputDataReceived += delegate(object _, DataReceivedEventArgs args)
			{
				if (!string.IsNullOrWhiteSpace(args.Data))
				{
					try
					{
						JsonElement json = JsonSerializer.Deserialize<JsonElement>(args.Data, _jsonOptions);
						if (json.TryGetProperty("event", out JsonElement evt) && evt.GetString() == "ready")
						{
							readySource.TrySetResult(true);
						}
					}
					catch
					{
					}
				}
			};
			process.ErrorDataReceived += delegate(object _, DataReceivedEventArgs args)
			{
				if (!string.IsNullOrWhiteSpace(args.Data))
				{
					AppLog.Warn("TTS worker error: " + args.Data);
				}
			};
			if (!process.Start())
			{
				shutdownCts.Dispose();
				return false;
			}
			if (_processRegistrar != null && !_processRegistrar(process))
			{
				AppLog.Warn("Warm TTS worker could not be assigned to the Speak process job.");
			}
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
			TtsWorkerContext context = new TtsWorkerContext(engineId, process, port, keepAliveMinutes, readySource, shutdownCts, authToken);
			_activeWorker = context;
			Task<bool> readyTask = readySource.Task;
			Task timeoutTask = Task.Delay(TimeSpan.FromMinutes(10), cancellationToken);
			if (await Task.WhenAny(readyTask, timeoutTask) != readyTask)
			{
				await StopWorkerAsync();
				return false;
			}
			return await readyTask;
		}
		finally
		{
			_workerGate.Release();
		}
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage(
		"Performance",
		"CA1822:Mark members as static",
		Justification = "This method is part of the instance-oriented TTS service API.")]
	public async Task StopWarmEngineAsync()
	{
		await _workerGate.WaitAsync();
		try
		{
			await StopWorkerAsync();
		}
		finally
		{
			_workerGate.Release();
		}
	}

	private static async Task StopWorkerAsync()
	{
		TtsWorkerContext? worker = Interlocked.Exchange(ref _activeWorker, null);
		if (worker == null)
		{
			return;
		}
		worker.ShutdownCts.Cancel();
		try
		{
			using HttpClient client = new HttpClient
			{
				Timeout = TimeSpan.FromSeconds(3)
			};
			using HttpResponseMessage response = await SendWorkerRequestAsync(
				client,
				$"http://127.0.0.1:{worker.Port}/shutdown",
				"{}",
				worker.AuthToken,
				CancellationToken.None);
		}
		catch
		{
		}
		await Task.Delay(200);
		try
		{
			if (!worker.Process.HasExited)
			{
				using Process? killer = Process.Start(new ProcessStartInfo
				{
					FileName = "taskkill.exe",
					Arguments = $"/PID {worker.Process.Id} /T /F",
					CreateNoWindow = true,
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true
				});
				killer?.WaitForExit(2000);
			}
		}
		catch
		{
		}
		worker.ShutdownCts.Dispose();
	}

	private async Task<string> SynthesizeViaWorkerAsync(TtsSynthesisRequest request, string outputRoot, CancellationToken cancellationToken)
	{
		TtsWorkerContext? worker = _activeWorker;
		if (worker == null || !worker.EngineId.Equals(request.EngineId, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("Warm worker is not available for the selected engine.");
		}
		string output = System.IO.Path.Combine(outputRoot, "warm-" + Stamp() + ".wav");
		Dictionary<string, object> payload = new Dictionary<string, object>
		{
			["text"] = request.Text,
			["output"] = output
		};
		if (worker.EngineId.Equals("qwen3-customvoice-1.7b", StringComparison.OrdinalIgnoreCase))
		{
			payload["speaker"] = string.IsNullOrWhiteSpace(request.VoiceId) ? "Ryan" : request.VoiceId;
			payload["language"] = string.IsNullOrWhiteSpace(request.Language) ? "English" : request.Language;
		}
		if (!string.IsNullOrWhiteSpace(request.VoicePromptPath) && File.Exists(request.VoicePromptPath))
		{
			payload["voicePromptPath"] = request.VoicePromptPath;
		}
		string json = JsonSerializer.Serialize(payload, _jsonOptions);
		using HttpResponseMessage response = await SendWorkerRequestAsync(
			_workerHttp,
			$"http://127.0.0.1:{worker.Port}/say",
			json,
			worker.AuthToken,
			cancellationToken);
		response.EnsureSuccessStatusCode();
		string resultJson = await response.Content.ReadAsStringAsync(cancellationToken);
		JsonElement result = JsonSerializer.Deserialize<JsonElement>(resultJson, _jsonOptions);
		string outputPath = result.GetProperty("output").GetString() ?? output;
		return outputPath;
	}

	private async Task<string> SynthesizeCloneViaWorkerAsync(string text, string refAudio, string language, string output, CancellationToken cancellationToken)
	{
		TtsWorkerContext? worker = _activeWorker;
		if (worker == null || !worker.EngineId.Equals("qwen3-base-1.7b", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("Warm Qwen3 Base worker is not available.");
		}
		Dictionary<string, object> payload = new Dictionary<string, object>
		{
			["text"] = text,
			["output"] = output,
			["ref_audio"] = refAudio,
			["language"] = string.IsNullOrWhiteSpace(language) ? "Auto" : language,
			["ref_text"] = ""
		};
		string json = JsonSerializer.Serialize(payload, _jsonOptions);
		using HttpResponseMessage response = await SendWorkerRequestAsync(
			_workerHttp,
			$"http://127.0.0.1:{worker.Port}/clone",
			json,
			worker.AuthToken,
			cancellationToken);
		response.EnsureSuccessStatusCode();
		string resultJson = await response.Content.ReadAsStringAsync(cancellationToken);
		JsonElement result = JsonSerializer.Deserialize<JsonElement>(resultJson, _jsonOptions);
		return result.GetProperty("output").GetString() ?? output;
	}

	private static async Task<string> SynthesizeChatterboxAsync(TtsSynthesisRequest request, string outputRoot, CancellationToken cancellationToken)
	{
		string output = Path.Combine(outputRoot, "chatterbox-" + Stamp() + ".wav");
		string modelName = ChatterboxModelNames.TryGetValue(request.EngineId, out string m) ? m : "turbo";
		ProcessStartInfo startInfo = new ProcessStartInfo
		{
			FileName = _config.TTS.ChatterboxPythonPath,
			WorkingDirectory = WorkspaceRoot,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		startInfo.ArgumentList.Add(RequireToolScript("tools\\chatter-tts\\chatter_tts_generate.py"));
		startInfo.ArgumentList.Add("--text");
		startInfo.ArgumentList.Add(request.Text);
		startInfo.ArgumentList.Add("--output");
		startInfo.ArgumentList.Add(output);
		startInfo.ArgumentList.Add("--model");
		startInfo.ArgumentList.Add(modelName);
		startInfo.ArgumentList.Add("--device");
		startInfo.ArgumentList.Add("auto");
		startInfo.ArgumentList.Add("--speed");
		startInfo.ArgumentList.Add("0.9");
		if (!string.IsNullOrWhiteSpace(request.VoicePromptPath) && File.Exists(request.VoicePromptPath))
		{
			startInfo.ArgumentList.Add("--voice-prompt");
			startInfo.ArgumentList.Add(request.VoicePromptPath);
		}
		SetChatterboxEnvironment(startInfo);
		await RunProcessAsync(startInfo, TimeSpan.FromMinutes(30), cancellationToken);
		return output;
	}

	private static async Task<string> SynthesizeQwenAsync(TtsSynthesisRequest request, string outputRoot, string modelPath, CancellationToken cancellationToken)
	{
		string output = Path.Combine(outputRoot, "qwen3-" + StableFilePart(request.VoiceId) + "-" + Stamp() + ".wav");
		ProcessStartInfo startInfo = new ProcessStartInfo
		{
			FileName = _config.TTS.ComfyUIPythonPath,
			WorkingDirectory = WorkspaceRoot,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		string scriptPath = ResolvePreferredQwenScript("tools\\qwen3_tts_local.py", out bool useUnifiedWorker);
		if (!File.Exists(scriptPath))
		{
			throw new FileNotFoundException("Qwen3 TTS helper script is not installed.", scriptPath);
		}
		startInfo.ArgumentList.Add(scriptPath);
		if (useUnifiedWorker)
		{
			startInfo.ArgumentList.Add("tts");
			startInfo.ArgumentList.Add("say");
			startInfo.ArgumentList.Add("--text");
		}
		startInfo.ArgumentList.Add(request.Text);
		startInfo.ArgumentList.Add("--out");
		startInfo.ArgumentList.Add(output);
		startInfo.ArgumentList.Add("--speaker");
		startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(request.VoiceId) ? "Ryan" : request.VoiceId);
		startInfo.ArgumentList.Add("--language");
		startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(request.Language) ? "English" : request.Language);
		startInfo.ArgumentList.Add("--model");
		startInfo.ArgumentList.Add(modelPath);
		startInfo.ArgumentList.Add("--device");
		startInfo.ArgumentList.Add("auto");
		SetQwenEnvironment(startInfo);
		await RunProcessAsync(startInfo, TimeSpan.FromMinutes(45), cancellationToken);
		return output;
	}

	public async Task<string> SynthesizeVoiceCloneAsync(string text, string refAudio, string cloneEngineId, string language, int keepAliveMinutes, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			throw new InvalidOperationException("There is no text to speak.");
		}
		if (string.IsNullOrWhiteSpace(refAudio) || !File.Exists(refAudio))
		{
			throw new FileNotFoundException("Reference audio not found.", refAudio);
		}

		string outputRoot = Path.Combine(SpeakDataPaths.ResolveDataRoot(), "tts", "clone-outputs");
		Directory.CreateDirectory(outputRoot);
		string output = Path.Combine(outputRoot, "clone-" + Stamp() + ".wav");

		if (string.IsNullOrWhiteSpace(cloneEngineId) || cloneEngineId.Equals("qwen3-base-1.7b", StringComparison.OrdinalIgnoreCase))
		{
			TtsEngineOption engine = TtsEngineOption.Find("qwen3-base-1.7b");
			if (!engine.IsModelReady())
			{
				throw new DirectoryNotFoundException("Qwen3 Base model not found: " + engine.ModelPath);
			}
			bool useWorker = await WarmUpAsync(engine.Id, keepAliveMinutes, cancellationToken);
			if (useWorker)
			{
				return await SynthesizeCloneViaWorkerAsync(text, refAudio, language, output, cancellationToken);
			}
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = _config.TTS.ComfyUIPythonPath,
				WorkingDirectory = WorkspaceRoot,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true
			};
			string scriptPath = ResolvePreferredQwenScript("tools\\qwen3_tts_clone.py", out bool useUnifiedWorker);
			if (!File.Exists(scriptPath))
			{
				throw new FileNotFoundException("Qwen3 TTS clone helper script is not installed.", scriptPath);
			}
			startInfo.ArgumentList.Add(scriptPath);
			if (useUnifiedWorker)
			{
				startInfo.ArgumentList.Add("tts");
				startInfo.ArgumentList.Add("clone");
				startInfo.ArgumentList.Add("--text");
			}
			startInfo.ArgumentList.Add(text);
			startInfo.ArgumentList.Add("--ref-audio");
			startInfo.ArgumentList.Add(refAudio);
			startInfo.ArgumentList.Add("--out");
			startInfo.ArgumentList.Add(output);
			startInfo.ArgumentList.Add("--language");
			startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(language) ? "Auto" : language);
			startInfo.ArgumentList.Add("--model");
			startInfo.ArgumentList.Add(engine.ModelPath);
			startInfo.ArgumentList.Add("--device");
			startInfo.ArgumentList.Add("auto");
			SetQwenEnvironment(startInfo);
			await RunProcessAsync(startInfo, TimeSpan.FromMinutes(45), cancellationToken);
		}
		else if (cloneEngineId.Equals("tortoise-ultra-fast", StringComparison.OrdinalIgnoreCase))
		{
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = _config.TTS.TortoisePythonPath,
				WorkingDirectory = WorkspaceRoot,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true
			};
			startInfo.ArgumentList.Add(RequireToolScript("tools\\tortoise_tts_clone.py"));
			startInfo.ArgumentList.Add(text);
			startInfo.ArgumentList.Add("--ref-audio");
			startInfo.ArgumentList.Add(refAudio);
			startInfo.ArgumentList.Add("--out");
			startInfo.ArgumentList.Add(output);
			startInfo.ArgumentList.Add("--preset");
			startInfo.ArgumentList.Add("ultra_fast");
			SetTortoiseEnvironment(startInfo);
			await RunProcessAsync(startInfo, TimeSpan.FromMinutes(60), cancellationToken);
		}
		else if (cloneEngineId.Equals("chatterbox-multilingual-v3", StringComparison.OrdinalIgnoreCase))
		{
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = _config.TTS.ChatterboxPythonPath,
				WorkingDirectory = WorkspaceRoot,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true
			};
			startInfo.ArgumentList.Add(RequireToolScript("tools\\chatter_tts_clone.py"));
			startInfo.ArgumentList.Add(text);
			startInfo.ArgumentList.Add("--ref-audio");
			startInfo.ArgumentList.Add(refAudio);
			startInfo.ArgumentList.Add("--out");
			startInfo.ArgumentList.Add(output);
			startInfo.ArgumentList.Add("--model");
			startInfo.ArgumentList.Add("multilingual-v3");
			SetChatterboxEnvironment(startInfo);
			await RunProcessAsync(startInfo, TimeSpan.FromMinutes(30), cancellationToken);
		}
		else
		{
			throw new InvalidOperationException("Unknown clone engine: " + cloneEngineId);
		}

		if (!File.Exists(output))
		{
			throw new FileNotFoundException("Voice clone completed but no output file was found.", output);
		}

		return output;
	}

	private static async Task<string> SynthesizeTortoiseAsync(TtsSynthesisRequest request, string outputRoot, CancellationToken cancellationToken)
	{
		string runDir = Path.Combine(outputRoot, "tortoise-" + Stamp());
		Directory.CreateDirectory(runDir);
		string tortoiseScript = Path.Combine(_config.Paths.ToolsRoot, "tortoise-tts", "tortoise", "do_tts.py");
		if (!File.Exists(tortoiseScript))
		{
			throw new FileNotFoundException("Tortoise helper script is not installed.", tortoiseScript);
		}
		ProcessStartInfo startInfo = new ProcessStartInfo
		{
			FileName = _config.TTS.TortoisePythonPath,
			WorkingDirectory = Path.Combine(_config.Paths.ToolsRoot, "tortoise-tts", "tortoise"),
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			CreateNoWindow = true
		};
		startInfo.ArgumentList.Add(tortoiseScript);
		startInfo.ArgumentList.Add("--text");
		startInfo.ArgumentList.Add(request.Text);
		startInfo.ArgumentList.Add("--voice");
		startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(request.VoiceId) || request.VoiceId.Equals("default", StringComparison.OrdinalIgnoreCase) ? "daniel" : request.VoiceId);
		startInfo.ArgumentList.Add("--preset");
		startInfo.ArgumentList.Add("ultra_fast");
		startInfo.ArgumentList.Add("--output_path");
		startInfo.ArgumentList.Add(runDir);
		startInfo.ArgumentList.Add("--model_dir");
		startInfo.ArgumentList.Add(_config.TTS.TortoiseModelDir);
		startInfo.ArgumentList.Add("--candidates");
		startInfo.ArgumentList.Add("1");
		startInfo.ArgumentList.Add("--half");
		startInfo.ArgumentList.Add("True");
		startInfo.ArgumentList.Add("--kv_cache");
		startInfo.ArgumentList.Add("True");
		startInfo.ArgumentList.Add("--produce_debug_state");
		startInfo.ArgumentList.Add("False");
		SetTortoiseEnvironment(startInfo);
		await RunProcessAsync(startInfo, TimeSpan.FromMinutes(60), cancellationToken);
		return Directory.EnumerateFiles(runDir, "*.wav", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
			?? Path.Combine(runDir, "output.wav");
	}

	private static async Task RunProcessAsync(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
	{
		SanitizeChildProcessEnvironment(startInfo);
		using Process process = new Process
		{
			StartInfo = startInfo,
			EnableRaisingEvents = true
		};
		StringBuilder stdout = new StringBuilder();
		StringBuilder stderr = new StringBuilder();
		process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs args)
		{
			if (args.Data != null)
			{
				stdout.AppendLine(args.Data);
			}
		};
		process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs args)
		{
			if (args.Data != null)
			{
				stderr.AppendLine(args.Data);
			}
		};
		process.Start();
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		Task waitTask = process.WaitForExitAsync(cancellationToken);
		Task completed = await Task.WhenAny(waitTask, Task.Delay(timeout, cancellationToken));
		if (completed != waitTask)
		{
			TryKill(process);
			throw new TimeoutException("TTS generation timed out.");
		}
		await waitTask;
		if (process.ExitCode != 0)
		{
			string detail = FirstNonEmpty(stderr.ToString().Trim(), stdout.ToString().Trim(), "TTS process failed.");
			throw new InvalidOperationException(detail);
		}
	}

	public static void SanitizeChildProcessEnvironment(ProcessStartInfo startInfo)
	{
		ArgumentNullException.ThrowIfNull(startInfo);
		foreach (string name in startInfo.Environment.Keys.ToArray())
		{
			if (IsSensitiveEnvironmentVariableName(name))
			{
				startInfo.Environment.Remove(name);
			}
		}
	}

	private static bool IsSensitiveEnvironmentVariableName(string name)
	{
		string normalized = name.ToUpperInvariant();
		return normalized.Contains("KEY", StringComparison.Ordinal)
			|| normalized.Contains("TOKEN", StringComparison.Ordinal)
			|| normalized.Contains("SECRET", StringComparison.Ordinal)
			|| normalized.Contains("PASSWORD", StringComparison.Ordinal)
			|| normalized.Contains("PASSWD", StringComparison.Ordinal)
			|| normalized.Contains("CREDENTIAL", StringComparison.Ordinal)
			|| normalized.Contains("AUTH", StringComparison.Ordinal)
			|| normalized.Contains("COOKIE", StringComparison.Ordinal)
			|| normalized.Contains("SESSION", StringComparison.Ordinal);
	}

	private static void SetChatterboxEnvironment(ProcessStartInfo startInfo)
	{
		var modelsRoot = _config.Paths.ModelsRoot;
		startInfo.Environment["HF_HOME"] = Path.Combine(modelsRoot, "chatter-tts");
		startInfo.Environment["HUGGINGFACE_HUB_CACHE"] = Path.Combine(modelsRoot, "chatter-tts", "hub");
		startInfo.Environment["TORCH_HOME"] = Path.Combine(modelsRoot, "chatter-tts", "torch");
		startInfo.Environment["XDG_CACHE_HOME"] = Path.Combine(modelsRoot, "chatter-tts", "xdg");
		startInfo.Environment["HF_HUB_DISABLE_XET"] = "1";
		startInfo.Environment["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1";
		PrependFfmpegPath(startInfo);
	}

	private static void SetQwenEnvironment(ProcessStartInfo startInfo)
	{
		var cacheRoot = _config.Paths.CacheRoot;
		var toolsRoot = _config.Paths.ToolsRoot;
		startInfo.Environment["HF_HOME"] = Path.Combine(cacheRoot, "huggingface");
		startInfo.Environment["HUGGINGFACE_HUB_CACHE"] = Path.Combine(cacheRoot, "huggingface", "hub");
		startInfo.Environment["TRANSFORMERS_CACHE"] = Path.Combine(cacheRoot, "huggingface", "transformers");
		startInfo.Environment["TORCH_HOME"] = Path.Combine(cacheRoot, "torch");
		startInfo.Environment["PYTHONPATH"] = Path.Combine(toolsRoot, "Qwen3-TTS") + ";" + (startInfo.Environment.TryGetValue("PYTHONPATH", out string existing) ? existing : "");
		PrependPath(startInfo, Path.Combine(toolsRoot, "ComfyUI-venv", "Scripts"));
		PrependSoxPath(startInfo);
		PrependFfmpegPath(startInfo);
	}

	private static void SetTortoiseEnvironment(ProcessStartInfo startInfo)
	{
		var modelsRoot = _config.Paths.ModelsRoot;
		var cacheRoot = _config.Paths.CacheRoot;
		startInfo.Environment["HF_HOME"] = Path.Combine(modelsRoot, "tortoise-tts", "hf-home");
		startInfo.Environment["TORCH_HOME"] = Path.Combine(modelsRoot, "tortoise-tts", "torch");
		startInfo.Environment["TORTOISE_MODELS_DIR"] = Path.Combine(modelsRoot, "tortoise-tts", "models");
		startInfo.Environment["XDG_CACHE_HOME"] = Path.Combine(cacheRoot, "tortoise-tts");
		startInfo.Environment["PYTHONUTF8"] = "1";
		startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
		PrependFfmpegPath(startInfo);
	}

	private static void PrependFfmpegPath(ProcessStartInfo startInfo)
	{
		PrependConfiguredOrSystemToolPath(startInfo, "SPEAK_FFMPEG_BIN", "ffmpeg", "bin");
	}

	private static void PrependSoxPath(ProcessStartInfo startInfo)
	{
		string configured = Environment.GetEnvironmentVariable("SPEAK_SOX_BIN") ?? "";
		if (!string.IsNullOrWhiteSpace(configured))
		{
			PrependPath(startInfo, PortablePathResolver.ExpandPath(configured));
		}

		string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
		if (!string.IsNullOrWhiteSpace(programFilesX86))
		{
			PrependPath(startInfo, Path.Combine(programFilesX86, "sox-14-4-2"));
		}
	}

	private static void PrependConfiguredOrSystemToolPath(
		ProcessStartInfo startInfo,
		string environmentVariable,
		params string[] relativeSegments)
	{
		string configured = Environment.GetEnvironmentVariable(environmentVariable) ?? "";
		if (!string.IsNullOrWhiteSpace(configured))
		{
			PrependPath(startInfo, PortablePathResolver.ExpandPath(configured));
		}

		string candidate = Path.GetPathRoot(Environment.SystemDirectory) ?? "";
		if (string.IsNullOrWhiteSpace(candidate))
		{
			return;
		}
		foreach (string segment in relativeSegments)
		{
			candidate = Path.Combine(candidate, segment);
		}
		PrependPath(startInfo, candidate);
	}

	private static void PrependPath(ProcessStartInfo startInfo, string path)
	{
		if (!Directory.Exists(path))
		{
			return;
		}
		string current = startInfo.Environment.TryGetValue("PATH", out string value) ? value : "";
		startInfo.Environment["PATH"] = path + Path.PathSeparator + current;
	}

	private static bool HasSynthesisScript(string engineId)
	{
		string expected = ExpectedSynthesisScript(engineId);
		return !string.IsNullOrWhiteSpace(expected) && File.Exists(expected);
	}

	private static string ExpectedSynthesisScript(string engineId)
	{
		return engineId switch
		{
			"qwen3-customvoice-1.7b" or "qwen3-base-1.7b" => ResolvePreferredQwenScript("tools\\qwen3_tts_local.py", out _),
			"tortoise-ultra-fast" => Path.Combine(_config.Paths.ToolsRoot, "tortoise-tts", "tortoise", "do_tts.py"),
			"chatterbox-turbo" or "chatterbox-multilingual-v3" => ResolveToolScript("tools\\chatter-tts\\chatter_tts_generate.py"),
			_ => ""
		};
	}

	private static string RequireToolScript(string relativePath)
	{
		string scriptPath = ResolveToolScript(relativePath);
		if (!File.Exists(scriptPath))
		{
			throw new FileNotFoundException("TTS helper script is not installed.", scriptPath);
		}
		return scriptPath;
	}

	private static string ResolveToolScript(string relativePath)
	{
		string normalizedRelativePath = relativePath.Replace('\\', Path.DirectorySeparatorChar);
		foreach (string root in ToolRoots())
		{
			if (string.IsNullOrWhiteSpace(root))
			{
				continue;
			}
			string candidate = Path.Combine(root, normalizedRelativePath);
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}
		return Path.Combine(WorkspaceRoot, normalizedRelativePath);
	}

	private static string ResolvePreferredQwenScript(string legacyRelativePath, out bool useUnifiedWorker)
	{
		string unified = ResolveToolScript(UnifiedWorkerScript);
		if (File.Exists(unified))
		{
			useUnifiedWorker = true;
			return unified;
		}

		useUnifiedWorker = false;
		return ResolveToolScript(legacyRelativePath);
	}

	private static IEnumerable<string> ToolRoots()
	{
		yield return WorkspaceRoot;
		yield return AppContext.BaseDirectory;
		yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
		yield return _config.Paths.ToolsRoot;
	}

	private static string Stamp()
	{
		return DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss_fff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
	}

	private static string CreateWorkerToken()
	{
		return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
			.TrimEnd('=')
			.Replace('+', '-')
			.Replace('/', '_');
	}

	private static async Task<HttpResponseMessage> SendWorkerRequestAsync(
		HttpClient client,
		string uri,
		string json,
		string authToken,
		CancellationToken cancellationToken)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, uri);
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
		request.Content = new StringContent(json, Encoding.UTF8, "application/json");
		return await client.SendAsync(request, cancellationToken);
	}

	private static string StableFilePart(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "voice";
		}
		char[] chars = value.ToLowerInvariant().Select((char ch) => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
		return string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
	}

	private static string FirstNonEmpty(params string[] values)
	{
		return values.FirstOrDefault((string value) => !string.IsNullOrWhiteSpace(value)) ?? "";
	}

	private static void TryKill(Process process)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch
		{
		}
	}
}
