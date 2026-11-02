using Highbridge.Security;

namespace Highbridge.Core.Tests;

public class OriginRulesTests
{
    [Theory]
    [InlineData("https://app.example.com", "https://app.example.com")]
    [InlineData("https://App.Example.com:443", "https://app.example.com")]
    [InlineData("https://app.example.com:8443", "https://app.example.com:8443")]
    [InlineData("https://app.example.com/", "https://app.example.com")]
    [InlineData("https://bücher.example", "https://xn--bcher-kva.example")]
    [InlineData("http://localhost:47200", "http://localhost:47200")]
    [InlineData("http://127.0.0.1:47200", "http://127.0.0.1:47200")]
    [InlineData("http://[::1]:8080", "http://[::1]:8080")]
    public void Accepts_and_normalizes(string raw, string expected)
    {
        Assert.True(OriginRules.TryNormalize(raw, allowInsecureLoopback: true, out string origin));
        Assert.Equal(expected, origin);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("http://app.example.com")]
    [InlineData("file:///C:/scan.html")]
    [InlineData("https://app.example.com/path")]
    [InlineData("https://app.example.com?x=1")]
    [InlineData("https://user:pw@app.example.com")]
    [InlineData("chrome-extension://abcdef")]
    [InlineData("not a url")]
    public void Rejects(string? raw)
    {
        Assert.False(OriginRules.TryNormalize(raw, allowInsecureLoopback: true, out _));
    }

    [Fact]
    public void Insecure_loopback_can_be_disabled()
    {
        Assert.False(OriginRules.TryNormalize("http://localhost:47200", allowInsecureLoopback: false, out _));
        Assert.True(OriginRules.TryNormalize("https://localhost:47200", allowInsecureLoopback: false, out _));
    }
}
