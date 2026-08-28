using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

// Traces, metrics and logs. EF Core and Redis are instrumented here rather than in the shared
// helper because only this service has them.
builder.AddBlogTelemetry(
    "blog-api",
    tracing => tracing
        .AddEntityFrameworkCoreInstrumentation(ef => ef.SetDbStatementForText = builder.Environment.IsDevelopment())
        // gRPC calls ride on HttpClient, which is already instrumented above; the separate
        // gRPC client instrumentation would only duplicate those spans.
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
        policy.WithOrigins(allowedOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddRateLimiter(options =>
{
    // Credential endpoints: tight, because this is what stands between the site and
    // password spraying.
    options.AddFixedWindowLimiter("auth", opt =>
    {
        opt.PermitLimit = 10;
        opt.Window = TimeSpan.FromSeconds(12);
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 2;
    });

    // Read endpoints used to be unlimited. They are cheap individually but trivially
    // scriptable, so they get a much looser per-IP ceiling of their own.
    options.AddPolicy("reads", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        }));

    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// Database, cache, search, messaging and the post use cases. Which database and which broker
// is a decision that belongs next to the code implementing it, not in the web host.
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

        // Pin the algorithm so a token cannot talk the validator into a weaker one.
        ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },

        // Default is five minutes of leeway, which makes short-lived access tokens meaningless.
        ClockSkew = TimeSpan.FromSeconds(30)
    };
});

builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IRefreshSessionService, RefreshSessionService>();

builder.Services.AddControllers();

builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen();

var app = builder.Build();

// Validated after Build() rather than during registration, so that configuration layered in
// by a host (WebApplicationFactory in tests, environment variables in containers) is visible.
// HMAC-SHA256 keys shorter than the 256-bit hash output weaken the signature and the library
// will not warn about it, so this fails fast rather than serving forgeable tokens.
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

    // The API only ever returns JSON, so it can afford the strictest possible policy:
    // nothing is allowed to load or execute from a response.
    headers.Append("Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'");

    // TLS is terminated at the proxy, so the request reaching this process is plain HTTP
    // even in production. The environment, not Request.IsHttps, is what decides here.
    if (!app.Environment.IsDevelopment())
    {
        headers.Append("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
    }

    await next();
});

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

    // Resolved rather than captured at registration, so this reflects the database the
    // application actually connected to.
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
