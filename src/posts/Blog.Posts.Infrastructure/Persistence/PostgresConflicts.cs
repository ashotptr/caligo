using Npgsql;

namespace Blog.Posts.Infrastructure.Persistence
{
    public static class PostgresConflicts
    {
        // 40001 and 40P01 mean another transaction got in the way. The work was rolled back and is
        // safe to run again from the start.
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

        // Exponential backoff with full jitter, so two transactions that collided once do not retry
        // in lockstep and collide again.
        public static TimeSpan Backoff(int attempt) =>
            TimeSpan.FromMilliseconds(Random.Shared.Next(1, (int)Math.Min(250, 10 * Math.Pow(2, attempt))));
    }
}
