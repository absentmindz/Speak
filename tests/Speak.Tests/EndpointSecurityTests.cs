using MaxFlowWindows.Core;
using Xunit;

namespace Speak.Tests;

public sealed class EndpointSecurityTests
{
    [Theory]
    [InlineData("https://api.example.test/v1")]
    [InlineData("HTTPS://api.example.test/v1")]
    [InlineData("http://127.0.0.1:53421/")]
    [InlineData("http://localhost:53421/")]
    [InlineData("http://[::1]:53421/")]
    public void RequireHttpsOrLoopbackAcceptsSecureOrLoopbackEndpoint(string address)
    {
        var endpoint = new Uri(address, UriKind.Absolute);

        Uri accepted = EndpointSecurity.RequireHttpsOrLoopback(endpoint, "Test");

        Assert.Same(endpoint, accepted);
    }

    [Theory]
    [InlineData("http://api.example.test/v1")]
    [InlineData("ftp://127.0.0.1/resource")]
    [InlineData("ws://127.0.0.1/socket")]
    public void RequireHttpsOrLoopbackRejectsInsecureEndpoint(string address)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => EndpointSecurity.RequireHttpsOrLoopback(
                new Uri(address, UriKind.Absolute),
                "Cloud speech"));

        Assert.Contains("HTTPS", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RequireHttpsOrLoopbackRejectsRelativeEndpoint()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => EndpointSecurity.RequireHttpsOrLoopback(
                new Uri("/relative", UriKind.Relative),
                "Cloud speech"));

        Assert.Contains("absolute", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveLoopbackPortUsesValidEnvironmentOverride()
    {
        const string variable = "SPEAK_TEST_LOOPBACK_PORT";
        string? previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "43123");

            Assert.Equal(43123, EndpointSecurity.ResolveLoopbackPort(variable, 40000));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-port")]
    [InlineData("0")]
    [InlineData("65536")]
    public void ResolveLoopbackPortFallsBackForMissingOrInvalidOverride(string configured)
    {
        const string variable = "SPEAK_TEST_LOOPBACK_PORT";
        string? previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, configured);

            Assert.Equal(40000, EndpointSecurity.ResolveLoopbackPort(variable, 40000));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }
}
