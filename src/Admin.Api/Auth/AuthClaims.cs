using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;

namespace ScalaAPI.Admin.Auth;

public static class AuthClaims
{
    public static bool TryGetUserId(ClaimsPrincipal principal, out long userId)
    {
        var value = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst("sub")?.Value
            ?? principal.Claims.FirstOrDefault(claim =>
                claim.Type.EndsWith("/nameidentifier", StringComparison.OrdinalIgnoreCase)
                || claim.Type.Equals("nameidentifier", StringComparison.OrdinalIgnoreCase))?.Value;
        return long.TryParse(value, out userId);
    }

    public static string? SessionId(ClaimsPrincipal principal) =>
        principal.FindFirst("sid")?.Value;
}
