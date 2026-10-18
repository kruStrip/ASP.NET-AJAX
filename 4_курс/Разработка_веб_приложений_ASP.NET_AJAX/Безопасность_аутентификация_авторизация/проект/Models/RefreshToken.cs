using System.ComponentModel.DataAnnotations;

namespace SecureTodo.Models;

public class RefreshToken
{
    public int Id { get; set; }

    // SHA-256 hash of the token; the raw secret is returned only to the client.
    [Required, StringLength(64)]
    public string Token { get; set; } = string.Empty;

    [Required]
    public string UserId { get; set; } = string.Empty;
    public AppUser? User { get; set; }

    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTime.UtcNow;
}
