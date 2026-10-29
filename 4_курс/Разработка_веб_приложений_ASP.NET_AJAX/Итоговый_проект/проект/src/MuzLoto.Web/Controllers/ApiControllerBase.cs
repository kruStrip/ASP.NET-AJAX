using Microsoft.AspNetCore.Mvc;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Controllers;

/// <summary>Uniform RFC 7807 errors for every business API controller.</summary>
public abstract class ApiControllerBase : ControllerBase
{
    protected async Task<IActionResult> Safe(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (BusinessException e) { return Problem(statusCode: e.StatusCode, title: "Операция отклонена", detail: e.Message); }
        catch (Exception e) when (DbErrors.IsSerializationFailure(e))
        { return Problem(statusCode: 409, title: "Данные изменились", detail: "Повторите запрос."); }
    }
}
