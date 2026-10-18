using System.Security.Claims;

namespace RockBot.UserProxy.Blazor.Auth;

/// <summary>Claim fix-ups for Entra ID sign-ins, so <see cref="UserAllowlist"/> has an address to match.</summary>
public static class EntraClaims
{
    /// <summary>
    /// Adds an email claim from <c>preferred_username</c> (the UPN) when the token carries none.
    /// Entra only emits <c>email</c> when the user has a mail attribute; the UPN is always present
    /// and, with the authority pinned to one tenant, only that tenant's admin can set it.
    /// </summary>
    public static void EnsureEmail(ClaimsIdentity identity)
    {
        if (identity.FindFirst(ClaimTypes.Email) is not null || identity.FindFirst("email") is not null)
            return;

        var upn = identity.FindFirst("preferred_username")?.Value;
        if (!string.IsNullOrWhiteSpace(upn) && upn.Contains('@'))
            identity.AddClaim(new Claim(ClaimTypes.Email, upn));
    }
}
