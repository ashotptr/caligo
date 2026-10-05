using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Blog.Api.Services;
using Blog.Observability;
using OpenTelemetry.Trace;
using Blog.Posts.Infrastructure;
using Blog.Posts.Infrastructure.Identity;
using Blog.Posts.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddBlogTelemetry(
    "blog-api",
    tracing => tracing
        .AddEntityFrameworkCoreInstrumentation(ef => ef.SetDbStatementForText = builder.Environment.IsDevelopment())
        .AddRedisInstrumentation());

builder.Services.AddHealthChecks()
    .AddNpgSql(
        _ => builder.Configuration.GetConnectionString("DefaultConnection") ?? string.Empty,
        name: "postgres",
        tags: new[] { HealthEndpoints.ReadinessTag })
    .AddRedis(
        _ => builder.Configuration.GetConnectionString("Redis") ?? string.Empty,
        name: "redis",
        tags: new[] { HealthEndpoints.ReadinessTag })
    .AddCheck(
        "kafka",
        new KafkaBrokerHealthCheck(builder.Configuration["Kafka:BootstrapServers"] ?? string.Empty),
        tags: new[] { HealthEndpoints.ReadinessTag });

builder.Services.AddCors(options =>
{
    var allowedOrigins = builder.Configuration.GetValue<string>("Cors:AllowedOrigins") ?? "http://localhost:5173";

    options.AddDefaultPolicy(policy =>
    {
        // The editor sends a post's ETag back as If-Match; browsers hide it from other origins unless exposed.
        policy.WithOrigins(allowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
              .AllowAnyHeader()
              .AllowAnyMethod()
              .WithExposedHeaders("ETag");
    });
});

// Behind nginx or the ingress every request comes from the proxy's address. Trusting
// X-Forwarded-For from the proxy networks listed here gives the per-client rate limit and the
// visitor hash the real client address. Nothing is trusted beyond the loopback default unless
// networks are configured.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    foreach (var network in builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
    {
        var parts = network.Split('/');

        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse(parts[0]), int.Parse(parts[1])));
    }
});

var limits = builder.Configuration.GetSection("RateLimiting");

builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("auth", opt =>
    {
        opt.PermitLimit = limits.GetValue("AuthPermitLimit", 10);
        opt.Window = limits.GetValue("AuthWindow", TimeSpan.FromSeconds(12));
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 2;
    });

    options.AddPolicy("reads", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limits.GetValue("ReadsPermitLimit", 120),
            Window = limits.GetValue("ReadsWindow", TimeSpan.FromMinutes(1)),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        }));

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

builder.Services.AddPostsInfrastructure(builder.Environment.IsDevelopment());

builder.Services.AddIdentity<ApplicationUser, IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
            builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT Key is not configured."))),

        ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },

        ClockSkew = TimeSpan.FromSeconds(30)
    };
});

builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IRefreshSessionService, RefreshSessionService>();

builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen();

var app = builder.Build();

var signingKeyBytes = Encoding.UTF8.GetByteCount(
    app.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is not configured."));

if (signingKeyBytes < 32)
{
    throw new InvalidOperationException(
        "Jwt:Key must be at least 32 bytes (256 bits) for HMAC-SHA256. " +
        $"The configured key is {signingKeyBytes} bytes.");
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;

    headers.Append("X-Frame-Options", "DENY");
    headers.Append("X-Content-Type-Options", "nosniff");
    headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");

    headers.Append("Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'");

    if (!app.Environment.IsDevelopment())
    {
        headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
    }

    await next();
});

app.UseForwardedHeaders();

app.UseCors();

app.UseRateLimiter();

app.UseAuthentication();

app.UseAuthorization();

app.MapControllers();

app.MapBlogHealthChecks();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var db = services.GetRequiredService<ApplicationDbContext>();

    var database = services.GetRequiredService<DependencyInjection.DatabaseChoice>();

    if (database.Provider == DatabaseProvider.Sqlite)
    {
        db.Database.EnsureCreated();

        if (database.FellBackToSqlite)
        {
            app.Logger.LogWarning(
                "PostgreSQL was configured but could not be reached, so this process is " +
                "running on SQLite at {ConnectionString}. Data will not be shared with " +
                "the PostgreSQL database. Set Database:FallbackToSqlite to false to fail " +
                "instead.", database.ConnectionString);
        }
        else
        {
            app.Logger.LogInformation(
                "Using SQLite at {ConnectionString}. No migrations were applied, the schema " +
                "was created from the model. Configure ConnectionStrings:DefaultConnection " +
                "to use PostgreSQL.", database.ConnectionString);
        }
    }
    else
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                db.Database.Migrate();

                break;
            }
            catch (Exception ex) when (attempt < 5)
            {
                app.Logger.LogWarning(ex, "Database not ready (attempt {Attempt}/5), retrying in 3 seconds...", attempt);

                await Task.Delay(3000);
            }
        }
    }

    await SeedData.Initialize(services, services.GetRequiredService<IConfiguration>());
}

app.Run();

public partial class Program { }
