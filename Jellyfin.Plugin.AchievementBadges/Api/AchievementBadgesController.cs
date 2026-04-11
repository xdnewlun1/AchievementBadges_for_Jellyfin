using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AchievementBadges.Models;
using Jellyfin.Plugin.AchievementBadges.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AchievementBadges.Api;

[ApiController]
[Authorize]
[Route("Plugins/AchievementBadges")]
public class AchievementBadgesController : ControllerBase
{
    private readonly AchievementBadgeService _badgeService;
    private readonly PlaybackCompletionService _playbackCompletionService;
    private readonly WatchHistoryBackfillService _backfillService;

    public AchievementBadgesController(
        AchievementBadgeService badgeService,
        PlaybackCompletionService playbackCompletionService,
        WatchHistoryBackfillService backfillService)
    {
        _badgeService = badgeService;
        _playbackCompletionService = playbackCompletionService;
        _backfillService = backfillService;
    }

    [HttpGet("test")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    public ActionResult<string> Test()
    {
        return Ok("Achievement Badges plugin working!");
    }

    [HttpGet("client-script/{name}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetClientScript([FromRoute] string name)
    {
        foreach (var ch in name)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '_')
            {
                return NotFound();
            }
        }

        var content = ResourceReader.ReadEmbeddedText(
            "Jellyfin.Plugin.AchievementBadges.Pages." + name + ".js");

        if (content is null)
        {
            return NotFound();
        }

        return Content(content, "application/javascript");
    }

    [HttpGet("users/{userId}")]
    [ProducesResponseType(typeof(List<AchievementBadge>), StatusCodes.Status200OK)]
    public ActionResult<List<AchievementBadge>> GetBadgesForUser([FromRoute] string userId)
    {
        var badges = _badgeService.GetBadgesForUser(userId);
        return Ok(badges);
    }

    [HttpGet("users/{userId}/badge/{badgeId}")]
    [ProducesResponseType(typeof(AchievementBadge), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AchievementBadge> GetBadge([FromRoute] string userId, [FromRoute] string badgeId)
    {
        var badge = _badgeService.GetBadge(userId, badgeId);

        if (badge is null)
        {
            return NotFound();
        }

        return Ok(badge);
    }

    [HttpGet("users/{userId}/newly-unlocked")]
    [ProducesResponseType(typeof(List<AchievementBadge>), StatusCodes.Status200OK)]
    public ActionResult<List<AchievementBadge>> GetNewlyUnlocked([FromRoute] string userId)
    {
        var badges = _badgeService.GetBadgesForUser(userId)
            .FindAll(b => b.Unlocked && b.UnlockedAt.HasValue);

        return Ok(badges);
    }

    [HttpGet("users/{userId}/recent-unlocks")]
    [ProducesResponseType(typeof(List<AchievementBadge>), StatusCodes.Status200OK)]
    public ActionResult<List<AchievementBadge>> GetRecentUnlocks([FromRoute] string userId, [FromQuery] int limit = 8)
    {
        if (limit < 1)
        {
            limit = 1;
        }

        if (limit > 50)
        {
            limit = 50;
        }

        var badges = _badgeService.GetBadgesForUser(userId)
            .Where(b => b.Unlocked && b.UnlockedAt.HasValue)
            .OrderByDescending(b => b.UnlockedAt)
            .Take(limit)
            .ToList();

        return Ok(badges);
    }

    [HttpGet("users/{userId}/next-badges")]
    [ProducesResponseType(typeof(List<AchievementBadge>), StatusCodes.Status200OK)]
    public ActionResult<List<AchievementBadge>> GetNextBadges([FromRoute] string userId, [FromQuery] int limit = 5)
    {
        if (limit < 1)
        {
            limit = 1;
        }

        if (limit > 20)
        {
            limit = 20;
        }

        var badges = _badgeService.GetBadgesForUser(userId)
            .Where(b => !b.Unlocked && b.TargetValue > 0)
            .OrderByDescending(b => (double)b.CurrentValue / b.TargetValue)
            .ThenBy(b => b.TargetValue - b.CurrentValue)
            .Take(limit)
            .ToList();

        return Ok(badges);
    }

    [HttpGet("users/{userId}/playback-state")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetPlaybackState([FromRoute] string userId)
    {
        var state = _playbackCompletionService.GetState(userId);
        return Ok(state);
    }

    [HttpPost("users/{userId}/record-completion")]
    [ProducesResponseType(typeof(List<AchievementBadge>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    public ActionResult<List<AchievementBadge>> RecordCompletion(
        [FromRoute] string userId,
        [FromQuery] string? itemId = null,
        [FromQuery] double completionPercent = 100,
        [FromQuery] bool isMovie = false,
        [FromQuery] bool isEpisode = true,
        [FromQuery] bool isSeriesCompleted = false)
    {
        var success = _playbackCompletionService.RecordCompletion(
            userId,
            itemId,
            isMovie,
            isEpisode,
            isSeriesCompleted,
            completionPercent,
            System.DateTimeOffset.Now,
            out var message);

        if (!success)
        {
            return BadRequest(new { Message = message });
        }

        var badges = _badgeService.GetBadgesForUser(userId);
        return Ok(badges);
    }

    [HttpPost("users/{userId}/unlock/{badgeId}")]
    [ProducesResponseType(typeof(AchievementBadge), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AchievementBadge> UnlockBadge([FromRoute] string userId, [FromRoute] string badgeId)
    {
        var badge = _badgeService.UnlockBadge(userId, badgeId);

        if (badge is null)
        {
            return NotFound();
        }

        return Ok(badge);
    }

    [HttpPost("users/{userId}/progress/{badgeId}")]
    [ProducesResponseType(typeof(AchievementBadge), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AchievementBadge> AddProgress(
        [FromRoute] string userId,
        [FromRoute] string badgeId,
        [FromQuery] int amount = 1)
    {
        var badge = _badgeService.UpdateProgress(userId, badgeId, amount);

        if (badge is null)
        {
            return NotFound();
        }

        return Ok(badge);
    }

    [HttpPost("users/{userId}/simulate-playback")]
    [ProducesResponseType(typeof(List<AchievementBadge>), StatusCodes.Status200OK)]
    public ActionResult<List<AchievementBadge>> SimulatePlayback(
        [FromRoute] string userId,
        [FromQuery] bool isMovie = false,
        [FromQuery] bool isSeriesCompleted = false)
    {
        _badgeService.RecordPlayback(
            userId,
            isMovie,
            !isMovie,
            isSeriesCompleted,
            null,
            System.DateTimeOffset.Now);

        var badges = _badgeService.GetBadgesForUser(userId);
        return Ok(badges);
    }

    [HttpPost("users/{userId}/reset")]
    [ProducesResponseType(typeof(List<AchievementBadge>), StatusCodes.Status200OK)]
    public ActionResult<List<AchievementBadge>> ResetBadges([FromRoute] string userId)
    {
        var badges = _badgeService.ResetBadgesForUser(userId);
        return Ok(badges);
    }

    [HttpGet("users/{userId}/equipped")]
    [ProducesResponseType(typeof(List<AchievementBadge>), StatusCodes.Status200OK)]
    public ActionResult<List<AchievementBadge>> GetEquipped([FromRoute] string userId)
    {
        var badges = _badgeService.GetEquippedBadges(userId);
        return Ok(badges);
    }

    [HttpPost("users/{userId}/equipped/{badgeId}")]
    [ProducesResponseType(typeof(List<AchievementBadge>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    public ActionResult<List<AchievementBadge>> EquipBadge([FromRoute] string userId, [FromRoute] string badgeId)
    {
        var success = _badgeService.EquipBadge(userId, badgeId, out var message);

        if (!success)
        {
            return BadRequest(new { Message = message });
        }

        var badges = _badgeService.GetEquippedBadges(userId);
        return Ok(badges);
    }

    [HttpDelete("users/{userId}/equipped/{badgeId}")]
    [ProducesResponseType(typeof(List<AchievementBadge>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    public ActionResult<List<AchievementBadge>> UnequipBadge([FromRoute] string userId, [FromRoute] string badgeId)
    {
        var success = _badgeService.UnequipBadge(userId, badgeId, out var message);

        if (!success)
        {
            return BadRequest(new { Message = message });
        }

        var badges = _badgeService.GetEquippedBadges(userId);
        return Ok(badges);
    }

    [HttpGet("users/{userId}/summary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetSummary([FromRoute] string userId)
    {
        var summary = _badgeService.GetSummary(userId);
        return Ok(summary);
    }

    [HttpGet("leaderboard")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetLeaderboard([FromQuery] int limit = 10)
    {
        var leaderboard = _badgeService.GetLeaderboard(limit);
        return Ok(leaderboard);
    }

    [HttpGet("server/stats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetServerStats()
    {
        var stats = _badgeService.GetServerStats();
        return Ok(stats);
    }

    [HttpPost("users/{userId}/backfill")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult BackfillUser([FromRoute] string userId)
    {
        var result = _backfillService.BackfillUser(userId);
        return Ok(result);
    }

    [HttpPost("backfill-all")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult BackfillAll()
    {
        var result = _backfillService.BackfillAllUsers();
        return Ok(result);
    }

    [HttpGet("admin/badge-catalog")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetBadgeCatalog()
    {
        var config = Plugin.Instance?.Configuration;
        var disabled = new HashSet<string>(
            config?.DisabledBadgeIds ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        var catalog = AchievementDefinitions.All
            .GroupBy(d => d.Category)
            .Select(g => new
            {
                Category = g.Key,
                Badges = g.Select(d => new
                {
                    d.Id,
                    d.Title,
                    d.Description,
                    d.Icon,
                    d.Rarity,
                    d.TargetValue,
                    Disabled = disabled.Contains(d.Id)
                }).ToList()
            })
            .ToList();

        return Ok(new
        {
            Catalog = catalog,
            DisabledBadgeIds = disabled.ToList()
        });
    }

    public class BadgeToggleRequest
    {
        public string? BadgeId { get; set; }
        public bool Disabled { get; set; }
    }

    public class BadgeBulkToggleRequest
    {
        public List<string>? DisabledBadgeIds { get; set; }
    }

    [HttpPost("admin/badge-catalog/toggle")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult ToggleBadge([FromBody] BadgeToggleRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.BadgeId))
        {
            return BadRequest(new { Message = "BadgeId is required." });
        }

        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return BadRequest(new { Message = "Plugin instance not available." });
        }

        var config = plugin.Configuration;
        config.DisabledBadgeIds ??= new List<string>();

        var exists = config.DisabledBadgeIds
            .Any(id => id.Equals(request.BadgeId, StringComparison.OrdinalIgnoreCase));

        if (request.Disabled && !exists)
        {
            config.DisabledBadgeIds.Add(request.BadgeId);
        }
        else if (!request.Disabled && exists)
        {
            config.DisabledBadgeIds.RemoveAll(id =>
                id.Equals(request.BadgeId, StringComparison.OrdinalIgnoreCase));
        }

        plugin.UpdateConfiguration(config);
        return Ok(new { Success = true, DisabledBadgeIds = config.DisabledBadgeIds });
    }

    [HttpPost("admin/badge-catalog/bulk")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult BulkSetDisabled([FromBody] BadgeBulkToggleRequest request)
    {
        var plugin = Plugin.Instance;
        if (plugin is null)
        {
            return BadRequest(new { Message = "Plugin instance not available." });
        }

        var config = plugin.Configuration;
        config.DisabledBadgeIds = (request?.DisabledBadgeIds ?? new List<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        plugin.UpdateConfiguration(config);
        return Ok(new { Success = true, DisabledBadgeIds = config.DisabledBadgeIds });
    }
}