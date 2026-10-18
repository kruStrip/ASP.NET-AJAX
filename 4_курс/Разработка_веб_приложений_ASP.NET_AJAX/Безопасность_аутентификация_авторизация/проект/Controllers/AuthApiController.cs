using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SecureTodo.Data;
using SecureTodo.DTOs;
using SecureTodo.Models;
using SecureTodo.Services;
using System.Security.Claims;

namespace SecureTodo.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthApiController(
    UserManager<AppUser> userManager,
    SignInManager<AppUser> signInManager,
    ApplicationDbContext db,
    TokenService tokenService) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<TokenResponse>> Login(ApiLoginRequest request)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null) return Unauthorized();
        var result = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded) return Unauthorized(new { error = result.IsLockedOut ? "Account is locked." : "Invalid credentials." });
        if (await userManager.GetTwoFactorEnabledAsync(user))
            return Unauthorized(new { error = "Use the interactive login to complete two-factor authentication." });
        return Ok(await IssueAndStoreAsync(user));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<TokenResponse>> Refresh(RefreshRequest request)
    {
        var hash = TokenService.HashRefreshToken(request.RefreshToken);
        var stored = await db.RefreshTokens.Include(t => t.User).SingleOrDefaultAsync(t => t.Token == hash);
        if (stored is null || !stored.IsActive || stored.User is null) return Unauthorized();
        stored.RevokedAt = DateTime.UtcNow;
        var response = await IssueAndStoreAsync(stored.User);
        await db.SaveChangesAsync();
        return Ok(response);
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(RefreshRequest request)
    {
        var hash = TokenService.HashRefreshToken(request.RefreshToken);
        var stored = await db.RefreshTokens.SingleOrDefaultAsync(t => t.Token == hash);
        if (stored is null || !stored.IsActive) return Unauthorized();
        stored.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("logout-all")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<IActionResult> LogoutAll()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        var active = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync();
        foreach (var token in active) token.RevokedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return NoContent();
    }

    private async Task<TokenResponse> IssueAndStoreAsync(AppUser user)
    {
        var pair = await tokenService.CreateTokenPairAsync(user);
        db.RefreshTokens.Add(new RefreshToken
        {
            Token = TokenService.HashRefreshToken(pair.RefreshToken),
            UserId = user.Id,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(7)
        });
        await db.SaveChangesAsync();
        return new TokenResponse(pair.AccessToken, pair.RefreshToken, pair.AccessTokenExpiresAt);
    }
}
