using System.Text.Json;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Mogu.Web.Infrastructure;

public enum FlashLevel
{
    Success,
    Info,
    Warning,
    Danger,
}

/// <summary>A one-shot message shown at the top of the next rendered page (Bootstrap alert).</summary>
/// <param name="IsHtml">Only set for trusted, server-authored markup.</param>
public sealed record FlashMessage(FlashLevel Level, string Text, bool IsHtml = false)
{
    public string CssClass => Level.ToString().ToLowerInvariant();

    public IHtmlContent Render() => IsHtml ? new HtmlString(Text) : new StringHtmlContent(Text);
}

public static class FlashExtensions
{
    private const string Key = "Mogu.Flashes";

    public static void AddFlash(this ITempDataDictionary tempData, FlashLevel level, string text, bool isHtml = false)
    {
        var messages = Peek(tempData);
        messages.Add(new FlashMessage(level, text, isHtml));
        tempData[Key] = JsonSerializer.Serialize(messages);
    }

    public static IReadOnlyList<FlashMessage> TakeFlashes(this ITempDataDictionary tempData) =>
        tempData[Key] is string json ? JsonSerializer.Deserialize<List<FlashMessage>>(json) ?? [] : [];

    private static List<FlashMessage> Peek(ITempDataDictionary tempData) =>
        tempData.Peek(Key) is string json ? JsonSerializer.Deserialize<List<FlashMessage>>(json) ?? [] : [];
}
