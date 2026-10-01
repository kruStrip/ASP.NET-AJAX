using LiveMonitor.Hubs;
using LiveMonitor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddSingleton<ConnectionTracker>();
builder.Services.AddCors(options => options.AddPolicy("frontend", policy => policy
    .WithOrigins("http://localhost:5000", "https://localhost:5001", "http://127.0.0.1:5000")
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors("frontend");

app.MapHub<MonitorHub>("/monitorHub");

app.MapPost("/api/system-message", async (NotificationRequest request, ConnectionTracker tracker) =>
{
    await tracker.SendSystemMessageAsync(request.Text);
    return Results.Ok();
});

app.MapPost("/api/room-message", async (RoomNotificationRequest request, ConnectionTracker tracker) =>
{
    await tracker.SendRoomMessageAsync(request.Room, request.Text);
    return Results.Ok();
});

app.MapPost("/api/private-message", async (PrivateNotificationRequest request, ConnectionTracker tracker) =>
{
    await tracker.SendPrivateMessageAsync(request.ConnectionId, request.Text);
    return Results.Ok();
});

app.Run();

public sealed record NotificationRequest(string Text);
public sealed record RoomNotificationRequest(string Room, string Text);
public sealed record PrivateNotificationRequest(string ConnectionId, string Text);
