using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AchievementBadges.Models;

namespace Jellyfin.Plugin.AchievementBadges.Services;

public class QuestService
{
    private readonly AchievementBadgeService _badgeService;

    public record QuestTemplate(string Id, string Title, string Description, AchievementMetric Metric, int Target, int Reward, string Icon);

    public static readonly IReadOnlyList<QuestTemplate> Templates = new List<QuestTemplate>
    {
        new("watch-any-1",        "Daily Watch",       "Watch any item today.",                AchievementMetric.TotalItemsWatched, 1, 20, "play_circle"),
        new("watch-movie-1",      "Movie Night",       "Watch 1 movie today.",                 AchievementMetric.MoviesWatched,     1, 30, "movie"),
        new("watch-ep-3",         "Episode Spree",     "Watch 3 episodes today.",              AchievementMetric.MaxEpisodesInSingleDay, 3, 40, "live_tv"),
        new("explore-lib-2",      "Library Hop",       "Watch from 2 different libraries.",    AchievementMetric.UniqueLibrariesVisited, 2, 30, "collections_bookmark"),
        new("late-night-1",       "Night Watch",       "Catch a late-night session.",          AchievementMetric.LateNightSessions, 1, 25, "dark_mode"),
        new("early-bird-1",       "Early Start",       "Catch an early-morning session.",      AchievementMetric.EarlyMorningSessions, 1, 25, "wb_sunny"),
        new("genre-explore-1",    "Genre Explorer",    "Watch from a new genre.",              AchievementMetric.UniqueGenresWatched, 1, 20, "category"),
        new("rewatch-1",          "Comfort Watch",     "Rewatch one of your favourites.",      AchievementMetric.RewatchCount, 1, 20, "replay")
    };

    public QuestService(AchievementBadgeService badgeService)
    {
        _badgeService = badgeService;
    }

    public object GetOrCreate(string userId)
    {
        var profile = _badgeService.PeekProfile(userId);
        if (profile is null) return new { };

        var today = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd");

        if (profile.DailyQuestDate != today)
        {
            // Pick a quest deterministically based on the day so all users share the same quest
            var seed = today.GetHashCode();
            var tpl = Templates[Math.Abs(seed) % Templates.Count];
            profile.DailyQuestId = tpl.Id;
            profile.DailyQuestDate = today;
            profile.DailyQuestCompleted = false;
            profile.DailyQuestStartValue = GetCounterValue(profile.Counters, tpl.Metric);
            _badgeService.SaveProfileDirect(profile);
        }

        var questTpl = Templates.FirstOrDefault(t => t.Id == profile.DailyQuestId) ?? Templates[0];
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

    private static int GetCounterValue(UserAchievementCounters counters, AchievementMetric metric)
    {
        return metric switch
        {
            AchievementMetric.TotalItemsWatched => counters.TotalItemsWatched,
            AchievementMetric.MoviesWatched => counters.MoviesWatched,
            AchievementMetric.MaxEpisodesInSingleDay => counters.MaxEpisodesInSingleDay,
            AchievementMetric.UniqueLibrariesVisited => counters.UniqueLibrariesVisited,
            AchievementMetric.LateNightSessions => counters.LateNightSessions,
            AchievementMetric.EarlyMorningSessions => counters.EarlyMorningSessions,
            AchievementMetric.UniqueGenresWatched => counters.UniqueGenresWatched,
            AchievementMetric.RewatchCount => counters.RewatchCount,
            _ => 0
        };
    }
}
