using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;

namespace MuzLoto.Web.Services;

/// <summary>
/// Session cover storage. Files live outside wwwroot (Storage:ImagesPath, default App_Data/images) and are
/// bound to the stable session ID, so changing the date never detaches a photo. Replacing bumps Version.
/// </summary>
public sealed class CoverImages(AppDbContext db, IConfiguration config, IWebHostEnvironment env)
{
    public const long MaxBytes = 5 * 1024 * 1024;

    private string Root => config["Storage:ImagesPath"] is { Length: > 0 } path
        ? path : Path.Combine(env.ContentRootPath, "App_Data", "images");

    /// <summary>URL for a stored cover: seeded static files are served from wwwroot, uploads through /images/{id}.</summary>
    public static string Url(GameImage image) => image.StorageKey.StartsWith('/')
        ? image.StorageKey : $"/images/{image.Id}?v={image.Version}";

    /// <summary>Size and content checks shared by covers and form images; returns the extension of the detected type.</summary>
    private static async Task<string> CheckImageAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length is 0 or > MaxBytes)
            throw new BusinessException("Файл пустой или больше 5 МБ.", 400);
        var header = new byte[12];
        await using (var probe = file.OpenReadStream())
            _ = await probe.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        return DetectExtension(header)
            ?? throw new BusinessException("Поддерживаются только изображения JPEG, PNG и WebP.", 400);
    }

    private async Task WriteAsync(IFormFile file, string key, CancellationToken ct)
    {
        var target = FullPath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var output = File.Create(target);
        await file.CopyToAsync(output, ct);
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".webp" => "image/webp", ".svg" => "image/svg+xml", ".pdf" => "application/pdf", _ => "image/jpeg"
    };

    // ---- Payment receipts uploaded by customers ----

    public const long MaxReceiptBytes = 10 * 1024 * 1024;

    /// <summary>Stores (or replaces) a booking's receipt: an image or a PDF up to 10 MB, checked by its content.</summary>
    public async Task SaveReceiptAsync(Booking booking, IFormFile file, CancellationToken ct = default)
    {
        if (file.Length is 0 or > MaxReceiptBytes)
            throw new BusinessException("Файл пустой или больше 10 МБ.", 400);
        var header = new byte[12];
        await using (var probe = file.OpenReadStream())
            _ = await probe.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        var extension = DetectExtension(header) ?? (header is [(byte)'%', (byte)'P', (byte)'D', (byte)'F', ..] ? ".pdf" : null)
            ?? throw new BusinessException("Загрузите фото или скриншот (JPEG, PNG, WebP) или PDF-чек.", 400);
        var previous = booking.ReceiptKey is { } old ? FullPath(old) : null;
        booking.ReceiptKey = $"receipts/{booking.Id:N}-{DateTime.UtcNow:yyyyMMddHHmmssfff}{extension}";
        booking.ReceiptUploadedAt = DateTimeOffset.UtcNow;
        await WriteAsync(file, booking.ReceiptKey, ct);
        var target = FullPath(booking.ReceiptKey);
        try { await db.SaveChangesAsync(ct); }
        catch { File.Delete(target); throw; }
        if (previous is not null && previous != target) File.Delete(previous);
    }

    public (string Path, string ContentType)? OpenReceipt(Booking booking)
    {
        if (booking.ReceiptKey is null) return null;
        var path = FullPath(booking.ReceiptKey);
        return File.Exists(path) ? (path, ContentType(path)) : null;
    }

    public static bool IsPdf(string? key) => key?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) == true;

    // ---- Payment QR code shown on the payment screen ----

    public async Task SavePaymentQrAsync(IFormFile file, CancellationToken ct = default)
    {
        var extension = await CheckImageAsync(file, ct);
        var settings = await PaymentSettingsAsync(ct);
        var previous = settings.QrStorageKey is { } old ? FullPath(old) : null;
        settings.QrVersion++;
        settings.QrStorageKey = $"payment/qr-v{settings.QrVersion}{extension}";
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        await WriteAsync(file, settings.QrStorageKey, ct);
        var target = FullPath(settings.QrStorageKey);
        try { await db.SaveChangesAsync(ct); }
        catch { File.Delete(target); throw; }
        if (previous is not null && previous != target) File.Delete(previous);
    }

    public async Task DeletePaymentQrAsync(CancellationToken ct = default)
    {
        var settings = await PaymentSettingsAsync(ct);
        if (settings.QrStorageKey is not { } key) return;
        settings.QrStorageKey = null;
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        File.Delete(FullPath(key));
    }

    public async Task<StoredFile?> OpenPaymentQrAsync(CancellationToken ct = default)
    {
        var settings = await db.PaymentSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        if (settings?.QrStorageKey is not { } key) return null;
        var path = FullPath(key);
        return File.Exists(path) ? new StoredFile(path, ContentType(path), settings.QrVersion, settings.UpdatedAt) : null;
    }

    /// <summary>The single settings row, created on first use (tracked).</summary>
    public async Task<PaymentSettings> PaymentSettingsAsync(CancellationToken ct = default)
    {
        var settings = await db.PaymentSettings.SingleOrDefaultAsync(ct);
        if (settings is null) db.PaymentSettings.Add(settings = new PaymentSettings());
        return settings;
    }

    public sealed record StoredFile(string Path, string ContentType, int Version, DateTimeOffset UpdatedAt);


    public async Task<GameImage> SaveAsync(Guid sessionId, IFormFile file, CancellationToken ct = default)
    {
        var extension = await CheckImageAsync(file, ct);
        if (!await db.GameSessions.AnyAsync(x => x.Id == sessionId, ct))
            throw new BusinessException("Сеанс не найден.", 404);

        var image = await db.GameImages.Where(x => x.GameSessionId == sessionId)
            .OrderBy(x => x.SortOrder).FirstOrDefaultAsync(ct);
        var previousFile = image is not null && !image.StorageKey.StartsWith('/') ? FullPath(image.StorageKey) : null;
        if (image is null)
        {
            image = new GameImage { GameSessionId = sessionId, SortOrder = 0, Version = 1 };
            db.GameImages.Add(image);
        }
        else image.Version++;

        // Key is generated by the server: no client-supplied file names ever reach the disk.
        image.StorageKey = $"{sessionId:N}/{image.Id:N}-v{image.Version}{extension}";
        await WriteAsync(file, image.StorageKey, ct);
        var target = FullPath(image.StorageKey);
        try { await db.SaveChangesAsync(ct); }
        catch { File.Delete(target); throw; }
        if (previousFile is not null && previousFile != target) File.Delete(previousFile);
        return image;
    }

    /// <summary>A new date of an existing game gets its own copy of the latest date's picture (if there is one).</summary>
    public async Task CopyLatestAsync(Guid gameId, Guid sessionId, CancellationToken ct = default)
    {
        var source = await db.GameImages.AsNoTracking()
            .Where(x => x.GameSession.GameId == gameId && x.GameSessionId != sessionId)
            .OrderByDescending(x => x.GameSession.StartsAt).ThenBy(x => x.SortOrder).FirstOrDefaultAsync(ct);
        if (source is null) return;
        var image = new GameImage { GameSessionId = sessionId, SortOrder = 0, Version = 1 };
        if (source.StorageKey.StartsWith('/')) image.StorageKey = source.StorageKey; // static demo file
        else
        {
            var from = FullPath(source.StorageKey);
            if (!File.Exists(from)) return;
            // A separate file: replacing or deleting one date's picture never touches the other.
            image.StorageKey = $"{sessionId:N}/{image.Id:N}-v1{Path.GetExtension(from)}";
            var target = FullPath(image.StorageKey);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(from, target);
        }
        db.GameImages.Add(image);
        await db.SaveChangesAsync(ct);
    }

    public async Task<(string Path, string ContentType)?> OpenAsync(Guid imageId, CancellationToken ct = default)
    {
        var image = await db.GameImages.AsNoTracking().SingleOrDefaultAsync(x => x.Id == imageId, ct);
        if (image is null || image.StorageKey.StartsWith('/')) return null;
        var path = FullPath(image.StorageKey);
        if (!File.Exists(path)) return null;
        return (path, ContentType(path));
    }

    private string FullPath(string key)
    {
        var root = Path.GetFullPath(Root);
        var full = Path.GetFullPath(Path.Combine(root, key));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Image key escapes the storage folder.");
        return full;
    }

    private static string? DetectExtension(byte[] h) =>
        h is [0xFF, 0xD8, 0xFF, ..] ? ".jpg"
        : h is [0x89, 0x50, 0x4E, 0x47, ..] ? ".png"
        : h is [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P'] ? ".webp"
        : null;
}
