using Mogu.Core.Entities;

namespace Mogu.Core.Data;

/// <summary>Mogu's fixed category tree. Changing it requires a new migration.</summary>
public static class CategorySeed
{
    public static readonly MainCategory[] MainCategories =
    [
        new() { Id = 1, Name = "Unarchived" },
        new() { Id = 2, Name = "Paid" },
    ];

    public static readonly SubCategory[] SubCategories =
    [
        new() { MainCategoryId = 1, Id = 1, Name = "Karaoke" },
        new() { MainCategoryId = 1, Id = 2, Name = "Copyright" },
        new() { MainCategoryId = 1, Id = 3, Name = "Yab" },
        new() { MainCategoryId = 2, Id = 1, Name = "Membership" },
        new() { MainCategoryId = 2, Id = 2, Name = "Voice pack" },
        new() { MainCategoryId = 2, Id = 3, Name = "Concert" },
        new() { MainCategoryId = 2, Id = 4, Name = "Songs" },
        new() { MainCategoryId = 2, Id = 5, Name = "Others" },
    ];
}
