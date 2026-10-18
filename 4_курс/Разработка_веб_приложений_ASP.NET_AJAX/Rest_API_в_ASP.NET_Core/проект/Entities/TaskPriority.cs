namespace TaskFlowApi.Entities;

/// <summary>
/// Приоритет задачи (используется в API v2).
/// </summary>
public enum TaskPriority
{
    /// <summary>Низкий приоритет.</summary>
    Low,

    /// <summary>Средний приоритет.</summary>
    Medium,

    /// <summary>Высокий приоритет.</summary>
    High
}
