using System.ComponentModel.DataAnnotations;

namespace Mogu.Core.Options;

/// <summary>Discord OAuth2 login, bound from the <c>Discord</c> configuration section.</summary>
public sealed class DiscordOptions
{
    public const string SectionName = "Discord";

    [Required]
    public string ClientId { get; set; } = "";

    [Required]
    public string ClientSecret { get; set; } = "";

    /// <summary>Guild the user must be a member of to log in.</summary>
    [Required]
    public string GuildId { get; set; } = "";

    /// <summary>Path Discord redirects back to; must match the redirect URI registered with the Discord app.</summary>
    [Required]
    public string CallbackPath { get; set; } = "/discord/oauth2/callback";

    /// <summary>Users need at least one of these roles to log in.</summary>
    public List<string> LoginRoles { get; set; } = [];

    public List<string> TrustedRoles { get; set; } = [];

    public List<string> ModeratorRoles { get; set; } = [];

    public List<string> AdminRoles { get; set; } = [];
}
