using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Mogu.Web.Pages;

/// <summary>Handles unhandled exceptions, re-executed status codes and login failures.</summary>
[IgnoreAntiforgeryToken]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class ErrorModel(ILogger<ErrorModel> logger) : MoguPageModel
{
    public bool IsNotFound { get; private set; }

    public string Message { get; private set; } = "";

    public string? RequestId { get; private set; }

    public void OnGet(string? code, string? message) => Handle(code, message);

    public void OnPost(string? code) => Handle(code, null);

    private void Handle(string? code, string? message)
    {
        if (code == "login")
        {
            Message = message ?? "Login failed.";
            return;
        }

        var status = int.TryParse(code, out var parsed) ? parsed : StatusCodes.Status500InternalServerError;
        Response.StatusCode = status;
        IsNotFound = status == StatusCodes.Status404NotFound;
        Message = status switch
        {
            StatusCodes.Status400BadRequest => "Bad request.",
            StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden => "You are not allowed to do that.",
            _ => "Something went wrong. Debug information has been logged.",
        };

        if (status >= 500)
        {
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
            if (HttpContext.Features.Get<IExceptionHandlerPathFeature>() is { } feature)
            {
                logger.LogError(feature.Error, "Unhandled exception on {Path}, request {RequestId}", feature.Path, RequestId);
            }
        }
    }
}
