namespace TaskFlowApi.Exceptions;

/// <summary>Исключение, означающее нарушение бизнес-правила.</summary>
public class BusinessRuleException : Exception
{
    public BusinessRuleException(string message) : base(message)
    {
    }
}
