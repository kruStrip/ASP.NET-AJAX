using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MuzLoto.Web.Data;
using Xunit;

namespace MuzLoto.Tests;

/// <summary>Ten fixed public image links for the FormDesigner form: the URL stays, the picture behind it changes.</summary>
[Trait("Category", "Integration")]
[Collection(AppCollection.Name)]
public sealed class FormImageLinksTests(AppFactory app)
{
    private static readonly byte[] PngA = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 1, 1, 1];
    private static readonly byte[] PngB = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 2, 2, 2, 2];

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient admin, int number, byte[] bytes, string? note = null, string handler = "Upload")
    {
        var token = await AppFactory.AntiforgeryTokenAsync(admin, "/form-images");
        var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent(number.ToString()), "number" },
            { new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, "image", "picture.png" }
        };
        if (note is not null) form.Add(new StringContent(note), "note");
        return await admin.PostAsync($"/form-images?handler={handler}", form);
    }

    private async Task ClearAsync(int number)
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().ImageSlots.Where(x => x.Number == number).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Link_ServesUploadedPicture_ReplacementKeepsUrl_WithRevalidation()
    {
        const int number = 7;
        await ClearAsync(number);
        var admin = await app.SignedInAsync(await app.CreateUserAsync("Admin"));
        var anonymous = app.Browser();

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/i/{number}")).StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await UploadAsync(admin, number, PngA, "05.11 — тест")).StatusCode);
        var first = await anonymous.GetAsync($"/i/{number}");            // public: no login needed
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("image/png", first.Content.Headers.ContentType?.MediaType);
        Assert.Equal(PngA, await first.Content.ReadAsByteArrayAsync());
        Assert.Equal(TimeSpan.FromMinutes(5), first.Headers.CacheControl?.MaxAge);
        var etag = first.Headers.ETag!;

        using (var revalidate = new HttpRequestMessage(HttpMethod.Get, $"/i/{number}"))
        {
            revalidate.Headers.IfNoneMatch.Add(etag);
            Assert.Equal(HttpStatusCode.NotModified, (await anonymous.SendAsync(revalidate)).StatusCode);
        }

        await UploadAsync(admin, number, PngB);
        var second = await anonymous.GetAsync($"/i/{number}");           // same URL, new picture
        Assert.Equal(PngB, await second.Content.ReadAsByteArrayAsync());
        Assert.NotEqual(etag, second.Headers.ETag);
        using (var stale = new HttpRequestMessage(HttpMethod.Get, $"/i/{number}"))
        {
            stale.Headers.IfNoneMatch.Add(etag);
            Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(stale)).StatusCode);  // old ETag → new picture
        }
        using (var scope = app.Services.CreateScope())
        {
            var slot = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ImageSlots.SingleAsync(x => x.Number == number);
            Assert.Equal((2, "05.11 — тест"), (slot.Version, slot.Note));                      // note kept when not sent
            Assert.Single(Directory.GetFiles(Path.Combine(AppFactory.ImagesPath, "slots"), $"{number}-v*"));  // old file removed
        }

        var token = await AppFactory.AntiforgeryTokenAsync(admin, "/form-images");
        await admin.PostAsync("/form-images?handler=Delete", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["__RequestVerificationToken"] = token, ["number"] = number.ToString() }));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/i/{number}")).StatusCode);
    }

    [Fact]
    public async Task OnlyTenLinks_OnlyImages_OnlyAdminsUpload()
    {
        var anonymous = app.Browser();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/i/0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/i/11")).StatusCode);

        const int number = 8;
        await ClearAsync(number);
        var admin = await app.SignedInAsync(await app.CreateUserAsync("Admin"));
        await UploadAsync(admin, number, "<html>not an image</html>"u8.ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/i/{number}")).StatusCode);

        var manager = await app.SignedInAsync(await app.CreateUserAsync("Manager"));
        var denied = await manager.GetAsync("/form-images");
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(denied.Headers.Location)).StatusCode);
    }
}
