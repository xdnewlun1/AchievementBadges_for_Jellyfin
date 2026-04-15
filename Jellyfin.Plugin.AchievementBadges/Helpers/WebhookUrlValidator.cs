using System;
using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.AchievementBadges.Helpers;

// Restricts webhook targets to well-formed https/http URLs that don't resolve
// to loopback, link-local, or RFC1918 space. Without this, admin-configurable
// WebhookUrl is a straight SSRF channel from a badge unlock event into any
// internal service (cloud metadata endpoints, internal admin panels, etc.).
public static class WebhookUrlValidator
{
    public static bool TryValidate(string? url, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(url))
        {
            error = "Webhook URL is required.";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            error = "Webhook URL must be an absolute URI.";
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            error = "Webhook URL must use http or https.";
            return false;
        }

        if (IsDisallowedHost(uri))
        {
            error = "Webhook URL resolves to a disallowed host (loopback, link-local, or private address).";
            return false;
        }

        return true;
    }

    private static bool IsDisallowedHost(Uri uri)
    {
        var host = uri.Host;

        if (IPAddress.TryParse(host, out var literal))
        {
            return IsDisallowedAddress(literal);
        }

        // Best-effort DNS. We intentionally bound this to the DNS lookup we'd
        // already do on send; admins pointing at "localhost" or a LAN-only
        // hostname should be blocked by the same rules as IP literals.
        try
        {
            var entries = Dns.GetHostAddresses(host);
            foreach (var address in entries)
            {
                if (IsDisallowedAddress(address))
                {
                    return true;
                }
            }
        }
        catch
        {
            // If DNS fails here we let the send path fail rather than silently
            // accepting — but we don't want config save to block on a temporary
            // resolver glitch. Fall through as "allowed".
            return false;
        }

        return false;
    }

    private static bool IsDisallowedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            // 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 169.254.0.0/16 link-local (incl. cloud metadata)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
            // 0.0.0.0/8
            if (bytes[0] == 0) return true;
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            {
                return true;
            }
            // fc00::/7 unique local addresses
            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xfe) == 0xfc)
            {
                return true;
            }
        }

        return false;
    }
}
