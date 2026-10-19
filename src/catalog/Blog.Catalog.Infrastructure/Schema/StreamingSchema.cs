using DbUp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Blog.Catalog.Infrastructure.Schema
{
    public class StreamingSchema
    {
        public const string Name = "streamingservice";
        public const string JournalTable = "schemaversions";
        public const string ScriptPrefix = "StreamingService.";

        private readonly CatalogOptions _options;
        private readonly ILogger<StreamingSchema> _logger;

        public StreamingSchema(IOptions<CatalogOptions> options, ILogger<StreamingSchema> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public void Ensure()
        {
            if (!_options.Enabled)
            {
                return;
            }

            Apply(_options.ConnectionString, _logger);
        }

        public static void Apply(string connectionString, ILogger logger)
        {
            using (var connection = new NpgsqlConnection(connectionString))
            {
                connection.Open();

                using var command = new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS {Name}", connection);

                command.ExecuteNonQuery();
            }

            var result = DeployChanges.To
                .PostgresqlDatabase(connectionString)
                .WithScriptsEmbeddedInAssembly(
                    typeof(StreamingSchema).Assembly,
                    name => name.StartsWith(ScriptPrefix, StringComparison.Ordinal))
                .WithVariablesDisabled()
                .WithTransactionPerScript()
                .JournalToPostgresqlTable(Name, JournalTable)
                .LogTo(logger)
                .Build()
                .PerformUpgrade();

            if (!result.Successful)
            {
                throw new InvalidOperationException(
                    $"Applying the streaming schema failed at {result.ErrorScript?.Name ?? "an unknown script"}.",
                    result.Error);
            }
        }
    }
}
