using System.ComponentModel.DataAnnotations;

namespace SecureTodo.DTOs;

public sealed class ApiLoginRequest
{
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    [Required] public string Password { get; set; } = string.Empty;
}

public sealed class RefreshRequest
{
    [Required] public string RefreshToken { get; set; } = string.Empty;
}
public sealed record TokenResponse(string AccessToken, string RefreshToken, DateTime AccessTokenExpiresAt);
