using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.AchievementBadges.Helpers;
using Jellyfin.Plugin.AchievementBadges.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AchievementBadges.Services;

public class WebhookNotifier
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ILogger<WebhookNotifier> _logger;

    public WebhookNotifier(ILogger<WebhookNotifier> logger)
    {
        _logger = logger;
    }

    public void NotifyUnlock(string userName, AchievementBadge badge)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.WebhookEnabled || string.IsNullOrWhiteSpace(config.WebhookUrl))
        {
            return;
        }

        // Re-validate at send time: the SaveWebhookConfig endpoint validates
        // on write, but config files can also be edited directly on disk
        // and the validator re-resolves DNS — a hostname that was public at
        // save time could later resolve to an internal address.
        if (!WebhookUrlValidator.TryValidate(config.WebhookUrl, out var validationError))
        {
            _logger.LogWarning("[AchievementBadges] Webhook disabled at send time: {Reason}", validationError);
            return;
        }

        var template = string.IsNullOrWhiteSpace(config.WebhookMessageTemplate)
            ? "{user} unlocked {badge}"
            : config.WebhookMessageTemplate!;

        var content = template
            .Replace("{user}", Sanitize(userName ?? "Someone"), StringComparison.OrdinalIgnoreCase)
            .Replace("{badge}", Sanitize(badge.Title ?? "a badge"), StringComparison.OrdinalIgnoreCase)
            .Replace("{rarity}", Sanitize(badge.Rarity ?? "Common"), StringComparison.OrdinalIgnoreCase)
            .Replace("{description}", Sanitize(badge.Description ?? string.Empty), StringComparison.OrdinalIgnoreCase);

        var url = config.WebhookUrl!;
        object payload;
        if (url.Contains("hooks.slack.com", StringComparison.OrdinalIgnoreCase))
        {
            payload = new { text = content };
        }
        else
        {
            // Default Discord / generic format
            payload = new { content };
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var json = JsonSerializer.Serialize(payload);
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                using var res = await _http.SendAsync(req).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[AchievementBadges] Webhook POST returned {Status}", (int)res.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AchievementBadges] Webhook POST failed.");
            }
        });
    }

    // Strip newlines and other control characters so a user-supplied name or
    // badge description can't break out of the surrounding template context
    // (newlines would otherwise appear as-is in Slack/Discord rendering and
    // could allow primitive markdown/mention injection).
    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch == '\r' || ch == '\n' || ch == '\t')
            {
                sb.Append(' ');
            }
            else if (char.IsControl(ch))
            {
                continue;
            }
            else
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }
}
