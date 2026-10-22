using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Mogu.Core.Data;
using Mogu.Core.Entities;

namespace Mogu.Web.Api;

/// <summary>
/// Per-user API tokens. Only the SHA-256 of a token is stored (<see cref="User.ApiTokenHash"/>);
/// the token itself is shown once when generated. Clients authenticate with
/// <c>Authorization: Bearer &lt;token&gt;</c> or HTTP Basic <c>username:token</c> (what the old upload script sends).
/// </summary>
public sealed class ApiTokenService(MoguDbContext db)
{
    public const string TokenPrefix = "mogu_";

    public static string NewToken() =>
        TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>Generates a new token for <paramref name="user"/>, replacing any previous one, and returns it.</summary>
    public async Task<string> GenerateAsync(User user, CancellationToken cancellationToken)
    {
        var token = NewToken();
        user.ApiTokenHash = Hash(token);
        await db.SaveChangesAsync(cancellationToken);
        return token;
    }

    public async Task RevokeAsync(User user, CancellationToken cancellationToken)
    {
        user.ApiTokenHash = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The active user the request's <c>Authorization</c> header authenticates, if any.</summary>
    public async Task<User?> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var header)
            || string.IsNullOrEmpty(header.Parameter))
        {
            return null;
        }

        string? username = null;
        string token;
        if (header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            token = header.Parameter;
        }
        else if (header.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
        {
            string credentials;
            try
            {
                credentials = Encoding.UTF8.GetString(Convert.FromBase64String(header.Parameter));
            }
            catch (FormatException)
            {
                return null;
            }

            var separator = credentials.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0)
            {
                return null;
            }

            username = credentials[..separator];
            token = credentials[(separator + 1)..];
        }
        else
        {
            return null;
        }

        if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var hash = Hash(token);
        var user = await db.Users.FirstOrDefaultAsync(u => u.ApiTokenHash == hash, cancellationToken);
        if (user is null || user.Status != UserStatus.Active
            || (username is not null && !string.Equals(username, user.Username, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return user;
    }
}
