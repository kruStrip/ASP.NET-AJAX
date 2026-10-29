using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Controllers;

[ApiController]
[IgnoreAntiforgeryToken]
[Route("api/auth")]
public sealed class AuthController(
    UserManager<AppUser> users, SignInManager<AppUser> signIn, JwtTokenService tokens) : ControllerBase
{
    public sealed record LoginRequest(
        [Required, EmailAddress] string Email,
        [Required] string Password);

    /// <summary>Проверяет e-mail и пароль и выдаёт JWT (claims: id, e-mail, роли; срок — 30 минут).</summary>
    /// <remarks>Доступен всем ролям. Права на конкретные данные проверяют сами эндпоинты.
    /// <c>/api/auth/token</c> — устаревший адрес, оставлен для совместимости.</remarks>
    [AllowAnonymous]
    [HttpPost("login")]
    [HttpPost("token")]
    [ProducesResponseType(typeof(TokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TokenResponse>> Login(LoginRequest request)
    {
        var user = await users.FindByEmailAsync(request.Email);
        if (user is null) return Unauthorized(new ProblemDetails { Status = 401, Title = "Неверные учётные данные" });
        // This validates the password, lockout and account state without setting a cookie.
        var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded) return Unauthorized(new ProblemDetails { Status = 401, Title = "Неверные учётные данные или вход заблокирован" });
        var (accessToken, expiresAt) = await tokens.IssueAsync(user);
        return Ok(new TokenResponse(accessToken, "Bearer", expiresAt));
    }

    public sealed record TokenResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt);
}
