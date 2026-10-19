using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MuzLoto.Web.Services;

public static class AjaxResult
{
    public static bool IsAjax(PageModel page) =>
        page.Request.Headers["X-Requested-With"] == "XMLHttpRequest";

    public static IActionResult Success(PageModel page, string message)
    {
        if (IsAjax(page)) return new JsonResult(new { ok = true, message });
        page.TempData["Notice"] = message;
        return page.RedirectToPage();
    }

    public static IActionResult Failure(PageModel page, BusinessException e)
    {
        if (IsAjax(page)) return new ObjectResult(new ProblemDetails
        {
            Status = e.StatusCode, Title = "Операция не выполнена", Detail = e.Message
        }) { StatusCode = e.StatusCode, ContentTypes = { "application/problem+json" } };
        page.TempData["Error"] = e.Message;
        return page.RedirectToPage();
    }
}
