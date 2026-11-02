namespace Highbridge.Security;

public static class OriginRules
{
    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "[::1]"];

    /// <summary>
    /// Normalizes a web origin ("https://Example.com:443" -> "https://example.com").
    /// Only https origins are accepted, plus http on loopback hosts when
    /// <paramref name="allowInsecureLoopback"/> is set (local development).
    /// </summary>
    public static bool TryNormalize(string? raw, bool allowInsecureLoopback, out string origin)
    {
        origin = string.Empty;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 512 || raw == "null")
        {
            return false;
        }

        string trimmed = raw.Trim();
        if (trimmed.EndsWith('/'))
        {
            trimmed = trimmed[..^1];
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
        {
            return false;
        }

        string host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.IdnHost.Trim('[', ']')}]" : uri.IdnHost;
        host = host.ToLowerInvariant();
        bool isLoopback = LoopbackHosts.Contains(host);

        bool schemeAllowed = uri.Scheme == Uri.UriSchemeHttps
            || (uri.Scheme == Uri.UriSchemeHttp && allowInsecureLoopback && isLoopback);
        if (!schemeAllowed)
        {
            return false;
        }

        origin = uri.IsDefaultPort ? $"{uri.Scheme}://{host}" : $"{uri.Scheme}://{host}:{uri.Port}";
        return true;
    }

    public static bool IsLoopbackHost(string host) =>
        LoopbackHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
}
