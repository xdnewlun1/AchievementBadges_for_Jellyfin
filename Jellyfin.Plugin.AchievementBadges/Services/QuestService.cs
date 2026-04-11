using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.AchievementBadges.Models;

namespace Jellyfin.Plugin.AchievementBadges.Services;

public class QuestService
{
    private readonly AchievementBadgeService _badgeService;

    public record QuestTemplate(string Id, string Title, string Description, AchievementMetric Metric, int Target, int Reward, string Icon);

    public static readonly IReadOnlyList<QuestTemplate> DailyTemplates = new List<QuestTemplate>
    {
        new("daily-watch-any",        "Daily Watch",         "Watch any item today.",                     AchievementMetric.TotalItemsWatched, 1, 20, "play_circle"),
        new("daily-movie",            "Movie Night",         "Watch 1 movie today.",                      AchievementMetric.MoviesWatched,     1, 30, "movie"),
        new("daily-ep-spree",         "Episode Spree",       "Watch 3 episodes today.",                   AchievementMetric.MaxEpisodesInSingleDay, 3, 40, "live_tv"),
        new("daily-lib-hop",          "Library Hop",         "Watch from 2 different libraries today.",   AchievementMetric.UniqueLibrariesVisited, 2, 30, "collections_bookmark"),
        new("daily-late-night",       "Night Watch",         "Catch a late-night session.",               AchievementMetric.LateNightSessions, 1, 25, "dark_mode"),
        new("daily-early-bird",       "Early Start",         "Catch an early-morning session.",           AchievementMetric.EarlyMorningSessions, 1, 25, "wb_sunny"),
        new("daily-genre",            "Genre Explorer",      "Watch from a new genre today.",             AchievementMetric.UniqueGenresWatched, 1, 20, "category"),
        new("daily-rewatch",          "Comfort Watch",       "Rewatch one of your favourites.",           AchievementMetric.RewatchCount, 1, 20, "replay"),
        new("daily-double",           "Double Feature",      "Watch 2 movies today.",                     AchievementMetric.MaxMoviesInSingleDay, 2, 50, "local_movies"),
        new("daily-binge-lite",       "Light Binger",        "Watch 5 items today.",                      AchievementMetric.TotalItemsWatched, 5, 60, "bolt"),
        new("daily-runtime",          "Long Haul",           "Watch an item over 2 hours long.",          AchievementMetric.LongestItemMinutes, 120, 40, "timer"),
        new("daily-short",            "Bite-Size",           "Watch an item under 30 minutes.",           AchievementMetric.ShortItemsWatched, 1, 15, "speed")
    };

    public static readonly IReadOnlyList<QuestTemplate> WeeklyTemplates = new List<QuestTemplate>
    {
        new("weekly-5-movies",        "Cinephile",           "Watch 5 movies this week.",                 AchievementMetric.MoviesWatched,     5, 150, "theaters"),
        new("weekly-20-episodes",     "Series Sprinter",     "Watch 20 episodes this week.",              AchievementMetric.TotalItemsWatched, 20, 200, "tv"),
        new("weekly-streak",          "Daily Dedication",    "Maintain a 5-day watch streak.",            AchievementMetric.CurrentWatchStreak, 5, 180, "timeline"),
        new("weekly-10-hours",        "Marathon Week",       "Watch 10 hours of content this week.",      AchievementMetric.TotalMinutesWatched, 600, 200, "hourglass_top"),
        new("weekly-3-genres",        "Genre Week",          "Watch from 3 different genres this week.",  AchievementMetric.UniqueGenresWatched, 3, 120, "swap_horiz"),
        new("weekly-finish-series",   "Finisher",            "Finish a series this week.",                AchievementMetric.SeriesCompleted,   1, 250, "check_circle"),
        new("weekly-late-3",          "Creature of the Night", "Have 3 late-night sessions this week.",   AchievementMetric.LateNightSessions, 3, 120, "nights_stay"),
        new("weekly-weekend",         "Weekend Binge",       "Have 4 weekend sessions this week.",        AchievementMetric.WeekendSessions,   4, 140, "event_available")
    };

    public QuestService(AchievementBadgeService badgeService)
    {
        _badgeService = badgeService;
    }

    public object GetOrCreate(string userId)
    {
        return new
        {
            Daily = GetOrCreateDaily(userId),
            Weekly = GetOrCreateWeekly(userId)
        };
    }

    public object GetOrCreateDaily(string userId)
    {
        var profile = _badgeService.PeekProfile(userId);
        if (profile is null) return new { };

        var today = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");

