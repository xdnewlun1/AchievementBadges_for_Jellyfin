using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.AchievementBadges.Api;

// Enforces that an authenticated, non-admin caller may only touch /users/{userId}/*
// routes whose {userId} matches their own Jellyfin-UserId claim. Admins (callers who
// satisfy the RequiresElevation policy) are allowed through. Endpoints that are
// already [Authorize(Policy = "RequiresElevation")] are skipped (admin-only already).
// Endpoints explicitly marked [AllowAnonymous] are also skipped so the handler can
// implement its own public-access policy (see GetProfileCard).
public class UserOwnershipFilter : IAsyncActionFilter
{
    private readonly IAuthorizationService _authz;

    public UserOwnershipFilter(IAuthorizationService authz)
    {
        _authz = authz;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!ShouldEnforce(context))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var user = context.HttpContext.User;
        var claimUserId = user.FindFirst("Jellyfin-UserId")?.Value;

        // Reached here without a Jellyfin-UserId claim — the base [Authorize] should
        // have rejected the request. Defensively 401 rather than silently allowing.
        if (string.IsNullOrEmpty(claimUserId))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var elevated = await _authz.AuthorizeAsync(user, null, "RequiresElevation").ConfigureAwait(false);
        if (elevated.Succeeded)
        {
            await next().ConfigureAwait(false);
            return;
        }

        var routeUserId = context.RouteData.Values["userId"]?.ToString();
        if (!MatchesUser(claimUserId, routeUserId))
        {
            context.Result = new ForbidResult();
            return;
        }

        await next().ConfigureAwait(false);
    }

    private static bool ShouldEnforce(ActionExecutingContext context)
    {
        if (!context.RouteData.Values.ContainsKey("userId"))
        {
            return false;
        }

        var metadata = context.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<AuthorizeAttribute>()
            .Any(a => string.Equals(a.Policy, "RequiresElevation", StringComparison.Ordinal)))
        {
            return false;
        }

        if (metadata.OfType<IAllowAnonymous>().Any())
        {
            return false;
        }

        return true;
    }

    private static bool MatchesUser(string claimUserId, string? routeUserId)
    {
        if (string.IsNullOrWhiteSpace(routeUserId))
        {
            return false;
        }

        if (Guid.TryParse(claimUserId, out var a) && Guid.TryParse(routeUserId, out var b))
        {
            return a == b;
        }

        return string.Equals(claimUserId, routeUserId, StringComparison.OrdinalIgnoreCase);
    }
}
