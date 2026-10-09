using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SecureTodo.Pages.Author;

[Authorize(Policy = "AuthorOnly")]
public class IndexModel : PageModel;
