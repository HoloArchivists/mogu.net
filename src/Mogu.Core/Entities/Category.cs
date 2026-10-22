namespace Mogu.Core.Entities;

public class MainCategory
{
    public int Id { get; set; }

    public required string Name { get; set; }

    public List<SubCategory> SubCategories { get; set; } = [];

    /// <summary>The <c>main_sub</c> identifier used in URLs, e.g. <c>1_0</c>.</summary>
    public string Code => $"{Id}_0";
}

/// <summary>Sub categories are numbered per main category, so the key is (MainCategoryId, Id).</summary>
public class SubCategory
{
    public int MainCategoryId { get; set; }

    public int Id { get; set; }

    public required string Name { get; set; }

    public MainCategory MainCategory { get; set; } = null!;

    public string Code => $"{MainCategoryId}_{Id}";
}
