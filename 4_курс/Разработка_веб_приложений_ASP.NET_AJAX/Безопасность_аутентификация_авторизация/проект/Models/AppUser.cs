using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace SecureTodo.Models;

public class AppUser : IdentityUser
{
    [Required, StringLength(100)]
    public string FullName { get; set; } = string.Empty;

    public ICollection<TaskItem> Tasks { get; set; } = [];
    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
}
