using System;
using Jellyfin.Plugin.AchievementBadges.Models;

namespace Jellyfin.Plugin.AchievementBadges.Helpers;

// Caps string fields and clamps numeric/enum values on admin-supplied
// AchievementDefinitions before they're persisted into plugin config.
// Without this, an admin (or anyone who compromises admin access) can plant
// unbounded strings — rendered by shell.js via innerHTML — and negative or
// absurd TargetValues that blow up badge-progress math.
public static class AchievementDefinitionSanitizer
{
    private const int MaxIdLength = 128;
    private const int MaxTitleLength = 200;
    private const int MaxDescriptionLength = 2000;
    private const int MaxIconLength = 64;
    private const int MaxCategoryLength = 64;
    private const int MaxRarityLength = 32;
    private const int MaxMetricParamLength = 256;
    private const int MinTargetValue = 1;
    private const int MaxTargetValue = 1_000_000;

    public static AchievementDefinition Sanitize(AchievementDefinition d)
    {
        if (d is null)
        {
            return new AchievementDefinition();
        }

        d.Id = Trim(d.Id, MaxIdLength);
        d.Key = Trim(d.Key, MaxIdLength);
        d.Title = Trim(d.Title, MaxTitleLength);
        d.Description = Trim(d.Description, MaxDescriptionLength);
        d.Icon = string.IsNullOrWhiteSpace(d.Icon) ? "emoji_events" : Trim(d.Icon, MaxIconLength);
        d.Category = string.IsNullOrWhiteSpace(d.Category) ? "General" : Trim(d.Category, MaxCategoryLength);
        d.Rarity = string.IsNullOrWhiteSpace(d.Rarity) ? "Common" : Trim(d.Rarity, MaxRarityLength);
        d.MetricParameter = d.MetricParameter is null ? null : Trim(d.MetricParameter, MaxMetricParamLength);

        d.TargetValue = Math.Clamp(d.TargetValue <= 0 ? MinTargetValue : d.TargetValue, MinTargetValue, MaxTargetValue);

        if (!Enum.IsDefined(typeof(AchievementMetric), d.Metric))
        {
            d.Metric = default;
        }

        return d;
    }

    public static AchievementBadge Sanitize(AchievementBadge b)
    {
        if (b is null)
        {
            return new AchievementBadge();
        }

        b.Id = Trim(b.Id, MaxIdLength);
        b.Key = Trim(b.Key, MaxIdLength);
        b.Title = Trim(b.Title, MaxTitleLength);
        b.Description = Trim(b.Description, MaxDescriptionLength);
        b.Icon = string.IsNullOrWhiteSpace(b.Icon) ? "military_tech" : Trim(b.Icon, MaxIconLength);
        b.Category = string.IsNullOrWhiteSpace(b.Category) ? "General" : Trim(b.Category, MaxCategoryLength);
        b.Rarity = string.IsNullOrWhiteSpace(b.Rarity) ? "Common" : Trim(b.Rarity, MaxRarityLength);
        b.TargetValue = Math.Clamp(b.TargetValue <= 0 ? MinTargetValue : b.TargetValue, MinTargetValue, MaxTargetValue);
        b.CurrentValue = Math.Clamp(b.CurrentValue, 0, MaxTargetValue);

        return b;
    }

    private static string Trim(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        return value.Length <= max ? value : value.Substring(0, max);
    }
}
