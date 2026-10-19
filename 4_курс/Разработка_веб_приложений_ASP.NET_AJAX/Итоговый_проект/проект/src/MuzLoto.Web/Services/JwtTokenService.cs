using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Services;

/// <summary>JWT credentials are read from environment / user secrets, never committed.</summary>
public sealed class JwtTokenService(IConfiguration config, UserManager<AppUser> users)
{
    public static byte[] RequireSigningKey(IConfiguration config)
    {
        var key = config["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
            throw new InvalidOperationException("Jwt:Key must contain at least 32 UTF-8 bytes (set Jwt__Key in .env).");
        return Encoding.UTF8.GetBytes(key);
    }

    public async Task<(string AccessToken, DateTimeOffset ExpiresAt)> IssueAsync(AppUser user)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(30);
        var issuer = config["Jwt:Issuer"] ?? "MuzLoto";
        var audience = config["Jwt:Audience"] ?? "MuzLoto.Api";
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.UserName ?? user.Id),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        foreach (var role in await users.GetRolesAsync(user)) claims.Add(new Claim(ClaimTypes.Role, role));
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(RequireSigningKey(config)), SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(issuer, audience, claims, now.UtcDateTime,
            expires.UtcDateTime, credentials);
        return (new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }
}
