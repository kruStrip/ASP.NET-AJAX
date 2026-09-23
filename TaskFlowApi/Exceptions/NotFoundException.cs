namespace TaskFlowApi.Exceptions;

/// <summary>Исключение, означающее, что запрошенный ресурс не найден.</summary>
public class NotFoundException : Exception
{
    public NotFoundException(string message) : base(message)
    {
    }
}
