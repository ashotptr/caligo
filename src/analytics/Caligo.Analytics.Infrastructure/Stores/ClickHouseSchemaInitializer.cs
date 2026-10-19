using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Caligo.Analytics.Infrastructure.Stores
{
    public class ClickHouseSchemaInitializer : BackgroundService
    {
        private readonly ClickHouseOlapStore _store;
        private readonly ILogger<ClickHouseSchemaInitializer> _logger;

        public ClickHouseSchemaInitializer(ClickHouseOlapStore store, ILogger<ClickHouseSchemaInitializer> logger)
        {
            _store = store;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_store.IsConfigured)
            {
                _logger.LogInformation("No ClickHouse configured; OLAP storage is off.");

                return;
            }

            for (var attempt = 1; !stoppingToken.IsCancellationRequested; attempt++)
            {
                try
                {
                    await _store.EnsureSchemaAsync(stoppingToken);

                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "ClickHouse not ready (attempt {Attempt}); retrying in 5 seconds.", attempt);

                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
            }
        }
    }
}
