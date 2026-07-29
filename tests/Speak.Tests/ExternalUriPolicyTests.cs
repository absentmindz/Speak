using MaxFlowWindows.Core;
using Xunit;

namespace Speak.Tests;

public sealed class ExternalUriPolicyTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://example.com/path?q=1#pricing")]
    [InlineData("HTTPS://EXAMPLE.COM/support")]
    [InlineData("https://8.8.8.8/support")]
    [InlineData("https://[2606:4700:4700::1111]/support")]
    public void AcceptsPublicHttpsLinks(string value)
    {
        Assert.True(ExternalUriPolicy.TryCreateSafeHttpsUri(value, out Uri? uri));
        Assert.NotNull(uri);
        Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://example.com")]
    [InlineData("https://user:***@example.com")]
    [InlineData("https://localhost:8443")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://10.0.0.1")]
    [InlineData("https://100.64.0.1")]
    [InlineData("https://169.254.1.1")]
    [InlineData("https://172.16.0.1")]
    [InlineData("https://192.168.1.1")]
    [InlineData("https://192.0.2.1")]
    [InlineData("https://198.51.100.1")]
    [InlineData("https://203.0.113.1")]
    [InlineData("https://224.0.0.1")]
    [InlineData("https://[::1]")]
    [InlineData("https://[fc00::1]")]
    [InlineData("https://[fe80::1]")]
    [InlineData("https://[2001:db8::1]")]
    [InlineData("https://intranet")]
    [InlineData("https://service.local")]
    [InlineData("https://service.internal")]
    [InlineData("https://service.invalid")]
    [InlineData("file:///C:/temp/file.txt")]
    public void RejectsUnsafeOrNonPublicLinks(string value)
    {
        Assert.False(ExternalUriPolicy.TryCreateSafeHttpsUri(value, out Uri? uri));
        Assert.Null(uri);
    }
}
