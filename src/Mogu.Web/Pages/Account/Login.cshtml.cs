using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Mogu.Web.Infrastructure;

namespace Mogu.Web.Pages.Account;

/// <summary>Starts the Discord OAuth2 flow.</summary>
public sealed class LoginModel : MoguPageModel
{
    public IActionResult OnGet(string? next) =>
        Challenge(
            new AuthenticationProperties { RedirectUri = Url.IsLocalUrl(next) ? next : "/" },
            DiscordAuthentication.Scheme);
}
