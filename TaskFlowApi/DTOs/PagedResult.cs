namespace TaskFlowApi.DTOs;

/// <summary>Постраничный результат выборки.</summary>
/// <typeparam name="T">Тип элементов страницы.</typeparam>
public class PagedResult<T>
{
    /// <summary>Элементы текущей страницы.</summary>
    public List<T> Items { get; set; } = new();

    /// <summary>Номер текущей страницы.</summary>
    public int Page { get; set; }

    /// <summary>Размер страницы.</summary>
    public int PageSize { get; set; }

    /// <summary>Общее количество элементов.</summary>
    public int TotalCount { get; set; }

    /// <summary>Общее количество страниц.</summary>
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
