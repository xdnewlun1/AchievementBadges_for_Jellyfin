using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.AchievementBadges.Helpers;
using Jellyfin.Plugin.AchievementBadges.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AchievementBadges.Services;

public class AchievementBadgeService
{
    private readonly string _dataFilePath;
    private readonly object _lock = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly ILogger<AchievementBadgeService> _logger;
    private readonly IUserManager _userManager;

    private Dictionary<string, UserAchievementProfile> _userProfiles = new();

    public AchievementBadgeService(
        IApplicationPaths applicationPaths,
        IUserManager userManager,
        ILogger<AchievementBadgeService> logger)
    {
        _logger = logger;
        _userManager = userManager;

        var pluginDataPath = Path.Combine(applicationPaths.PluginConfigurationsPath, "achievementbadges");
        Directory.CreateDirectory(pluginDataPath);

        _dataFilePath = Path.Combine(pluginDataPath, "badges.json");
        Load();
    }

    public List<AchievementBadge> GetBadgesForUser(string userId)
    {
        userId = NormalizeUserId(userId);
        lock (_lock)
        {
            var profile = GetOrCreateProfile(userId);
            EvaluateBadges(profile, userId);
            Save();
            return GetEnabledBadgeClones(profile);
        }
    }

    public List<AchievementBadge> GetAllBadgesForUserIncludingDisabled(string userId)
    {
        userId = NormalizeUserId(userId);
        lock (_lock)
        {
            var profile = GetOrCreateProfile(userId);
            EvaluateBadges(profile, userId);
            Save();
            return profile.Badges.Select(CloneBadge).ToList();
        }
    }

    public AchievementBadge? GetBadge(string userId, string badgeId)
    {
        userId = NormalizeUserId(userId);
        lock (_lock)
        {
            var profile = GetOrCreateProfile(userId);
            EvaluateBadges(profile, userId);

            if (!IsBadgeEnabled(badgeId))
            {
                return null;
            }

            var badge = profile.Badges.FirstOrDefault(b => b.Id.Equals(badgeId, StringComparison.OrdinalIgnoreCase));
            return badge is null ? null : CloneBadge(badge);
        }
    }

