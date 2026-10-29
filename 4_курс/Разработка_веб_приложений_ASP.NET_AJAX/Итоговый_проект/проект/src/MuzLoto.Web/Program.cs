using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Microsoft.EntityFrameworkCore;
using MuzLoto.Web.Data;
using MuzLoto.Web.Domain;
using MuzLoto.Web.Hubs;
using MuzLoto.Web.Services;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var connection = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Default.");
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connection));
builder.Services.AddIdentity<AppUser, IdentityRole>(options =>
{
    options.User.RequireUniqueEmail = true;
    options.SignIn.RequireConfirmedAccount = false;
    options.Password.RequiredLength = 6;
    options.Password.RequireDigit = true;
    options.Password.RequireUppercase = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireNonAlphanumeric = false;
    options.Lockout.AllowedForNewUsers = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
}).AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/account/login";
    o.AccessDeniedPath = "/account/denied";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // local HTTP permitted, production HTTPS required
    o.Events.OnRedirectToLogin = ctx =>
    {
        if (ctx.Request.Headers["X-Requested-With"] == "XMLHttpRequest" || ctx.Request.Path.StartsWithSegments("/api"))
        { ctx.Response.StatusCode = 401; return Task.CompletedTask; }
        ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
    o.Events.OnRedirectToAccessDenied = ctx =>
    {
        if (ctx.Request.Headers["X-Requested-With"] == "XMLHttpRequest" || ctx.Request.Path.StartsWithSegments("/api"))
        { ctx.Response.StatusCode = 403; return Task.CompletedTask; }
        ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
});
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);
// Identity's cookie remains the default for Razor Pages. API controllers explicitly use JWT.
var jwtKey = JwtTokenService.RequireSigningKey(builder.Configuration);
builder.Services.AddAuthentication().AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateIssuerSigningKey = true,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "MuzLoto",
        ValidAudience = builder.Configuration["Jwt:Audience"] ?? "MuzLoto.Api",
        IssuerSigningKey = new SymmetricSecurityKey(jwtKey),
        RoleClaimType = System.Security.Claims.ClaimTypes.Role,
        NameClaimType = System.Security.Claims.ClaimTypes.Name,
        ClockSkew = TimeSpan.FromSeconds(15)
    };
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = async context =>
        {
            var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<AppUser>>();
            var principal = context.Principal;
            var id = principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            var stamp = principal?.FindFirst("muzloto_security_stamp")?.Value;
            var user = id is not null ? await userManager.FindByIdAsync(id) : null;
            if (user is null || stamp is null || stamp != await userManager.GetSecurityStampAsync(user))
            {
                context.Fail("Устаревший токен: войдите заново.");
                return;
            }
            var actualRoles = await userManager.GetRolesAsync(user);
            var tokenRoles = principal!.FindAll(ClaimTypes.Role).Select(x => x.Value).ToHashSet();
            if (!tokenRoles.SetEquals(actualRoles)) context.Fail("Права доступа изменились: войдите заново.");
        }
    };
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(BookingPolicies.CanViewBooking, p => p.AddRequirements(new BookingRequirement(OwnerAccess.AnyStatus)));
    options.AddPolicy(BookingPolicies.CanEditBooking, p => p.AddRequirements(new BookingRequirement(OwnerAccess.PendingOnly)));
    options.AddPolicy(BookingPolicies.CanManageBooking, p => p.AddRequirements(new BookingRequirement(OwnerAccess.None)));
});
builder.Services.AddScoped<IAuthorizationHandler, BookingAuthorizationHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "МузЛото REST API", Version = "v1",
        Description = "Мероприятия (плоский контракт), заявки, отчёты. Токен: POST /api/auth/login." });
    options.SwaggerDoc("v2", new OpenApiInfo { Title = "МузЛото REST API", Version = "v2",
        Description = "Мероприятия v2: вложенные location/availability, remainingTickets, mapUrl, поиск и пагинация." });
    // Controllers without a group (auth, integration) are shared by both documents.
    options.DocInclusionPredicate((doc, api) => api.GroupName is null || api.GroupName == doc);
    options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, "MuzLoto.Web.xml"));
    // Every controller nests its own ReadDto/WriteDto: prefix the schema with the controller to keep names unique.
    options.CustomSchemaIds(t => t.DeclaringType is null ? t.Name : t.DeclaringType.Name.Replace("Controller", "") + t.Name);
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization", Type = SecuritySchemeType.Http,
        Scheme = "bearer", BearerFormat = "JWT", In = ParameterLocation.Header,
        Description = "JWT access token from POST /api/auth/login"
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme
        {
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
        }] = Array.Empty<string>()
    });
});
// CSRF: every unsafe cookie-authenticated request needs a token; AJAX sends it in X-CSRF-TOKEN.
builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Home");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/Register");
    options.Conventions.AllowAnonymousToPage("/Account/Denied");
}).AddMvcOptions(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
// JWT API controllers opt out with [IgnoreAntiforgeryToken]: browsers never attach a bearer token by themselves.
builder.Services.AddControllers(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));

builder.Services.AddSignalR();
builder.Services.AddSingleton<HubConnectionTracker>();
builder.Services.AddScoped<HubAccessRevoker>();
builder.Services.AddScoped<BookingWorkflow>();
builder.Services.AddScoped<StaffAccess>();
builder.Services.AddScoped<StaffManagement>();
builder.Services.AddScoped<SessionCrud>();
builder.Services.AddScoped<SalesReport>();
builder.Services.AddScoped<CoverImages>();
builder.Services.AddScoped<JwtTokenService>();

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/swagger/v1/swagger.json", "МузЛото API v1");
        o.SwaggerEndpoint("/swagger/v2/swagger.json", "МузЛото API v2");
    });
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
app.Use(async (ctx, next) =>
{
    ctx.Response.OnStarting(() =>
    {
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self' ws: wss:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
        return Task.CompletedTask;
    });
    await next();
});
// Money and dates are shown in Russian format regardless of the server's OS locale ("1 400,00", not "1,400.00").
app.UseRequestLocalization("ru-RU");
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
// Uploaded covers: staff only, cache-busted by ?v=version.
app.MapGet("/images/{id:guid}", async (Guid id, CoverImages images, HttpContext http, CancellationToken ct) =>
{
    var file = await images.OpenAsync(id, ct);
    if (file is null) return Results.NotFound();
    http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
    return Results.File(file.Value.Path, file.Value.ContentType);
}).RequireAuthorization(new AuthorizeAttribute { Roles = "Admin,Manager" });
app.MapRazorPages();
app.MapControllers();
app.MapHub<BookingHub>("/hubs/bookings");

// Migrations must be created with 'dotnet ef migrations add InitialCreate' before first run.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (!(await db.Database.GetPendingMigrationsAsync()).Any() &&
        !(await db.Database.GetAppliedMigrationsAsync()).Any())
        throw new InvalidOperationException("No EF Core migrations found. Run scripts/init-db.sh first.");
    await db.Database.MigrateAsync();
}
await IdentitySeeder.SeedAsync(app.Services, app.Configuration, app.Environment);
await DemoDataSeeder.SeedAsync(app.Services, app.Configuration, app.Environment);
app.Run();
public partial class Program;
