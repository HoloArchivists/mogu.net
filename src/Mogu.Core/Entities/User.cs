namespace Mogu.Core.Entities;

public class User
{
    public int Id { get; set; }

    /// <summary>Discord snowflake of the account this user logs in with.</summary>
    public long DiscordId { get; set; }

    public required string Username { get; set; }

    public UserStatus Status { get; set; } = UserStatus.Active;

    public UserLevel Level { get; set; } = UserLevel.Regular;

    public DateTime CreatedAt { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public string? LastLoginIp { get; set; }

    public string? RegistrationIp { get; set; }

    /// <summary>Collapse the comment section on torrent pages by default.</summary>
    public bool HideComments { get; set; }

    /// <summary>SHA-256 of the user's API token, if one was generated.</summary>
    public byte[]? ApiTokenHash { get; set; }

    public List<Torrent> Torrents { get; set; } = [];

    public List<Comment> Comments { get; set; } = [];

    public bool IsTrusted => Level >= UserLevel.Trusted;

    public bool IsModerator => Level >= UserLevel.Moderator;

    public bool IsAdministrator => Level >= UserLevel.Administrator;

    public bool IsBanned => Status == UserStatus.Banned;

    public TimeSpan Age(DateTime utcNow) => utcNow - CreatedAt;

    public string LevelTitle
    {
        get
        {
            var title = Level switch
            {
                UserLevel.Trusted => "Trusted",
                UserLevel.Moderator => "Moderator",
                UserLevel.Administrator => "Administrator",
                _ => "User",
            };
            return IsBanned ? "BANNED " + title : title;
        }
    }

    /// <summary>Bootstrap text colour suffix (used as <c>text-{LevelColor}</c>).</summary>
    public string LevelColor
    {
        get
        {
            var color = Level switch
            {
                UserLevel.Trusted => "success",
                >= UserLevel.Moderator => "purple",
                _ => "default",
            };
            return IsBanned ? color + " strike" : color;
        }
    }
}
