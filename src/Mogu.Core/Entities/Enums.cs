namespace Mogu.Core.Entities;

public enum UserLevel
{
    Regular = 0,
    Trusted = 1,
    Moderator = 2,
    Administrator = 3,
}

public enum UserStatus
{
    Inactive = 0,
    Active = 1,
    Banned = 2,
}

public enum ReportStatus
{
    InReview = 0,
    Valid = 1,
    Invalid = 2,
}
