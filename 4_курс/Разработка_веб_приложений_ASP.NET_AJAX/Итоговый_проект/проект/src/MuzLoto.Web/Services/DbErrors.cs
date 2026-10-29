using Npgsql;

namespace MuzLoto.Web.Services;

/// <summary>PostgreSQL errors can arrive wrapped (EF Core 8 wraps transient ones in InvalidOperationException): look through the chain.</summary>
public static class DbErrors
{
    public static bool IsSerializationFailure(Exception ex) => Has(ex, PostgresErrorCodes.SerializationFailure);
    public static bool IsUniqueViolation(Exception ex) => Has(ex, PostgresErrorCodes.UniqueViolation);

    private static bool Has(Exception? ex, string sqlState)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is PostgresException pg && pg.SqlState == sqlState) return true;
        return false;
    }
}

/// <summary>A SERIALIZABLE transaction lost a race; the operation may be retried against fresh data.</summary>
public sealed class ConcurrencyConflictException(string message) : BusinessException(message, 409);
