using System.ComponentModel.DataAnnotations;

namespace TaskFlowApi.Entities;

/// <summary>
/// Пользователь системы.
/// </summary>
public class AppUser
{
    /// <summary>Идентификатор пользователя.</summary>
    public int Id { get; set; }

    /// <summary>Имя пользователя.</summary>
    [Required]
    [StringLength(50)]
    public string Username { get; set; } = string.Empty;

    /// <summary>Электронная почта пользователя.</summary>
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    /// <summary>Хэш пароля пользователя (никогда не возвращается через API).</summary>
    public string PasswordHash { get; set; } = string.Empty;
}
