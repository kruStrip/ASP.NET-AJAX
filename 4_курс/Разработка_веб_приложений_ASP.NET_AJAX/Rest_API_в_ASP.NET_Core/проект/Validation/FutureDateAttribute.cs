using System.ComponentModel.DataAnnotations;

namespace TaskFlowApi.Validation;

/// <summary>
/// Проверяет, что дата (если указана) находится в будущем.
/// </summary>
public class FutureDateAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is null)
        {
            return ValidationResult.Success;
        }

        if (value is DateTime date && date <= DateTime.UtcNow)
        {
            return new ValidationResult(ErrorMessage ?? "Дата должна быть в будущем.");
        }

        return ValidationResult.Success;
    }
}
