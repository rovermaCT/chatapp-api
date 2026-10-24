using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ChatApp.Api.Extensions;

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal principal) =>
        Guid.Parse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
            ?? throw new InvalidOperationException("Authenticated principal has no 'sub' claim."));
}
