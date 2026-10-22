using Microsoft.AspNetCore.Mvc;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Web.Api;
using Mogu.Web.Infrastructure;

namespace Mogu.Web.Pages.Account;

public sealed class ProfileModel(MoguDbContext db, ApiTokenService tokens) : MoguPageModel
{
    [BindProperty(Name = "hide_comments")]
    public bool HideComments { get; set; }

    /// <summary>A freshly generated token; only ever shown in the response to its generation.</summary>
    public string? NewApiToken { get; private set; }

    /// <summary>Which tab to show: <c>preferences</c> or <c>api</c>.</summary>
    public string ActiveTab { get; private set; } = "preferences";

    public User ProfileUser => CurrentUser.User!;

    public IActionResult OnGet()
    {
        if (!CurrentUser.IsAuthenticated)
        {
            return Redirect("/");
        }

        ActiveTab = Request.Query["tab"] == "api" ? "api" : "preferences";
        return Page();
    }

    public async Task<IActionResult> OnPostSettingsAsync(CancellationToken cancellationToken)
    {
        if (CurrentUser.User is not { } user)
        {
            return Redirect("/");
        }

        user.HideComments = HideComments;
        await db.SaveChangesAsync(cancellationToken);
        Flash(FlashLevel.Success, "<strong>Preferences successfully changed!</strong>", isHtml: true);
        return Redirect("/profile");
    }

    public async Task<IActionResult> OnPostGenerateTokenAsync(CancellationToken cancellationToken)
    {
        if (CurrentUser.User is not { } user)
        {
            return Redirect("/");
        }

        // Rendered directly rather than redirected, so the token never lands in TempData or a URL.
        NewApiToken = await tokens.GenerateAsync(user, cancellationToken);
        ActiveTab = "api";
        Response.Headers.CacheControl = "no-store";
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeTokenAsync(CancellationToken cancellationToken)
    {
        if (CurrentUser.User is not { } user)
        {
            return Redirect("/");
        }

        await tokens.RevokeAsync(user, cancellationToken);
        Flash(FlashLevel.Success, "API token revoked.");
        return Redirect("/profile?tab=api");
    }
}
