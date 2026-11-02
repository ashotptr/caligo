using Microsoft.Extensions.Options;
using Caligo.Catalog.Application.Accounts;
using Caligo.Catalog.Infrastructure;

namespace Caligo.Api.Catalog;

public sealed class SubscriptionRenewalService : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<CatalogOptions> _options;
    private readonly ILogger<SubscriptionRenewalService> _logger;

    public SubscriptionRenewalService(
        IServiceScopeFactory scopes,
        IOptions<CatalogOptions> options,
        ILogger<SubscriptionRenewalService> logger)
    {
        _scopes = scopes;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(Every);

        do
        {
            try
            {
                using var scope = _scopes.CreateScope();

                var renewed = await scope.ServiceProvider
                    .GetRequiredService<StreamingAccountService>()
                    .RenewDueAsync(stoppingToken);

                if (renewed > 0)
                {
                    _logger.LogInformation("Renewed {Count} subscriptions", renewed);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Renewing subscriptions failed; trying again later");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
