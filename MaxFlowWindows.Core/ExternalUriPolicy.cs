using System;
using System.Net;
using System.Net.Sockets;

namespace MaxFlowWindows.Core;

public static class ExternalUriPolicy
{
    private static readonly string[] ReservedDnsSuffixes =
    {
        ".arpa",
        ".home",
        ".internal",
        ".invalid",
        ".lan",
        ".local",
        ".localhost",
        ".onion",
        ".test"
    };

    public static bool TryCreateSafeHttpsUri(string? value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? candidate)
            || !string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(candidate.Host)
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || candidate.IsLoopback
            || !IsPublicHost(candidate.DnsSafeHost))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    private static bool IsPublicHost(string host)
    {
        string normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (IPAddress.TryParse(normalized, out IPAddress? address))
        {
            return IsPublicAddress(address);
        }

        if (!normalized.Contains('.')
            || Array.Exists(
                ReservedDnsSuffixes,
                suffix => normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.None)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.IPv6None))
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !IsReservedIpv4(bytes);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6
            || address.IsIPv6LinkLocal
            || address.IsIPv6Multicast
            || address.IsIPv6SiteLocal)
        {
            return false;
        }

        // RFC 4193 unique-local addresses use fc00::/7. RFC 3849 reserves
        // 2001:db8::/32 for documentation rather than public routing.
        bool isUniqueLocal = (bytes[0] & 0xFE) == 0xFC;
        bool isDocumentation = bytes[0] == 0x20
            && bytes[1] == 0x01
            && bytes[2] == 0x0D
            && bytes[3] == 0xB8;
        return !isUniqueLocal && !isDocumentation;
    }

    private static bool IsReservedIpv4(byte[] bytes)
    {
        byte first = bytes[0];
        byte second = bytes[1];

        return first == 0
            || first == 10
            || first == 127
            || first >= 224
            || (first == 100 && second >= 64 && second <= 127)
            || (first == 169 && second == 254)
            || (first == 172 && second >= 16 && second <= 31)
            || (first == 192 && second == 0)
            || (first == 192 && second == 168)
            || (first == 198 && (second == 18 || second == 19))
            || (first == 198 && second == 51 && bytes[2] == 100)
            || (first == 203 && second == 0 && bytes[2] == 113);
    }
}
