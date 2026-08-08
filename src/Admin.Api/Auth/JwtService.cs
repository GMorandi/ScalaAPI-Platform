using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace ScalaAPI.Admin.Auth;

public class JwtService(IConfiguration config)
{
    private readonly string _key = GetKey(config);
    private readonly string _issuer = config["Jwt:Issuer"]
        ?? throw new InvalidOperationException("Jwt:Issuer is required");
    private readonly int _expiryMinutes = int.Parse(config["Jwt:ExpiryMinutes"] ?? "1440");

    public TimeSpan AccessTokenLifetime => TimeSpan.FromMinutes(_expiryMinutes);

    public string GenerateToken(string username, string role = "user", long? subjectId = null,
        string? sessionId = null)
    {
        var handler = new JwtSecurityTokenHandler();
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username),
            new(ClaimTypes.Role, string.IsNullOrWhiteSpace(role) ? "user" : role),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        if (subjectId.HasValue)
            claims.Add(new Claim(JwtRegisteredClaimNames.Sub, subjectId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (!string.IsNullOrWhiteSpace(sessionId))
            claims.Add(new Claim("sid", sessionId));
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(_expiryMinutes),
            Issuer = _issuer,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_key)),
                SecurityAlgorithms.HmacSha256Signature)
        };
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    public TokenValidationParameters GetValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = _issuer,
        ValidateAudience = false,
        ValidateLifetime = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_key))
    };

    private static string GetKey(IConfiguration config)
    {
        var key = config["Jwt:Key"]
            ?? throw new InvalidOperationException("Jwt:Key is required");
        if (Encoding.UTF8.GetByteCount(key) < 32)
            throw new InvalidOperationException("Jwt:Key must be at least 32 bytes");
        return key;
    }
}
