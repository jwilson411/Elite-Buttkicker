using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace EDButtkicker.Hosting;

/// <summary>
/// The anti-forgery token for this process. It is generated once at startup from the system CSPRNG
/// and handed to the same-origin UI over a GET (cookies plus <c>/api/csrf</c>); every mutation has
/// to echo it back in the <see cref="HeaderName"/> header. A page on another origin cannot read the
/// GET response or the cookies, so it cannot forge a mutation even though the listener is reachable
/// from the browser it runs in.
/// </summary>
public sealed class CsrfTokenProvider
{
    /// <summary>The header a mutation must carry. A JSON body field would be forgeable by a form post.</summary>
    public const string HeaderName = "X-CSRF-Token";

    /// <summary>Server-side copy of the token: HttpOnly, so no script can read it.</summary>
    public const string CookieName = "EDBK-CSRF";

    /// <summary>The copy the same-origin page reads to build the header. Deliberately not HttpOnly.</summary>
    public const string ScriptCookieName = "EDBK-CSRF-JS";

    private readonly byte[] _tokenHash;

    public CsrfTokenProvider()
    {
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(Token));
    }

    /// <summary>The token as 64 lowercase hex characters (32 random bytes).</summary>
    public string Token { get; }

    /// <summary>Constant-time comparison, so a candidate cannot be recovered a character at a time.</summary>
    public bool Matches(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }

        // FixedTimeEquals throws when the two spans differ in length, and a candidate is attacker
        // controlled. Comparing SHA-256 digests keeps both sides 32 bytes, so any candidate - short,
        // long or empty - is answered by the same constant-time comparison rather than an exception.
        var candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));

        return CryptographicOperations.FixedTimeEquals(_tokenHash, candidateHash);
    }

    /// <summary>
    /// Puts both cookies on a safe (GET/HEAD/OPTIONS) response, so loading the UI is enough to arm
    /// its mutations. Skipped when the caller already holds the current token.
    /// </summary>
    public void IssueCookies(HttpContext context)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        if (context.Request.Cookies.TryGetValue(CookieName, out var existing) && Matches(existing) &&
            context.Request.Cookies.TryGetValue(ScriptCookieName, out var script) && Matches(script))
        {
            return;
        }

        var options = new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true
        };

        context.Response.Cookies.Append(CookieName, Token, options);
        context.Response.Cookies.Append(ScriptCookieName, Token, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true
        });
    }
}
