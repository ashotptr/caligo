using Microsoft.Extensions.Options;

namespace Caligo.Api.Catalog;

public class DemoOptions
{
    public const string SectionName = "Demo";

    public string? PlanPath { get; set; }
}

public class DemoTrickleService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<DemoOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<DemoTrickleService> _logger;

    public DemoTrickleService(
        IServiceScopeFactory scopes,
        IOptions<DemoOptions> options,
        TimeProvider time,
        ILogger<DemoTrickleService> logger)
    {
        _scopes = scopes;
        _options = options;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var plan = _options.Value.PlanPath;

        if (string.IsNullOrWhiteSpace(plan))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(Random.Shared.Next(15, 31)), _time, stoppingToken);

            if (!File.Exists(plan) || !File.Exists(DemoActivity.ReceiptFor(plan)))
            {
                continue;
            }

            try
            {
                using var scope = _scopes.CreateScope();

                var until = DateTime.SpecifyKind(_time.GetUtcNow().UtcDateTime, DateTimeKind.Unspecified);
                var summary = await scope.ServiceProvider.GetRequiredService<DemoActivity>()
                    .AdvanceAsync(plan, until, stoppingToken);

                if (summary.Steps > 0)
                {
                    _logger.LogInformation("{Summary}", summary);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Demo activity could not advance.");
            }
        }
    }
}
