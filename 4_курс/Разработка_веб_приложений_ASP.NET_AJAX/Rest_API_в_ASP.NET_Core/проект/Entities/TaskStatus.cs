namespace TaskFlowApi.Entities;

/// <summary>
/// Статус задачи.
/// </summary>
public enum TaskItemStatus
{
    /// <summary>Задача ещё не начата.</summary>
    ToDo,

    /// <summary>Задача в работе.</summary>
    InProgress,

    /// <summary>Задача завершена.</summary>
    Done
}
