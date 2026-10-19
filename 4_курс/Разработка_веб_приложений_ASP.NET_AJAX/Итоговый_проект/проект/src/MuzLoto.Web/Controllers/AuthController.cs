using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    UserManager<AppUser> users, SignInManager<AppUser> signIn, JwtTokenService tokens) : ControllerBase
{
    public sealed record LoginRequest(
        [Required, EmailAddress] string Email,
        [Required] string Password);

    [AllowAnonymous]
    [HttpPost("token")]
    [ProducesResponseType(typeof(TokenResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<TokenResponse>> Token(LoginRequest request)
    {
        var user = await users.FindByEmailAsync(request.Email);
        if (user is null) return Unauthorized(new ProblemDetails { Status = 401, Title = "Неверные учётные данные" });
        // This validates the password, lockout and account state without setting a cookie.
        var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded) return Unauthorized(new ProblemDetails { Status = 401, Title = "Неверные учётные данные или вход заблокирован" });
        if (!await users.IsInRoleAsync(user, "Admin") && !await users.IsInRoleAsync(user, "Manager"))
            return Forbid();
        var (accessToken, expiresAt) = await tokens.IssueAsync(user);
        return Ok(new TokenResponse(accessToken, "Bearer", expiresAt));
    }

    public sealed record TokenResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt);
}
