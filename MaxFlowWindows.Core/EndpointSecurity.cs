using System;

namespace MaxFlowWindows.Core;

public static class EndpointSecurity
{
	public static int ResolveLoopbackPort(string environmentVariable, int defaultPort)
	{
		if (string.IsNullOrWhiteSpace(environmentVariable))
			throw new ArgumentException("An environment-variable name is required.", nameof(environmentVariable));
		if (defaultPort is < 1 or > 65535)
			throw new ArgumentOutOfRangeException(nameof(defaultPort));

		string configured = Environment.GetEnvironmentVariable(environmentVariable) ?? "";
		return int.TryParse(configured, out int port) && port is >= 1 and <= 65535
			? port
			: defaultPort;
	}

	public static Uri RequireHttpsOrLoopback(Uri endpoint, string purpose)
	{
		if (!endpoint.IsAbsoluteUri)
			throw new InvalidOperationException(purpose + " endpoint must be an absolute URL.");

		if (endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
			return endpoint;

		if (endpoint.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && endpoint.IsLoopback)
			return endpoint;

		throw new InvalidOperationException(purpose + " endpoint must use HTTPS. Plain HTTP is allowed only for loopback services.");
	}
}
