namespace TaskFlowApi.Entities;

/// <summary>
/// Запись об уже обработанном идемпотентном запросе.
/// </summary>
public class IdempotencyRecord
{
    /// <summary>Ключ идемпотентности (значение заголовка X-Idempotency-Key).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Хэш тела запроса.</summary>
    public string RequestBodyHash { get; set; } = string.Empty;

    /// <summary>Сохранённое тело ответа (JSON).</summary>
    public string ResponseBody { get; set; } = string.Empty;

    /// <summary>Код статуса сохранённого ответа.</summary>
    public int StatusCode { get; set; }

    /// <summary>Заголовок Location сохранённого ответа (если был).</summary>
    public string? Location { get; set; }

    /// <summary>Дата создания записи.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