        if (profile.DailyQuestDate != today)
        {
            var seed = today.GetHashCode();
            var tpl = DailyTemplates[Math.Abs(seed) % DailyTemplates.Count];
            profile.DailyQuestId = tpl.Id;
            profile.DailyQuestDate = today;
            profile.DailyQuestCompleted = false;
            profile.DailyQuestStartValue = GetCounterValue(profile.Counters, tpl.Metric);
            _badgeService.SaveProfileDirect(profile);
        }

        var questTpl = DailyTemplates.FirstOrDefault(t => t.Id == profile.DailyQuestId) ?? DailyTemplates[0];
        var current = GetCounterValue(profile.Counters, questTpl.Metric) - profile.DailyQuestStartValue;
        if (current < 0) current = 0;
        var complete = current >= questTpl.Target;

        if (complete && !profile.DailyQuestCompleted)
        {
            profile.DailyQuestCompleted = true;
            profile.ScoreBank += questTpl.Reward;
            _badgeService.SaveProfileDirect(profile);
        }

        return new
        {
            Kind = "daily",
            questTpl.Id,
            questTpl.Title,
            questTpl.Description,
            questTpl.Icon,
            questTpl.Reward,
            Target = questTpl.Target,
            Current = Math.Min(current, questTpl.Target),
            Completed = profile.DailyQuestCompleted,
            Date = today
        };
    }

    public object GetOrCreateWeekly(string userId)
    {
        var profile = _badgeService.PeekProfile(userId);
        if (profile is null) return new { };

        var now = DateTime.Today;
        var isoWeek = ISOWeek.GetWeekOfYear(now);
        var isoYear = ISOWeek.GetYear(now);
        var weekKey = isoYear + "-W" + isoWeek.ToString("D2");

        if (profile.WeeklyQuestWeek != weekKey)
        {
            var seed = weekKey.GetHashCode();
            var tpl = WeeklyTemplates[Math.Abs(seed) % WeeklyTemplates.Count];
            profile.WeeklyQuestId = tpl.Id;
            profile.WeeklyQuestWeek = weekKey;
            profile.WeeklyQuestCompleted = false;
            profile.WeeklyQuestStartValue = GetCounterValue(profile.Counters, tpl.Metric);
            _badgeService.SaveProfileDirect(profile);
        }

        var questTpl = WeeklyTemplates.FirstOrDefault(t => t.Id == profile.WeeklyQuestId) ?? WeeklyTemplates[0];
        var current = GetCounterValue(profile.Counters, questTpl.Metric) - profile.WeeklyQuestStartValue;
        if (current < 0) current = 0;
        var complete = current >= questTpl.Target;

        if (complete && !profile.WeeklyQuestCompleted)
        {
            profile.WeeklyQuestCompleted = true;
            profile.ScoreBank += questTpl.Reward;
            _badgeService.SaveProfileDirect(profile);
        }

        return new
        {
            Kind = "weekly",
            questTpl.Id,
            questTpl.Title,
            questTpl.Description,
            questTpl.Icon,
            questTpl.Reward,
            Target = questTpl.Target,
            Current = Math.Min(current, questTpl.Target),
            Completed = profile.WeeklyQuestCompleted,
            Week = weekKey
        };
    }

    private static int GetCounterValue(UserAchievementCounters counters, AchievementMetric metric)
    {
        return metric switch
        {
            AchievementMetric.TotalItemsWatched => counters.TotalItemsWatched,
            AchievementMetric.MoviesWatched => counters.MoviesWatched,
            AchievementMetric.MaxEpisodesInSingleDay => counters.MaxEpisodesInSingleDay,
            AchievementMetric.MaxMoviesInSingleDay => counters.MaxMoviesInSingleDay,
            AchievementMetric.UniqueLibrariesVisited => counters.UniqueLibrariesVisited,
            AchievementMetric.LateNightSessions => counters.LateNightSessions,
            AchievementMetric.EarlyMorningSessions => counters.EarlyMorningSessions,
            AchievementMetric.WeekendSessions => counters.WeekendSessions,
            AchievementMetric.UniqueGenresWatched => counters.UniqueGenresWatched,
            AchievementMetric.RewatchCount => counters.RewatchCount,
            AchievementMetric.TotalMinutesWatched => counters.TotalMinutesWatched > int.MaxValue ? int.MaxValue : (int)counters.TotalMinutesWatched,
            AchievementMetric.CurrentWatchStreak => counters.BestWatchStreak,
            AchievementMetric.SeriesCompleted => counters.SeriesCompleted,
            AchievementMetric.LongestItemMinutes => counters.LongestItemMinutes,
            AchievementMetric.ShortItemsWatched => counters.ShortItemsWatched,
            _ => 0
        };
    }
}
