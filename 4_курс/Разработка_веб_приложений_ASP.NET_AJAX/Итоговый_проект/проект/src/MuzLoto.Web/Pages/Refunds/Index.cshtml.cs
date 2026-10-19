using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Pages.Refunds;

[Authorize(Roles = "Admin")]
public sealed class IndexModel(AppDbContext db, BookingWorkflow workflow) : PageModel
{
    public List<Refund> Pending { get; private set; } = new();
    public List<Refund> Completed { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        var refunds = db.Refunds.Include(x => x.Payment).ThenInclude(x => x.Booking)
            .ThenInclude(x => x.GameSession).ThenInclude(x => x.Game).AsNoTracking();
        Pending = await refunds.Where(x => x.Status == RefundStatus.Pending)
            .OrderBy(x => x.RequestedAt).Take(100).ToListAsync(ct);
        Completed = await refunds.Where(x => x.Status == RefundStatus.Completed)
            .OrderByDescending(x => x.CompletedAt).Take(100).ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostCompleteAsync(Guid id, bool confirmTransfer,
        string? transferReference, CancellationToken ct)
    {
        if (!confirmTransfer)
            return AjaxResult.Failure(this, new BusinessException(
                "Подтвердите, что деньги действительно перечислены клиенту.", 400));
        try
        {
            await workflow.ConfirmRefundAsync(id,
                User.FindFirstValue(ClaimTypes.NameIdentifier)!, transferReference, ct);
            return AjaxResult.Success(this, "Денежный возврат зафиксирован. Чистые поступления обновлены.");
        }
        catch (BusinessException ex) { return AjaxResult.Failure(this, ex); }
    }
}
