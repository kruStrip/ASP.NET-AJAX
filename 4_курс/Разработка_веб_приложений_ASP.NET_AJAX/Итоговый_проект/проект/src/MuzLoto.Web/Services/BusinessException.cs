namespace MuzLoto.Web.Services;

public sealed class BusinessException(string message, int statusCode = 409) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
