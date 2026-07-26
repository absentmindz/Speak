using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MaxFlowWindows.Core;

public sealed class HttpRequest
{
	public string Method { get; set; } = "";
	public string Path { get; set; } = "";
	public string RawUrl { get; set; } = "";
	public Dictionary<string, string> Query { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	public Dictionary<string, string> PathParams { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	public string Body { get; set; } = "";
	public string ContentType { get; set; } = "";
	public CancellationToken CancellationToken { get; internal set; }
}

public sealed class RestApiServer : IDisposable
{
	private const int MaxHeaderBytes = 32 * 1024;
	private const int MaxBodyBytes = 1024 * 1024;
	private const int MaxConcurrentConnections = 8;
	private const int ListenBacklog = 8;
	private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	private readonly int _port;
	private readonly string _bearerToken;
	private readonly HashSet<string> _allowedOrigins;
	private readonly List<RouteEntry> _routes = new();
	private readonly SemaphoreSlim _connectionLimit = new(
		MaxConcurrentConnections,
		MaxConcurrentConnections);
	private readonly object _lifecycleSync = new();
	private readonly object _connectionsSync = new();
	private readonly HashSet<Task> _activeConnections = new();
	private TcpListener? _listener;
	private CancellationTokenSource? _cts;
	private Task? _task;

	public bool IsRunning
	{
		get
		{
			lock (_lifecycleSync)
				return _task != null && !_task.IsCompleted;
		}
	}

	public RestApiServer(int port, string bearerToken, IEnumerable<string>? allowedOrigins = null)
	{
		if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
			throw new ArgumentOutOfRangeException(nameof(port));
		if (string.IsNullOrWhiteSpace(bearerToken) || bearerToken.Length < 32)
			throw new ArgumentException("REST API bearer token must contain at least 32 characters.", nameof(bearerToken));

		_port = port;
		_bearerToken = bearerToken;
		_allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string origin in allowedOrigins ?? Array.Empty<string>())
		{
			if (TryNormalizeOrigin(origin, out string? normalized))
				_allowedOrigins.Add(normalized);
			else if (!string.IsNullOrWhiteSpace(origin))
				throw new ArgumentException(
					"REST API allowed origins must be HTTP(S) origins without paths, credentials, queries, or fragments.",
					nameof(allowedOrigins));
		}
	}

	public void RegisterRoute(string method, string pathPattern, Func<HttpRequest, Task<object?>> handler)
	{
		_routes.Add(new RouteEntry
		{
			Method = method,
			Pattern = pathPattern,
			Segments = pathPattern.Split('/', StringSplitOptions.RemoveEmptyEntries),
			Handler = handler
		});
	}

	public void RegisterRoute(string method, string pathPattern, Func<HttpRequest, object?> handler)
	{
		RegisterRoute(method, pathPattern, request => Task.FromResult(handler(request)));
	}

	public void Start()
	{
		lock (_lifecycleSync)
		{
			if (_task != null && !_task.IsCompleted)
				return;

			_cts?.Dispose();
			_cts = new CancellationTokenSource();
			_listener = new TcpListener(IPAddress.Loopback, _port);
				_listener.Start(ListenBacklog);
				_task = RunAsync(_listener, _cts.Token);
		}
	}

	public async Task StopAsync()
	{
		Task? task;
		CancellationTokenSource? cts;
			lock (_lifecycleSync)
			{
				task = _task;
				cts = _cts;
				cts?.Cancel();
				_listener?.Stop();
				_listener = null;
		}

		if (task != null)
		{
			try { await task.ConfigureAwait(false); }
			catch (OperationCanceledException) { }
			catch (ObjectDisposedException) { }
		}
			lock (_lifecycleSync)
			{
				if (ReferenceEquals(_task, task))
				{
					_task = null;
					_cts = null;
				}
			}
			cts?.Dispose();
	}

	public void Stop()
	{
		StopAsync().ConfigureAwait(false).GetAwaiter().GetResult();
	}

	public void Dispose()
	{
		Stop();
	}

	private async Task RunAsync(TcpListener listener, CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				try
				{
					await _connectionLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					break;
				}

				TcpClient? client = null;
				bool releaseSlot = true;
				try
				{
					client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
					Task connection = HandleAcceptedConnectionAsync(client, cancellationToken);
					TrackConnection(connection);
					releaseSlot = false;
				}
				catch (OperationCanceledException)
				{
					client?.Dispose();
					break;
				}
				catch (ObjectDisposedException)
				{
					client?.Dispose();
					break;
				}
				catch (SocketException) when (cancellationToken.IsCancellationRequested)
				{
					client?.Dispose();
					break;
				}
				finally
				{
					if (releaseSlot)
						_connectionLimit.Release();
				}
			}
		}
		finally
		{
			await DrainConnectionsAsync().ConfigureAwait(false);
		}
	}

	private async Task HandleAcceptedConnectionAsync(
		TcpClient client,
		CancellationToken cancellationToken)
	{
		try
		{
			using (client)
				await HandleRequestAsync(client, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_connectionLimit.Release();
		}
	}

	private void TrackConnection(Task connection)
	{
		lock (_connectionsSync)
			_activeConnections.Add(connection);

		_ = connection.ContinueWith(
			completed =>
			{
				_ = completed.Exception;
				lock (_connectionsSync)
					_activeConnections.Remove(completed);
			},
			CancellationToken.None,
			TaskContinuationOptions.ExecuteSynchronously,
			TaskScheduler.Default);
	}

	private async Task DrainConnectionsAsync()
	{
		Task[] active;
		lock (_connectionsSync)
			active = _activeConnections.ToArray();
		if (active.Length == 0)
			return;

		try
		{
			await Task.WhenAll(active).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception exception)
		{
			AppLog.Warn("REST API connection shutdown failed.", exception);
		}
	}

	private async Task HandleRequestAsync(TcpClient client, CancellationToken serverCancellationToken)
	{
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
		timeout.CancelAfter(RequestTimeout);
		CancellationToken cancellationToken = timeout.Token;
		NetworkStream? stream = null;

		try
		{
			stream = client.GetStream();
			HttpRequest request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
			request.CancellationToken = cancellationToken;

			if (!IsOriginAllowed(request, out string? allowedOrigin))
			{
				await WriteResponseAsync(stream, 403, new { error = "Browser origin is not allowed", statusCode = 403 }, null, cancellationToken).ConfigureAwait(false);
				return;
			}

			if (request.Method.Equals("OPTIONS", StringComparison.Ordinal))
			{
				if (!TryValidatePreflight(request, allowedOrigin, out string requestedMethod))
				{
					await WriteResponseAsync(stream, 403, new { error = "CORS preflight is not allowed", statusCode = 403 }, null, cancellationToken).ConfigureAwait(false);
					return;
				}
				await WritePreflightResponseAsync(stream, allowedOrigin!, requestedMethod, cancellationToken).ConfigureAwait(false);
				return;
			}

			if (!IsAuthorized(request))
			{
				await WriteResponseAsync(stream, 401, new { error = "Unauthorized", statusCode = 401 }, allowedOrigin, cancellationToken).ConfigureAwait(false);
				return;
			}

			(RouteEntry? route, Dictionary<string, string> parameters) = MatchRoute(request.Method, request.Path);
			if (route == null)
			{
				await WriteResponseAsync(stream, 404, new { error = "Not found", method = request.Method, path = request.Path, statusCode = 404 }, allowedOrigin, cancellationToken).ConfigureAwait(false);
				return;
			}

			request.PathParams = parameters;
			object? responseBody;
			try
			{
				responseBody = await route.Handler(request).ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				AppLog.Warn("REST API route failed.", exception);
				responseBody = new { error = "Request failed", statusCode = 500 };
			}

			int statusCode = ResolveStatusCode(responseBody);
			await WriteResponseAsync(stream, statusCode, responseBody, allowedOrigin, cancellationToken).ConfigureAwait(false);
		}
		catch (HttpRequestException exception)
		{
			try
			{
				if (stream != null)
					await WriteResponseAsync(stream, exception.StatusCode, new { error = exception.Message, statusCode = exception.StatusCode }, null, CancellationToken.None).ConfigureAwait(false);
			}
			catch
			{
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (IOException)
		{
		}
		catch (SocketException)
		{
		}
		finally
		{
			stream?.Dispose();
		}
	}

	private static async Task<HttpRequest> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
	{
		using var received = new MemoryStream();
		byte[] buffer = new byte[4096];
		int headerEnd = -1;

		while (headerEnd < 0)
		{
			int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
			if (read == 0)
				throw new HttpRequestException(400, "Empty request.");
			received.Write(buffer, 0, read);
			if (received.Length > MaxHeaderBytes)
				throw new HttpRequestException(431, "Request headers are too large.");
			headerEnd = FindHeaderEnd(received.GetBuffer(), (int)received.Length);
		}

		byte[] requestBytes = received.ToArray();
		string headerText = Encoding.ASCII.GetString(requestBytes, 0, headerEnd);
		HttpRequest request = ParseHeaders(headerText);

		if (request.Headers.ContainsKey("Transfer-Encoding"))
			throw new HttpRequestException(400, "Transfer-Encoding is not supported.");

		int contentLength = 0;
		if (request.Headers.TryGetValue("Content-Length", out string? contentLengthValue) &&
			(!int.TryParse(contentLengthValue, out contentLength) || contentLength < 0))
		{
			throw new HttpRequestException(400, "Invalid Content-Length.");
		}
		if (contentLength > MaxBodyBytes)
			throw new HttpRequestException(413, "Request body is too large.");

		int bodyOffset = headerEnd + 4;
		int bodyBytesAlreadyRead = requestBytes.Length - bodyOffset;
		if (bodyBytesAlreadyRead > contentLength)
			bodyBytesAlreadyRead = contentLength;

		byte[] body = new byte[contentLength];
		if (bodyBytesAlreadyRead > 0)
			Buffer.BlockCopy(requestBytes, bodyOffset, body, 0, bodyBytesAlreadyRead);

		int offset = bodyBytesAlreadyRead;
		while (offset < contentLength)
		{
			int read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), cancellationToken).ConfigureAwait(false);
			if (read == 0)
				throw new HttpRequestException(400, "Request body ended early.");
			offset += read;
		}

		request.Body = Encoding.UTF8.GetString(body);
		return request;
	}

	private static HttpRequest ParseHeaders(string headerText)
	{
		string[] lines = headerText.Split("\r\n", StringSplitOptions.None);
		if (lines.Length == 0)
			throw new HttpRequestException(400, "Missing request line.");

		string[] requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase))
			throw new HttpRequestException(400, "Malformed request line.");

		var request = new HttpRequest
		{
			Method = requestLine[0].ToUpperInvariant(),
			RawUrl = requestLine[1]
		};
		if (request.Method is not ("GET" or "POST" or "OPTIONS"))
			throw new HttpRequestException(405, "Method not allowed.");

		if (!Uri.TryCreate("http://127.0.0.1" + request.RawUrl, UriKind.Absolute, out Uri? uri))
			throw new HttpRequestException(400, "Malformed request URL.");
		request.Path = uri.AbsolutePath.TrimEnd('/');
		if (request.Path.Length == 0)
			request.Path = "/";
		foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
		{
			string[] keyValue = pair.Split('=', 2);
			request.Query[Uri.UnescapeDataString(keyValue[0])] = keyValue.Length == 2
				? Uri.UnescapeDataString(keyValue[1])
				: "";
		}

		for (int index = 1; index < lines.Length; index++)
		{
			int separator = lines[index].IndexOf(':');
			if (separator <= 0)
				throw new HttpRequestException(400, "Malformed request header.");
			string name = lines[index][..separator].Trim();
			string value = lines[index][(separator + 1)..].Trim();
			if (!request.Headers.TryAdd(name, value))
				throw new HttpRequestException(400, "Duplicate request header.");
		}
		request.ContentType = request.Headers.GetValueOrDefault("Content-Type") ?? "";
		return request;
	}

	private bool IsAuthorized(HttpRequest request)
	{
		if (!request.Headers.TryGetValue("Authorization", out string? authorization) ||
			!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		byte[] expected = Encoding.UTF8.GetBytes(_bearerToken);
		byte[] supplied = Encoding.UTF8.GetBytes(authorization["Bearer ".Length..].Trim());
		return expected.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
	}

	private bool IsOriginAllowed(HttpRequest request, out string? allowedOrigin)
	{
		allowedOrigin = null;
		if (!request.Headers.TryGetValue("Origin", out string? origin) || string.IsNullOrWhiteSpace(origin))
			return true;

		if (!TryNormalizeOrigin(origin, out string? normalized))
			return false;
		if (!_allowedOrigins.Contains(normalized))
			return false;
		allowedOrigin = normalized;
		return true;
	}

	private bool TryValidatePreflight(
		HttpRequest request,
		string? allowedOrigin,
		out string requestedMethod)
	{
		requestedMethod = "";
		if (string.IsNullOrWhiteSpace(allowedOrigin) ||
			!request.Headers.TryGetValue(
				"Access-Control-Request-Method",
				out string? method))
			return false;

		requestedMethod = method.Trim().ToUpperInvariant();
		if (requestedMethod is not ("GET" or "POST") ||
			MatchRoute(requestedMethod, request.Path).Route == null)
			return false;

		if (!request.Headers.TryGetValue(
			"Access-Control-Request-Headers",
			out string? requestedHeaders))
			return true;

		foreach (string header in requestedHeaders.Split(
			',',
			StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (!header.Equals("Authorization", StringComparison.OrdinalIgnoreCase) &&
				!header.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
				return false;
		}
		return true;
	}

	private static bool TryNormalizeOrigin(string value, out string normalized)
	{
		normalized = "";
		if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri) ||
			(uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
			!string.IsNullOrEmpty(uri.UserInfo) ||
			uri.AbsolutePath != "/" ||
			!string.IsNullOrEmpty(uri.Query) ||
			!string.IsNullOrEmpty(uri.Fragment))
			return false;

		normalized = uri.GetLeftPart(UriPartial.Authority);
		return true;
	}

	private (RouteEntry? Route, Dictionary<string, string> Parameters) MatchRoute(string method, string path)
	{
		string[] pathSegments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
		foreach (RouteEntry route in _routes)
		{
			if (!string.Equals(route.Method, method, StringComparison.OrdinalIgnoreCase) ||
				route.Segments.Length != pathSegments.Length)
			{
				continue;
			}

			var extracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			bool matches = true;
			for (int index = 0; index < route.Segments.Length; index++)
			{
				string segment = route.Segments[index];
				if (segment.StartsWith('{') && segment.EndsWith('}'))
					extracted[segment.Trim('{', '}')] = Uri.UnescapeDataString(pathSegments[index]);
				else if (!string.Equals(segment, pathSegments[index], StringComparison.OrdinalIgnoreCase))
					matches = false;
			}
			if (matches)
				return (route, extracted);
		}
		return (null, new Dictionary<string, string>());
	}

	private static int ResolveStatusCode(object? responseBody)
	{
		if (responseBody == null)
			return 204;
		object? status = responseBody.GetType().GetProperty("statusCode")?.GetValue(responseBody);
		return status is int code && code is >= 100 and <= 599 ? code : 200;
	}

	private static async Task WriteResponseAsync(NetworkStream stream, int statusCode, object? responseBody, string? allowedOrigin, CancellationToken cancellationToken)
	{
		string responseJson = responseBody == null ? "" : JsonSerializer.Serialize(responseBody, JsonOptions);
		byte[] bodyBytes = Encoding.UTF8.GetBytes(responseJson);
		string reason = statusCode switch
		{
			200 => "OK",
			201 => "Created",
			204 => "No Content",
			400 => "Bad Request",
			401 => "Unauthorized",
			403 => "Forbidden",
			404 => "Not Found",
			405 => "Method Not Allowed",
			409 => "Conflict",
			413 => "Payload Too Large",
			431 => "Request Header Fields Too Large",
			500 => "Internal Server Error",
			_ => "Response"
		};

		var header = new StringBuilder()
			.Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(reason).Append("\r\n")
			.Append("Content-Type: application/json; charset=utf-8\r\n")
			.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n")
			.Append("Cache-Control: no-store\r\n")
			.Append("X-Content-Type-Options: nosniff\r\n")
			.Append("Connection: close\r\n");
			if (!string.IsNullOrWhiteSpace(allowedOrigin))
				header.Append("Access-Control-Allow-Origin: ").Append(allowedOrigin)
					.Append("\r\nAccess-Control-Allow-Credentials: true\r\nVary: Origin\r\n");
			header.Append("\r\n");

		byte[] headerBytes = Encoding.ASCII.GetBytes(header.ToString());
		await stream.WriteAsync(headerBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
		if (bodyBytes.Length > 0)
				await stream.WriteAsync(bodyBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
	}

	private static async Task WritePreflightResponseAsync(
		NetworkStream stream,
		string allowedOrigin,
		string requestedMethod,
		CancellationToken cancellationToken)
	{
		string response =
			"HTTP/1.1 204 No Content\r\n" +
			"Content-Length: 0\r\n" +
			"Cache-Control: no-store\r\n" +
			"Connection: close\r\n" +
			"Access-Control-Allow-Origin: " + allowedOrigin + "\r\n" +
			"Access-Control-Allow-Credentials: true\r\n" +
			"Access-Control-Allow-Methods: " + requestedMethod + "\r\n" +
			"Access-Control-Allow-Headers: Authorization, Content-Type\r\n" +
			"Access-Control-Max-Age: 600\r\n" +
			"Vary: Origin, Access-Control-Request-Method, Access-Control-Request-Headers\r\n\r\n";
		await stream.WriteAsync(
			Encoding.ASCII.GetBytes(response).AsMemory(),
			cancellationToken).ConfigureAwait(false);
	}

	private static int FindHeaderEnd(byte[] buffer, int length)
	{
		for (int index = 0; index <= length - 4; index++)
		{
			if (buffer[index] == '\r' && buffer[index + 1] == '\n' &&
				buffer[index + 2] == '\r' && buffer[index + 3] == '\n')
			{
				return index;
			}
		}
		return -1;
	}

	private sealed class RouteEntry
	{
		public string Method { get; init; } = "";
		public string Pattern { get; init; } = "";
		public string[] Segments { get; init; } = Array.Empty<string>();
		public Func<HttpRequest, Task<object?>> Handler { get; init; } = _ => Task.FromResult<object?>(null);
	}

	private sealed class HttpRequestException : Exception
	{
		public int StatusCode { get; }

		public HttpRequestException(int statusCode, string message) : base(message)
		{
			StatusCode = statusCode;
		}
	}
}
