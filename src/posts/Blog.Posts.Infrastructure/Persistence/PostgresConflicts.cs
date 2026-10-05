using Npgsql;

namespace Blog.Posts.Infrastructure.Persistence
{
    public static class PostgresConflicts
    {
        public static bool IsRetryable(Exception exception) => SqlState(exception) is
            PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected;

        public static bool IsForeignKeyViolation(Exception exception) =>
            SqlState(exception) == PostgresErrorCodes.ForeignKeyViolation;

        public static string? SqlState(Exception exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                if (current is PostgresException postgres)
                {
                    return postgres.SqlState;
                }
            }

            return null;
        }

        public static TimeSpan Backoff(int attempt) =>
            TimeSpan.FromMilliseconds(Random.Shared.Next(1, (int)Math.Min(250, 10 * Math.Pow(2, attempt))));
    }
}
