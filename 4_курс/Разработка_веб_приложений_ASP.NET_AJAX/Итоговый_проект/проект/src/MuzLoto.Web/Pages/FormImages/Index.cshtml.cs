using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages.FormImages;

/// <summary>Ten fixed public image links (/i/1 … /i/10) for the FormDesigner form: upload, replace, label, remove.</summary>
[Authorize(Roles = "Admin")]
public class IndexModel(AppDbContext db, CoverImages images) : PageModel
{
    public sealed record Row(int Number, string PublicUrl, ImageSlot? Slot);
    public List<Row> Rows { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        var slots = await db.ImageSlots.AsNoTracking().ToDictionaryAsync(x => x.Number, ct);
        var origin = $"{Request.Scheme}://{Request.Host}";
        Rows = Enumerable.Range(1, ImageSlot.Count)
            .Select(n => new Row(n, $"{origin}/i/{n}", slots.GetValueOrDefault(n))).ToList();
    }

    public async Task<IActionResult> OnPostUploadAsync(int number, IFormFile? image, string? note, CancellationToken ct)
    {
        if (image is null) return Done("Выберите файл картинки.", error: true);
        try { await images.SaveSlotAsync(number, image, note, ct); }
        catch (BusinessException ex) { return Done(ex.Message, error: true); }
        return Done($"Картинка для ссылки №{number} сохранена. Ссылка не изменилась.");
    }

    public async Task<IActionResult> OnPostNoteAsync(int number, string? note, CancellationToken ct)
    {
        try { await images.UpdateSlotNoteAsync(number, note, ct); }
        catch (BusinessException ex) { return Done(ex.Message, error: true); }
        return Done($"Подпись ссылки №{number} сохранена.");
    }

    public async Task<IActionResult> OnPostDeleteAsync(int number, CancellationToken ct)
    {
        await images.DeleteSlotAsync(number, ct);
        return Done($"Картинка ссылки №{number} удалена: по ссылке теперь «не найдено».");
    }

    private IActionResult Done(string message, bool error = false)
    {
        TempData[error ? "Error" : "Notice"] = message;
        return RedirectToPage();
    }
}