    public List<AchievementBadge> GetEquippedBadges(string userId)
    {
        userId = NormalizeUserId(userId);
        lock (_lock)
        {
            var profile = GetOrCreateProfile(userId);
            EvaluateBadges(profile, userId);

            var equipped = profile.EquippedBadgeIds
                .Where(IsBadgeEnabled)
                .Select(id => profile.Badges.FirstOrDefault(b => b.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                .Where(b => b is not null)
                .Select(b => CloneBadge(b!))
                .ToList();

            return equipped;
        }
    }

    public bool EquipBadge(string userId, string badgeId, out string message)
    {
        userId = NormalizeUserId(userId);
        lock (_lock)
        {
            var profile = GetOrCreateProfile(userId);
            EvaluateBadges(profile, userId);

            var badge = profile.Badges.FirstOrDefault(b => b.Id.Equals(badgeId, StringComparison.OrdinalIgnoreCase));

            if (badge is null)
            {
                message = "Badge not found.";
                return false;
            }

            if (!badge.Unlocked)
            {
                message = "Only unlocked badges can be equipped.";
                return false;
            }

            if (profile.EquippedBadgeIds.Any(x => x.Equals(badgeId, StringComparison.OrdinalIgnoreCase)))
            {
                message = "Badge is already equipped.";
                return true;
            }

            if (profile.EquippedBadgeIds.Count >= 5)
            {
                message = "You can only equip up to 5 badges.";
                return false;
            }

            profile.EquippedBadgeIds.Add(badge.Id);
            Save();

            _logger.LogInformation("Equipped badge {BadgeId} for user {UserId}", badgeId, userId);

            message = "Badge equipped.";
            return true;
        }
    }

    public bool UnequipBadge(string userId, string badgeId, out string message)
    {
        userId = NormalizeUserId(userId);
        lock (_lock)
        {
            var profile = GetOrCreateProfile(userId);

            var removed = profile.EquippedBadgeIds.RemoveAll(x => x.Equals(badgeId, StringComparison.OrdinalIgnoreCase));

            if (removed == 0)
            {
                message = "Badge was not equipped.";
                return false;
            }

            Save();

            _logger.LogInformation("Unequipped badge {BadgeId} for user {UserId}", badgeId, userId);

            message = "Badge unequipped.";
            return true;
        }
    }

    public AchievementBadge? UpdateProgress(string userId, string badgeId, int amount)
    {
        userId = NormalizeUserId(userId);
        lock (_lock)
        {
            var profile = GetOrCreateProfile(userId);
            var badge = profile.Badges.FirstOrDefault(b => b.Id.Equals(badgeId, StringComparison.OrdinalIgnoreCase));

            if (badge is null)
            {
                return null;
            }

            if (!badge.Unlocked)
            {
                badge.CurrentValue = Math.Clamp(badge.CurrentValue + amount, 0, badge.TargetValue);

                if (badge.CurrentValue >= badge.TargetValue)
                {
                    badge.CurrentValue = badge.TargetValue;
                    badge.Unlocked = true;
                    badge.UnlockedAt = DateTimeOffset.UtcNow;
                    _logger.LogInformation("Unlocked badge {BadgeId} for user {UserId}", badgeId, userId);
                }

                Save();

                _logger.LogInformation(
                    "Updated badge {BadgeId} for user {UserId}: {Current}/{Target}",
                    badgeId,
                    userId,
                    badge.CurrentValue,
                    badge.TargetValue);
            }

            return CloneBadge(badge);
        }
    }

    public AchievementBadge? UnlockBadge(string userId, string badgeId)
    {
        userId = NormalizeUserId(userId);
        lock (_lock)
        {
            var profile = GetOrCreateProfile(userId);
            var badge = profile.Badges.FirstOrDefault(b => b.Id.Equals(badgeId, StringComparison.OrdinalIgnoreCase));

            if (badge is null)
            {
                return null;
            }

            if (!badge.Unlocked)
            {
                badge.Unlocked = true;
                badge.UnlockedAt = DateTimeOffset.UtcNow;
                badge.CurrentValue = badge.TargetValue;
                Save();
                _logger.LogInformation("Force unlocked badge {BadgeId} for user {UserId}", badgeId, userId);
            }

            return CloneBadge(badge);
        }
    }

    public List<AchievementBadge> ResetBadgesForUser(string userId)
    {
        userId = NormalizeUserId(userId);
        lock (_lock)
        {
            var profile = CreateProfile(userId);
            _userProfiles[userId] = profile;
            Save();
            _logger.LogInformation("Reset badges for user {UserId}", userId);
            return profile.Badges.Select(CloneBadge).ToList();
        }
    }

    public List<AchievementBadge> RecordPlayback(
        string userId,
        bool isMovie = false,
        bool isEpisode = false,
        bool seriesCompleted = false,
        string? libraryName = null,
        DateTimeOffset? playedAt = null)
    {
        return RecordPlayback(new PlaybackContext
        {
            UserId = userId,
            IsMovie = isMovie,
            IsEpisode = isEpisode,
            SeriesCompleted = seriesCompleted,
            LibraryName = libraryName,
            PlayedAt = playedAt
        });
    }

    public List<AchievementBadge> RecordPlayback(PlaybackContext context)
    {
        var userId = NormalizeUserId(context.UserId);
        lock (_lock)
        {
            var profile = GetOrCreateProfile(userId);
            var counters = profile.Counters;
            var timestamp = context.PlayedAt ?? DateTimeOffset.Now;
            var dayKey = timestamp.ToString("yyyy-MM-dd");
            var today = DateOnly.FromDateTime(timestamp.DateTime);

            counters.TotalItemsWatched++;
            counters.WatchDates.Add(dayKey);

            if (counters.LastWatchDate == null)
            {
                counters.LastWatchDate = today;
            }
            else
            {
                var diff = today.DayNumber - counters.LastWatchDate.Value.DayNumber;

                if (diff >= 1)
                {
                    counters.LastWatchDate = today;
                }
            }

            var currentStreak = GetCurrentWatchStreak(counters);
            if (currentStreak > counters.BestWatchStreak)
            {
                counters.BestWatchStreak = currentStreak;
            }

            if (!string.IsNullOrWhiteSpace(context.LibraryName))
            {
                counters.LibrariesVisited.Add(context.LibraryName.Trim());
            }

            if (context.IsMovie)
            {
                counters.MoviesWatched++;

                if (!counters.MoviesByDate.ContainsKey(dayKey))
                {
                    counters.MoviesByDate[dayKey] = 0;
                }

                counters.MoviesByDate[dayKey]++;
            }

            if (context.IsEpisode)
            {
                if (!counters.EpisodesByDate.ContainsKey(dayKey))
                {
                    counters.EpisodesByDate[dayKey] = 0;
                }

                counters.EpisodesByDate[dayKey]++;
            }

            if (context.SeriesCompleted)
            {
                counters.SeriesCompleted++;

                if (context.CompletedSeriesEpisodeCount >= 50)
                {
                    counters.LongSeriesCompleted++;
                }

                if (context.CompletedSeriesEpisodeCount >= 100)
                {
                    counters.VeryLongSeriesCompleted++;
                }
            }

            if (context.IsRewatch)
            {
                counters.RewatchCount++;
            }

            if (context.ProductionYear is int year && year > 0)
            {
                counters.DecadesWatched.Add(year / 10 * 10);
            }

            if (context.ProductionLocations is { Count: > 0 })
            {
                foreach (var loc in context.ProductionLocations)
                {
                    if (!string.IsNullOrWhiteSpace(loc))
                    {
                        counters.CountriesWatched.Add(loc.Trim());
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(context.OriginalLanguage))
            {
                counters.LanguagesWatched.Add(context.OriginalLanguage.Trim().ToLowerInvariant());
            }

            if (context.Genres is { Count: > 0 })
            {
                foreach (var genre in context.Genres)
                {
                    if (!string.IsNullOrWhiteSpace(genre))
                    {
                        counters.GenresWatched.Add(genre.Trim());
                    }
                }
            }

            if (context.RunTimeTicks is long ticks && ticks > 0)
            {
                var minutes = (int)(ticks / TimeSpan.TicksPerMinute);
                counters.TotalMinutesWatched += minutes;

                if (minutes > counters.LongestItemMinutes)
                {
                    counters.LongestItemMinutes = minutes;
                }

                if (minutes > 0 && minutes < 30)
                {
                    counters.ShortItemsWatched++;
                }
            }

            if (timestamp.Month == 12 && timestamp.Day == 25)
            {
                counters.WatchedOnChristmas = true;
            }

            if (timestamp.Month == 1 && timestamp.Day == 1)
            {
                counters.WatchedOnNewYear = true;
            }

            if (timestamp.Month == 10 && timestamp.Day == 31)
            {
                counters.WatchedOnHalloween = true;
            }

            var hour = timestamp.Hour;

            if (hour >= 23 || hour < 5)
            {
                counters.LateNightSessions++;
            }

            if (hour >= 5 && hour < 9)
            {
                counters.EarlyMorningSessions++;
            }

            if (timestamp.DayOfWeek == DayOfWeek.Saturday || timestamp.DayOfWeek == DayOfWeek.Sunday)
            {
                counters.WeekendSessions++;
            }

            EvaluateBadges(profile, userId);
            Save();

            _logger.LogInformation(
                "[AchievementBadges] Recorded playback user={UserId} movie={IsMovie} ep={IsEpisode} seriesDone={SeriesCompleted} library={LibraryName}",
                userId,
                context.IsMovie,
                context.IsEpisode,
                context.SeriesCompleted,
                context.LibraryName ?? string.Empty);

            return GetEnabledBadgeClones(profile);
        }
    }

    public object GetSummary(string userId)
    {
        userId = NormalizeUserId(userId);
        lock (_lock)
        {
            var profile = GetOrCreateProfile(userId);
            EvaluateBadges(profile, userId);

            var enabledBadges = profile.Badges.Where(b => IsBadgeEnabled(b.Id)).ToList();
            var unlocked = enabledBadges.Count(b => b.Unlocked);
            var total = enabledBadges.Count;
            var percentage = total == 0 ? 0 : Math.Round((double)unlocked / total * 100.0, 1);
            var score = AchievementScoreHelper.GetTotalUnlockedScore(enabledBadges);
            var equippedCount = profile.EquippedBadgeIds.Count(IsBadgeEnabled);

            return new BadgeSummary
            {
                Unlocked = unlocked,
                Total = total,
                Percentage = percentage,
                EquippedCount = equippedCount,
                Score = score,
                CurrentWatchStreak = GetCurrentWatchStreak(profile.Counters),
                BestWatchStreak = profile.Counters.BestWatchStreak
            };
        }
    }

    public object GetLeaderboard(int limit = 10)
    {
        lock (_lock)
        {
            var entries = _userProfiles.Values
                .Select(profile =>
                {
                    EvaluateBadges(profile, profile.UserId);
                    var enabled = profile.Badges.Where(b => IsBadgeEnabled(b.Id)).ToList();
                    var unlocked = enabled.Count(b => b.Unlocked);
                    var total = enabled.Count;
                    var percentage = total == 0 ? 0 : Math.Round((double)unlocked / total * 100.0, 1);
                    var score = AchievementScoreHelper.GetTotalUnlockedScore(enabled);

                    return new
                    {
                        UserId = profile.UserId,
                        UserName = ResolveUserName(profile.UserId),
                        Unlocked = unlocked,
                        Total = total,
                        Percentage = percentage,
                        Score = score,
                        BestWatchStreak = profile.Counters.BestWatchStreak
                    };
                })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.BestWatchStreak)
                .ThenByDescending(x => x.Unlocked)
                .ThenByDescending(x => x.Percentage)
                .Take(limit)
                .ToList();

            return entries;
        }
    }

    public object GetServerStats()
    {
        lock (_lock)
        {
            var totalUsers = _userProfiles.Count;
            var totalBadgesUnlocked = _userProfiles.Values.Sum(p => p.Badges.Count(b => b.Unlocked && IsBadgeEnabled(b.Id)));
            var totalItemsWatched = _userProfiles.Values.Sum(p => p.Counters.TotalItemsWatched);
            var totalMoviesWatched = _userProfiles.Values.Sum(p => p.Counters.MoviesWatched);
            var totalSeriesCompleted = _userProfiles.Values.Sum(p => p.Counters.SeriesCompleted);
            var totalAchievementScore = _userProfiles.Values.Sum(p => AchievementScoreHelper.GetTotalUnlockedScore(p.Badges.Where(b => IsBadgeEnabled(b.Id)).ToList()));

            var mostCommonBadge = _userProfiles.Values
                .SelectMany(p => p.Badges.Where(b => b.Unlocked && IsBadgeEnabled(b.Id)))
                .GroupBy(b => b.Id)
                .OrderByDescending(g => g.Count())
                .Select(g => g.First().Title)
                .FirstOrDefault() ?? "None";

            return new ServerStats
            {
                TotalUsers = totalUsers,
                TotalBadgesUnlocked = totalBadgesUnlocked,
                TotalItemsWatched = totalItemsWatched,
                TotalMoviesWatched = totalMoviesWatched,
                TotalSeriesCompleted = totalSeriesCompleted,
                MostCommonBadge = mostCommonBadge,
                TotalAchievementScore = totalAchievementScore
            };
        }
    }

    private static string NormalizeUserId(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return string.Empty;
        }

        if (Guid.TryParse(userId, out var guid))
        {
            return guid.ToString("D");
        }

        return userId.Trim();
    }

    private string ResolveUserName(string userId)
    {
        try
        {
            if (Guid.TryParse(userId, out var guid))
            {
                var user = _userManager.GetUserById(guid);
                if (user != null && !string.IsNullOrWhiteSpace(user.Username))
                {
                    return user.Username;
                }
            }
        }
        catch
        {
        }

        return userId;
    }

    private UserAchievementProfile GetOrCreateProfile(string userId)
    {
        if (!_userProfiles.TryGetValue(userId, out var profile))
        {
            profile = CreateProfile(userId);
            _userProfiles[userId] = profile;
            Save();
            _logger.LogInformation("Created achievement profile for user {UserId}", userId);
        }
        else
        {
            SyncDefinitions(profile, userId);
            SanitizeEquippedBadges(profile);
        }

        return profile;
    }

    private static UserAchievementProfile MergeProfiles(UserAchievementProfile a, UserAchievementProfile b)
    {
        // Prefer the profile with more watch activity; union the rest.
        var primary = b.Counters.TotalItemsWatched > a.Counters.TotalItemsWatched ? b : a;
        var secondary = ReferenceEquals(primary, a) ? b : a;

        foreach (var lib in secondary.Counters.LibrariesVisited)
        {
            primary.Counters.LibrariesVisited.Add(lib);
        }

        foreach (var date in secondary.Counters.WatchDates)
        {
            primary.Counters.WatchDates.Add(date);
        }

        foreach (var pair in secondary.Counters.MoviesByDate)
        {
            if (!primary.Counters.MoviesByDate.ContainsKey(pair.Key) ||
                primary.Counters.MoviesByDate[pair.Key] < pair.Value)
            {
                primary.Counters.MoviesByDate[pair.Key] = pair.Value;
            }
        }

        foreach (var pair in secondary.Counters.EpisodesByDate)
        {
            if (!primary.Counters.EpisodesByDate.ContainsKey(pair.Key) ||
                primary.Counters.EpisodesByDate[pair.Key] < pair.Value)
            {
                primary.Counters.EpisodesByDate[pair.Key] = pair.Value;
            }
        }

        foreach (var badge in secondary.Badges)
        {
            var existing = primary.Badges.FirstOrDefault(x => x.Id.Equals(badge.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                primary.Badges.Add(badge);
                continue;
            }

            if (badge.CurrentValue > existing.CurrentValue)
            {
                existing.CurrentValue = badge.CurrentValue;
            }

            if (badge.Unlocked && !existing.Unlocked)
            {
                existing.Unlocked = true;
                existing.UnlockedAt = badge.UnlockedAt ?? existing.UnlockedAt;
            }
        }

        foreach (var equipped in secondary.EquippedBadgeIds)
        {
            if (!primary.EquippedBadgeIds.Contains(equipped, StringComparer.OrdinalIgnoreCase))
            {
                primary.EquippedBadgeIds.Add(equipped);
            }
        }

        return primary;
    }

    private static UserAchievementProfile CreateProfile(string userId)
    {
        return new UserAchievementProfile
        {
            UserId = userId,
            Counters = new UserAchievementCounters(),
            Badges = AchievementDefinitions.All.Select(def => CreateBadgeFromDefinition(def, userId)).ToList(),
            EquippedBadgeIds = new List<string>()
        };
    }

    private static void SyncDefinitions(UserAchievementProfile profile, string userId)
    {
        foreach (var def in AchievementDefinitions.All)
        {
            var existing = profile.Badges.FirstOrDefault(b => b.Id.Equals(def.Id, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                profile.Badges.Add(CreateBadgeFromDefinition(def, userId));
                continue;
            }

            existing.Key = def.Key;
            existing.Title = def.Title;
            existing.Description = def.Description;
            existing.Icon = def.Icon;
            existing.Category = def.Category;
            existing.Rarity = def.Rarity;
            existing.TargetValue = def.TargetValue;
        }
    }

    private static void SanitizeEquippedBadges(UserAchievementProfile profile)
    {
        var unlockedIds = profile.Badges
            .Where(b => b.Unlocked && IsBadgeEnabled(b.Id))
            .Select(b => b.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        profile.EquippedBadgeIds = profile.EquippedBadgeIds
            .Where(id => unlockedIds.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    private void EvaluateBadges(UserAchievementProfile profile, string userId)
    {
        foreach (var def in AchievementDefinitions.All)
        {
            var badge = profile.Badges.First(b => b.Id.Equals(def.Id, StringComparison.OrdinalIgnoreCase));
            var current = Math.Clamp(GetMetricValue(profile.Counters, def.Metric), 0, def.TargetValue);

            var wasUnlocked = badge.Unlocked;
            badge.CurrentValue = current;

            if (!badge.Unlocked && current >= def.TargetValue)
            {
                badge.Unlocked = true;
                badge.UnlockedAt = DateTimeOffset.UtcNow;
                _logger.LogInformation("Unlocked badge {BadgeId} for user {UserId}", def.Id, userId);
            }

            if (wasUnlocked && badge.UnlockedAt is null)
            {
                badge.UnlockedAt = DateTimeOffset.UtcNow;
            }
        }

        SanitizeEquippedBadges(profile);
    }

    private static int GetMetricValue(UserAchievementCounters counters, AchievementMetric metric)
    {
        return metric switch
        {
            AchievementMetric.TotalItemsWatched => counters.TotalItemsWatched,
            AchievementMetric.MoviesWatched => counters.MoviesWatched,
            AchievementMetric.SeriesCompleted => counters.SeriesCompleted,
            AchievementMetric.LateNightSessions => counters.LateNightSessions,
            AchievementMetric.EarlyMorningSessions => counters.EarlyMorningSessions,
            AchievementMetric.WeekendSessions => counters.WeekendSessions,
            AchievementMetric.UniqueLibrariesVisited => counters.UniqueLibrariesVisited,
            AchievementMetric.DaysWatched => counters.DaysWatched,
            AchievementMetric.CurrentWatchStreak => GetCurrentWatchStreak(counters),
            AchievementMetric.BestWatchStreak => counters.BestWatchStreak,
            AchievementMetric.MaxEpisodesInSingleDay => counters.MaxEpisodesInSingleDay,
            AchievementMetric.MaxMoviesInSingleDay => counters.MaxMoviesInSingleDay,
            AchievementMetric.UniqueDecadesWatched => counters.UniqueDecadesWatched,
            AchievementMetric.UniqueCountriesWatched => counters.UniqueCountriesWatched,
            AchievementMetric.UniqueLanguagesWatched => counters.UniqueLanguagesWatched,
            AchievementMetric.UniqueGenresWatched => counters.UniqueGenresWatched,
            AchievementMetric.TotalMinutesWatched => counters.TotalMinutesWatched > int.MaxValue ? int.MaxValue : (int)counters.TotalMinutesWatched,
            AchievementMetric.LongestItemMinutes => counters.LongestItemMinutes,
            AchievementMetric.ShortItemsWatched => counters.ShortItemsWatched,
            AchievementMetric.WatchedOnChristmas => counters.WatchedOnChristmas ? 1 : 0,
            AchievementMetric.WatchedOnNewYear => counters.WatchedOnNewYear ? 1 : 0,
            AchievementMetric.WatchedOnHalloween => counters.WatchedOnHalloween ? 1 : 0,
            AchievementMetric.LongSeriesCompleted => counters.LongSeriesCompleted,
            AchievementMetric.VeryLongSeriesCompleted => counters.VeryLongSeriesCompleted,
            AchievementMetric.RewatchCount => counters.RewatchCount,
            _ => 0
        };
    }

    private static bool IsBadgeEnabled(string badgeId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.DisabledBadgeIds is null || config.DisabledBadgeIds.Count == 0)
        {
            return true;
        }

        return !config.DisabledBadgeIds.Contains(badgeId, StringComparer.OrdinalIgnoreCase);
    }

    private List<AchievementBadge> GetEnabledBadgeClones(UserAchievementProfile profile)
    {
        return profile.Badges
            .Where(b => IsBadgeEnabled(b.Id))
            .Select(CloneBadge)
            .ToList();
    }

    private static int GetCurrentWatchStreak(UserAchievementCounters counters)
    {
        if (counters.WatchDates.Count == 0)
        {
            return 0;
        }

        var dates = counters.WatchDates
            .Select(d => DateOnly.TryParse(d, out var parsed) ? parsed : default)
            .Where(d => d != default)
            .OrderByDescending(d => d)
            .ToList();

        if (dates.Count == 0)
        {
            return 0;
        }

        var streak = 1;
        var current = dates[0];

        for (var i = 1; i < dates.Count; i++)
        {
            if (dates[i] == current.AddDays(-1))
            {
                streak++;
                current = dates[i];
            }
            else if (dates[i] == current)
            {
                continue;
            }
            else
            {
                break;
            }
        }

        return streak;
    }

    private void Load()
    {
        if (!File.Exists(_dataFilePath))
        {
            _userProfiles = new Dictionary<string, UserAchievementProfile>();
            _logger.LogInformation("No badge data file found, starting with empty store.");
            return;
        }

        try
        {
            var json = File.ReadAllText(_dataFilePath);
            var store = JsonSerializer.Deserialize<UserBadgeStore>(json, _jsonOptions);

            var rawProfiles = store?.UserProfiles ?? new Dictionary<string, UserAchievementProfile>();
            _userProfiles = new Dictionary<string, UserAchievementProfile>();
            var migrated = false;

            foreach (var pair in rawProfiles)
            {
                var canonicalKey = NormalizeUserId(pair.Key);
                var profile = pair.Value;
                profile.UserId = canonicalKey;

                if (_userProfiles.TryGetValue(canonicalKey, out var existing))
                {
                    _userProfiles[canonicalKey] = MergeProfiles(existing, profile);
                    migrated = true;
                }
                else
                {
                    _userProfiles[canonicalKey] = profile;
                }

                if (!string.Equals(pair.Key, canonicalKey, StringComparison.Ordinal))
                {
                    migrated = true;
                }
            }

            foreach (var profile in _userProfiles.Values)
            {
                SyncDefinitions(profile, profile.UserId);
                EvaluateBadges(profile, profile.UserId);
            }

            if (migrated)
            {
                _logger.LogInformation("Canonicalized achievement profile user keys.");
                Save();
            }

            _logger.LogInformation("Loaded achievement data for {UserCount} users.", _userProfiles.Count);
        }
        catch (Exception ex)
        {
            _userProfiles = new Dictionary<string, UserAchievementProfile>();
            _logger.LogError(ex, "Failed to load achievement data, starting with empty store.");
        }
    }

    private void Save()
    {
        var store = new UserBadgeStore
        {
            UserProfiles = _userProfiles
        };

        var json = JsonSerializer.Serialize(store, _jsonOptions);
        File.WriteAllText(_dataFilePath, json);
    }

    private static AchievementBadge CreateBadgeFromDefinition(AchievementDefinition def, string userId)
    {
        return new AchievementBadge
        {
            Id = def.Id,
            UserId = userId,
            Key = def.Key,
            Title = def.Title,
            Description = def.Description,
            Icon = def.Icon,
            Category = def.Category,
            Unlocked = false,
            UnlockedAt = null,
            CurrentValue = 0,
            TargetValue = def.TargetValue,
            Rarity = def.Rarity
        };
    }

    private static AchievementBadge CloneBadge(AchievementBadge badge)
    {
        return new AchievementBadge
        {
            Id = badge.Id,
            UserId = badge.UserId,
            Key = badge.Key,
            Title = badge.Title,
            Description = badge.Description,
            Icon = badge.Icon,
            Category = badge.Category,
            Unlocked = badge.Unlocked,
            UnlockedAt = badge.UnlockedAt,
            CurrentValue = badge.CurrentValue,
            TargetValue = badge.TargetValue,
            Rarity = badge.Rarity
        };
    }
}