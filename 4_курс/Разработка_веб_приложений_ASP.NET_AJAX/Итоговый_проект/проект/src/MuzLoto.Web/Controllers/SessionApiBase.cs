using Microsoft.AspNetCore.Mvc;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Controllers;

/// <summary>Uniform RFC 7807 errors for both API contract versions.</summary>
public abstract class SessionApiBase : ControllerBase
{
    protected async Task<IActionResult> Safe(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (BusinessException e) { return Problem(statusCode: e.StatusCode, title: "Операция отклонена", detail: e.Message); }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException e) when (
            e.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.SerializationFailure })
        { return Problem(statusCode: 409, title: "Данные изменились", detail: "Повторите запрос."); }
        catch (Npgsql.PostgresException e) when (e.SqlState == Npgsql.PostgresErrorCodes.SerializationFailure)
        { return Problem(statusCode: 409, title: "Данные изменились", detail: "Повторите запрос."); }
    }
}
