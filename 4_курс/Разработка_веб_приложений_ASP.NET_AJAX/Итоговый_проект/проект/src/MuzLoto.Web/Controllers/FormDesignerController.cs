using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MuzLoto.Web.Services;

namespace MuzLoto.Web.Controllers;

/// <summary>
/// FormDesigner integration. <c>webhook</c> accepts the form's own webhook (flat parameters, GET or POST, form or JSON);
/// <c>submissions</c> is the canonical JSON contract for a trusted adapter that already knows our session IDs.
/// Both are protected by Integration:Secret instead of user JWT.
/// </summary>
[ApiController]
[IgnoreAntiforgeryToken]
[Route("api/integrations/formdesigner")]
[AllowAnonymous]
public sealed class FormDesignerController(BookingWorkflow workflow, IConfiguration config, FormDesignerWebhook webhook) : ControllerBase
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

    /// <summary>
    /// Вебхук FormDesigner. Параметры называются в настройках вебхука: game_date, game_time, game_name, quantity,
    /// name, phone, telegram, team, total, submission_id; секрет — произвольный параметр <c>key</c> (или заголовок X-Integration-Key).
    /// 201 — принята, 200 — повторная доставка, 400 — не хватает данных, 401 — неверный ключ, 404 — нет игры на дату, 409 — продажи закрыты.
    /// </summary>
    [HttpGet("webhook")]
    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config["Integration:Secret"])) return StatusCode(503, new { error = "Integration not configured" });
        var fields = await ReadFlatParametersAsync(ct);
        if (fields is null) return Problem(statusCode: 400, title: "Заявка отклонена", detail: "Тело запроса не распознано: ожидается форма или JSON-объект.");
        var key = Request.Headers["X-Integration-Key"].FirstOrDefault()
            ?? (fields.TryGetValue(FormDesignerWebhook.Fields.Key, out var k) ? k : null);
        if (!webhook.IsAuthorized(key)) return Unauthorized();
        var result = await webhook.ReceiveAsync(fields, ct);
        return result.StatusCode < 300
            ? StatusCode(result.StatusCode, new { ok = true, message = result.Message, bookingId = result.BookingId, duplicate = result.Duplicate })
            : Problem(statusCode: result.StatusCode, title: "Заявка отклонена", detail: result.Message);
    }

    /// <summary>Query string plus form or JSON body, merged into one case-insensitive dictionary of strings.</summary>
    private async Task<Dictionary<string, string?>?> ReadFlatParametersAsync(CancellationToken ct)
    {
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in Request.Query) fields[name] = value.ToString();
        if (Request.HasFormContentType)
        {
            foreach (var (name, value) in await Request.ReadFormAsync(ct)) fields[name] = value.ToString();
        }
        else if (Request.ContentLength is > 0 || Request.Headers.TransferEncoding.Count > 0)
        {
            try
            {
                using var json = await System.Text.Json.JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
                if (json.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
                foreach (var property in json.RootElement.EnumerateObject())
                    fields[property.Name] = property.Value.ValueKind == System.Text.Json.JsonValueKind.String
                        ? property.Value.GetString() : property.Value.GetRawText();
            }
            catch (System.Text.Json.JsonException) { return null; }
        }
        return fields;
    }

    [HttpPost("submissions")]
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
