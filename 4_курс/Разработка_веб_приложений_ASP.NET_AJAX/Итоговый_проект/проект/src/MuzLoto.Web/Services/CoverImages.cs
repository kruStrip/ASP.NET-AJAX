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
        ".png" => "image/png", ".webp" => "image/webp", ".svg" => "image/svg+xml", _ => "image/jpeg"
    };

    // ---- Form images: ten fixed public links /i/1 … /i/10 ----

    public async Task<ImageSlot> SaveSlotAsync(int number, IFormFile file, string? note, CancellationToken ct = default)
    {
        if (number is < 1 or > ImageSlot.Count) throw new BusinessException("Нет такой ссылки.", 404);
        var extension = await CheckImageAsync(file, ct);
        var slot = await db.ImageSlots.SingleOrDefaultAsync(x => x.Number == number, ct);
        var previous = slot is not null && !slot.StorageKey.StartsWith('/') ? FullPath(slot.StorageKey) : null;
        if (slot is null) db.ImageSlots.Add(slot = new ImageSlot { Number = number, Version = 1 });
        else slot.Version++;
        slot.StorageKey = $"slots/{number}-v{slot.Version}{extension}";
        slot.UpdatedAt = DateTimeOffset.UtcNow;
        if (note is not null) slot.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        await WriteAsync(file, slot.StorageKey, ct);
        var target = FullPath(slot.StorageKey);
        try { await db.SaveChangesAsync(ct); }
        catch { File.Delete(target); throw; }
        if (previous is not null && previous != target) File.Delete(previous);
        return slot;
    }

    public async Task UpdateSlotNoteAsync(int number, string? note, CancellationToken ct = default)
    {
        var slot = await db.ImageSlots.SingleOrDefaultAsync(x => x.Number == number, ct)
            ?? throw new BusinessException("Сначала загрузите картинку.", 404);
        slot.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteSlotAsync(int number, CancellationToken ct = default)
    {
        var slot = await db.ImageSlots.SingleOrDefaultAsync(x => x.Number == number, ct);
        if (slot is null) return;
        var file = slot.StorageKey.StartsWith('/') ? null : FullPath(slot.StorageKey);
        db.ImageSlots.Remove(slot);
        await db.SaveChangesAsync(ct);
        if (file is not null) File.Delete(file);
    }

    public sealed record SlotFile(string Path, string ContentType, int Version, DateTimeOffset UpdatedAt);

    /// <summary>The file behind a public link; static demo keys ("/img/…") are served from wwwroot.</summary>
    public async Task<SlotFile?> OpenSlotAsync(int number, CancellationToken ct = default)
    {
        var slot = await db.ImageSlots.AsNoTracking().SingleOrDefaultAsync(x => x.Number == number, ct);
        if (slot is null) return null;
        var path = slot.StorageKey.StartsWith('/')
            ? Path.Combine(env.WebRootPath, slot.StorageKey.TrimStart('/'))
            : FullPath(slot.StorageKey);
        return File.Exists(path) ? new SlotFile(path, ContentType(path), slot.Version, slot.UpdatedAt) : null;
    }

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
