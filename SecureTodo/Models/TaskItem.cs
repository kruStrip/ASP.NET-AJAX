using System.ComponentModel.DataAnnotations;

namespace SecureTodo.Models;

public class TaskItem
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Введите заголовок."), StringLength(200)]
    [Display(Name = "Заголовок")]
    public string Title { get; set; } = string.Empty;

    [StringLength(2000)]
    [Display(Name = "Описание")]
    public string? Description { get; set; }

    [Display(Name = "Создана")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Display(Name = "Выполнена")]
    public bool IsCompleted { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;
    public AppUser? User { get; set; }
}
