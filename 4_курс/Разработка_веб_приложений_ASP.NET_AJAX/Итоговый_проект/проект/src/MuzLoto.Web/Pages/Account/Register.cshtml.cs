using System.ComponentModel.DataAnnotations;
using System.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Pages.Account;

/// <summary>Self-registration always grants the base "User" role; staff roles are assigned only by an admin.</summary>
public class RegisterModel(
    UserManager<AppUser> users, SignInManager<AppUser> signIn, AppDbContext db) : PageModel
{
    public const string DefaultRole = "User";

    [BindProperty] public RegisterInput Input { get; set; } = new();
    public sealed class RegisterInput
    {
        [Required(ErrorMessage = "Укажите имя."), StringLength(150, MinimumLength = 2, ErrorMessage = "Имя: от 2 до 150 символов.")]
        public string FullName { get; set; } = string.Empty;
        [Required(ErrorMessage = "Укажите e-mail."), EmailAddress(ErrorMessage = "Некорректный e-mail."), StringLength(254)]
        public string Email { get; set; } = string.Empty;
        [Required(ErrorMessage = "Укажите пароль."), MinLength(6, ErrorMessage = "Пароль: не менее 6 символов."), DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
        [Required(ErrorMessage = "Повторите пароль."), Compare(nameof(Password), ErrorMessage = "Пароли не совпадают."), DataType(DataType.Password)]
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? RedirectToPage("/Home") : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToPage("/Home");
        if (!ModelState.IsValid) return Page();
        var email = Input.Email.Trim();
        if (await users.FindByEmailAsync(email) is not null)
        {
            ModelState.AddModelError("Input.Email", "Пользователь с таким e-mail уже зарегистрирован.");
            return Page();
        }

        var user = new AppUser { UserName = email, Email = email, FullName = Input.FullName.Trim() };
        await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            var created = await users.CreateAsync(user, Input.Password);
            if (!created.Succeeded)
            {
                foreach (var error in created.Errors) ModelState.AddModelError(string.Empty, Translate(error));
                return Page();
            }
            var role = await users.AddToRoleAsync(user, DefaultRole);
            if (!role.Succeeded) throw new InvalidOperationException("Role assignment failed: " +
                string.Join("; ", role.Errors.Select(x => x.Description)));
            await tx.CommitAsync(ct);
        }
        await signIn.SignInAsync(user, isPersistent: false);
        TempData["Notice"] = "Регистрация завершена. Добро пожаловать!";
        return RedirectToPage("/Home");
    }

    private static string Translate(IdentityError error) => error.Code switch
    {
        nameof(IdentityErrorDescriber.PasswordRequiresDigit) => "Пароль должен содержать хотя бы одну цифру.",
        nameof(IdentityErrorDescriber.PasswordTooShort) => "Пароль слишком короткий.",
        nameof(IdentityErrorDescriber.DuplicateEmail) or nameof(IdentityErrorDescriber.DuplicateUserName)
            => "Пользователь с таким e-mail уже зарегистрирован.",
        nameof(IdentityErrorDescriber.InvalidEmail) => "Некорректный e-mail.",
        _ => error.Description
    };
}
