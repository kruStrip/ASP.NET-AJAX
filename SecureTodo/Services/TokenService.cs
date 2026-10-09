using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using SecureTodo.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace SecureTodo.Services;

public sealed record TokenPair(string AccessToken, string RefreshToken, DateTime AccessTokenExpiresAt);

public sealed class TokenService(IConfiguration configuration, UserManager<AppUser> userManager)
{
    public async Task<TokenPair> CreateTokenPairAsync(AppUser user)
    {
        var now = DateTime.UtcNow;
        var expiresAt = now.AddMinutes(15);
        var roles = await userManager.GetRolesAsync(user);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(ClaimTypes.NameIdentifier, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new(ClaimTypes.Name, user.FullName)
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var jwt = configuration.GetSection("Jwt");
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"]!)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: jwt["Issuer"], audience: jwt["Audience"], claims: claims,
            notBefore: now, expires: expiresAt, signingCredentials: credentials);

        return new TokenPair(
            new JwtSecurityTokenHandler().WriteToken(token),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)),
            expiresAt);
    }

    public static string HashRefreshToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
