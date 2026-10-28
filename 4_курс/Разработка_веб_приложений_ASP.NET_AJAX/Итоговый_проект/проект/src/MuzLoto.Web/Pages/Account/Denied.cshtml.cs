using Microsoft.AspNetCore.Mvc.RazorPages;
namespace MuzLoto.Web.Pages.Account;
public class DeniedModel : PageModel
{
    // Authenticated but lacking rights: the final response is 403, not a 200 page.
    public void OnGet() => Response.StatusCode = StatusCodes.Status403Forbidden;
}
