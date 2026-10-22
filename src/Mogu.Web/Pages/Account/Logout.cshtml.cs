using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace Mogu.Web.Pages.Account;

public sealed class LogoutModel : MoguPageModel
{
    public async Task<IActionResult> OnGetAsync(string? next)
    {
        await HttpContext.SignOutAsync();
        return LocalRedirect(RedirectTarget(next));
    }

    /// <summary>Same rules as the old <c>redirect_url()</c>: a local <c>next</c>, else a same-host referrer, else home.</summary>
    private string RedirectTarget(string? next)
    {
        if (Url.IsLocalUrl(next))
        {
            return next!;
        }

        if (Uri.TryCreate(Request.Headers.Referer.ToString(), UriKind.Absolute, out var referer)
            && referer.Authority == Request.Host.Value
            && referer.PathAndQuery != "/logout")
        {
            return referer.PathAndQuery;
        }

        return "/";
    }
}
