using System.Diagnostics;
using TaskBoard.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// Шаг 3.2. Регистрация сервиса в DI.
// Singleton — потому что список задач хранится в памяти самого объекта сервиса.
// Scoped/Transient создавали бы новый пустой список на каждый запрос,
// и задачи «терялись» бы сразу после редиректа.
builder.Services.AddSingleton<ITaskService, InMemoryTaskService>();
builder.Services.AddSingleton<IStoreService, InMemoryStoreService>();

var app = builder.Build();

// ---------------------------------------------------------------------------
// Шаг 2. Замер времени обработки запроса.
// Стоит ПЕРВЫМ в конвейере, чтобы засечь время работы всех остальных middleware.
// Заголовок пишем из OnStarting: к моменту возврата из next() тело ответа уже
// начало отправляться, и коллекция заголовков становится read-only.
// ---------------------------------------------------------------------------
app.Use(async (context, next) =>
{
    var sw = Stopwatch.StartNew();

    context.Response.OnStarting(() =>
    {
        sw.Stop();
        context.Response.Headers.Append("X-Response-Time-ms", sw.ElapsedMilliseconds.ToString());
        return Task.CompletedTask;
    });

    await next(context);
});

// ---------------------------------------------------------------------------
// Шаг 1. Кастомный заголовок + логирование запроса и ответа.
// ---------------------------------------------------------------------------
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-App-Name", "TaskBoard");

    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("--> {Method} {Path}", context.Request.Method, context.Request.Path);

    await next(context);

    logger.LogInformation("<-- {StatusCode}", context.Response.StatusCode);
});

// ---------------------------------------------------------------------------
// Шаг 1 (усложнение). /health отвечает сразу и не передаёт запрос дальше.
// ---------------------------------------------------------------------------
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/health")
    {
        context.Response.StatusCode = 200;
        await context.Response.WriteAsync("healthy");
        return; // не вызываем next — запрос не идёт дальше
    }

    await next(context);
});

// ---------------------------------------------------------------------------
// Уровень 2 (среднее). Защита JSON API ключом X-Api-Key.
// ---------------------------------------------------------------------------
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/tasks/api"))
    {
        var key = context.Request.Headers["X-Api-Key"].ToString();

        if (key != "secret123")
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(new { error = "Неверный или отсутствующий X-Api-Key" });
            return; // дальше по конвейеру не идём
        }
    }

    await next(context);
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

// Шаг 4.2. Маршрутизация по умолчанию: {controller}/{action}/{id?}
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
