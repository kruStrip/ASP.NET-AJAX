using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Core.Infrastructure;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace MuzLoto.Web.Services;

/// <summary>
/// A stale anti-forgery token (signed in or out in another tab, an old page) on an ordinary page form: instead of a
/// bare "400 Bad Request" the page opens again with a message. Nothing is executed. AJAX and API keep their 400.
/// </summary>
public sealed class AntiforgeryFailureFilter(ITempDataDictionaryFactory tempData) : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not IAntiforgeryValidationFailedResult) return;
        var request = context.HttpContext.Request;
        if (request.Headers["X-Requested-With"] == "XMLHttpRequest" || request.Path.StartsWithSegments("/api")) return;
        var data = tempData.GetTempData(context.HttpContext);
        data["Error"] = "Страница устарела — например, вы вошли или вышли из аккаунта в другой вкладке. Повторите действие.";
        data.Save();   // the request was stopped before the page, so the usual TempData save does not run
        context.Result = new RedirectResult(request.PathBase + request.Path);
    }

    public void OnResultExecuted(ResultExecutedContext context) { }
}
