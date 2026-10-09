using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using Asp.Versioning;
using Asp.Versioning.ApiExplorer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using TaskFlowApi.Authorization;
using TaskFlowApi.Data;
using TaskFlowApi.Entities;
using TaskFlowApi.Exceptions;
using TaskFlowApi.Middleware;

var builder = WebApplication.CreateBuilder(args);

const string CorsPolicyName = "FrontendClient";

// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=taskflow.db"));

var jwt = builder.Configuration.GetSection("Jwt");
var jwtKey = jwt["Key"] ?? throw new InvalidOperationException("Jwt:Key должен быть настроен через конфигурацию или секреты.");
builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt["Issuer"],
            ValidateAudience = true,
            ValidAudience = jwt["Audience"],
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.Zero
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AuthorOrAdmin", policy => policy.RequireRole("Author", "Admin"));
    options.AddPolicy("CanEditTask", policy => policy.Requirements.Add(new CanEditTaskRequirement()));
});
builder.Services.AddSingleton<IAuthorizationHandler, CanEditTaskHandler>();

builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
})
    .AddMvc()
    .AddApiExplorer(options =>
    {
        options.GroupNameFormat = "'v'VVV";
        options.SubstituteApiVersionInUrl = true;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "TaskFlow API",
        Version = "v1",
        Description = "Устаревшая версия API для управления задачами. Используйте v2."
    });
    options.SwaggerDoc("v2", new OpenApiInfo
    {
        Title = "TaskFlow API",
        Version = "v2",
        Description = "Текущая версия API: приоритет задач, пагинация, идемпотентность."
    });

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }
});

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicyName, policy =>
    {
        policy.WithOrigins("http://localhost:3000")
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseExceptionHandler();

app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/swagger"))
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'; base-uri 'self'";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            return Task.CompletedTask;
        });
    }

    await next();
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        var provider = app.Services.GetRequiredService<IApiVersionDescriptionProvider>();
        foreach (var description in provider.ApiVersionDescriptions)
        {
            var displayName = description.GroupName.ToUpperInvariant();
            if (description.IsDeprecated)
            {
                displayName += " (устарела)";
            }

            options.SwaggerEndpoint($"/swagger/{description.GroupName}/swagger.json", displayName);
        }
    });
}

app.UseHttpsRedirection();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseRouting();

app.UseCors(CorsPolicyName);

app.UseAuthentication();
app.UseAuthorization();

app.UseMiddleware<IdempotencyMiddleware>();

app.MapControllers();

app.Run();

/// <summary>Точка входа приложения (частичный класс для тестирования).</summary>
public partial class Program
{
}
