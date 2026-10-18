using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using TaskFlowApi.Data;
using TaskFlowApi.Entities;

namespace TaskFlowApi.Middleware;

/// <summary>
/// Защищает POST /api/v2/tasks от повторной обработки одинаковых запросов
/// с помощью заголовка X-Idempotency-Key.
/// </summary>
public class IdempotencyMiddleware
{
    private const string HeaderName = "X-Idempotency-Key";
    private static readonly PathString ProtectedPath = new("/api/v2/tasks");

    private readonly RequestDelegate _next;

    public IdempotencyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AppDbContext db)
    {
        if (!IsProtectedRequest(context))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var keyValues) || string.IsNullOrWhiteSpace(keyValues))
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "Missing idempotency key",
                $"Заголовок {HeaderName} обязателен при создании задачи.");
            return;
        }

        var key = keyValues.ToString();

        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        context.Request.Body.Position = 0;

        var bodyHash = ComputeHash(body);

        var existing = await db.IdempotencyRecords.FindAsync(key);
        if (existing is not null)
        {
            if (existing.RequestBodyHash != bodyHash)
            {
                await WriteProblemAsync(context, StatusCodes.Status409Conflict, "Idempotency key conflict",
                    "Idempotency key reused with different body");
                return;
            }

            context.Response.StatusCode = existing.StatusCode;
            context.Response.ContentType = "application/json";
            if (!string.IsNullOrEmpty(existing.Location))
            {
                context.Response.Headers.Location = existing.Location;
            }

            await context.Response.WriteAsync(existing.ResponseBody);
            return;
        }

        var originalBodyStream = context.Response.Body;
        using var capturedBody = new MemoryStream();
        context.Response.Body = capturedBody;

        await _next(context);

        capturedBody.Position = 0;
        var responseText = await new StreamReader(capturedBody).ReadToEndAsync();

        capturedBody.Position = 0;
        await capturedBody.CopyToAsync(originalBodyStream);
        context.Response.Body = originalBodyStream;

        if (context.Response.StatusCode is >= 200 and < 300)
        {
            db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Key = key,
                RequestBodyHash = bodyHash,
                ResponseBody = responseText,
                StatusCode = context.Response.StatusCode,
                Location = context.Response.Headers.Location.ToString() is { Length: > 0 } location ? location : null,
                CreatedAt = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
        }
    }

    private static bool IsProtectedRequest(HttpContext context)
    {
        return HttpMethods.IsPost(context.Request.Method) &&
               context.Request.Path.StartsWithSegments(ProtectedPath, out var remaining) &&
               remaining.Value is null or "";
    }

    private static string ComputeHash(string body)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(body));
        return Convert.ToHexString(bytes);
    }

    private static async Task WriteProblemAsync(HttpContext context, int statusCode, string title, string detail)
    {
        var problem = new ProblemDetails
        {
            Title = title,
            Status = statusCode,
            Detail = detail
        };

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(problem);
    }
}
