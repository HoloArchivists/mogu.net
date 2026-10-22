using Microsoft.AspNetCore.Authorization;
using Mogu.Core.Entities;

namespace Mogu.Web.Infrastructure;

public static class Policies
{
    public const string Trusted = nameof(Trusted);
    public const string Moderator = nameof(Moderator);
    public const string Administrator = nameof(Administrator);

    public static AuthorizationBuilder AddMoguPolicies(this AuthorizationBuilder builder) => builder
        .AddPolicy(Trusted, p => p.AddRequirements(new MinimumLevelRequirement(UserLevel.Trusted)))
        .AddPolicy(Moderator, p => p.AddRequirements(new MinimumLevelRequirement(UserLevel.Moderator)))
        .AddPolicy(Administrator, p => p.AddRequirements(new MinimumLevelRequirement(UserLevel.Administrator)));
}

/// <summary>Requires the database user (not a cached claim) to be at least <paramref name="Level"/>.</summary>
public sealed record MinimumLevelRequirement(UserLevel Level) : IAuthorizationRequirement;

internal sealed class MinimumLevelHandler(CurrentUser currentUser) : AuthorizationHandler<MinimumLevelRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, MinimumLevelRequirement requirement)
    {
        if (currentUser.User is { } user && user.Level >= requirement.Level)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
