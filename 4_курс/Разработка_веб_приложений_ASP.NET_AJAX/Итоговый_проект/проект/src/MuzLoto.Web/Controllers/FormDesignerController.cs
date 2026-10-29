using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Controllers;

/// <summary>Canonical JSON endpoint for a verified FormDesigner adapter.</summary>
/// <remarks>The real FormDesigner field mapping/transport must be confirmed before connection.</remarks>
[ApiController]
[IgnoreAntiforgeryToken]
[Route("api/integrations/formdesigner/submissions")]
[AllowAnonymous]
public sealed class FormDesignerController(BookingWorkflow workflow, IConfiguration config) : ControllerBase
{
    public sealed class IncomingDto
    {
        [Required, MaxLength(250)] public string ExternalSubmissionId { get; set; } = string.Empty;
        [Required] public Guid GameSessionId { get; set; }
        [Required, MaxLength(150)] public string TeamName { get; set; } = string.Empty;
        [Required, MaxLength(150)] public string CustomerName { get; set; } = string.Empty;
        [Required, MaxLength(40)] public string Phone { get; set; } = string.Empty;
        [MaxLength(100)] public string? Telegram { get; set; }
        [Range(1, 100000)] public int Quantity { get; set; }
    }

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] IncomingDto input, CancellationToken ct)
    {
        var secret = config["Integration:Secret"];
        if (string.IsNullOrWhiteSpace(secret)) return StatusCode(503, new { error = "Integration not configured" });
        var provided = Request.Headers["X-Integration-Key"].ToString();
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        if (!CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash)) return Unauthorized();
        try
        {
            var result = await workflow.ReceiveAsync(new IncomingBooking(
                input.ExternalSubmissionId, input.GameSessionId, input.TeamName,
                input.CustomerName, input.Phone, input.Telegram, input.Quantity), ct);
            return StatusCode(result.Duplicate ? 200 : 201, new { bookingId = result.Id, duplicate = result.Duplicate });
        }
        catch (BusinessException ex)
        {
            return Problem(statusCode: ex.StatusCode, detail: ex.Message, title: "Заявка отклонена");
        }
    }
}
