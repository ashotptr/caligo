using Npgsql;
using NpgsqlTypes;

namespace Caligo.Catalog.Infrastructure.Persistence
{
    internal static class SqlValues
    {
        public static NpgsqlParameter Timestamp(DateTime value) => new()
        {
            NpgsqlDbType = NpgsqlDbType.Timestamp,
            Value = DateTime.SpecifyKind(value, DateTimeKind.Unspecified)
        };
    }
}
