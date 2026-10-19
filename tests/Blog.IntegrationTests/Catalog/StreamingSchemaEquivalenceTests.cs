using System.Collections;
using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;
using Npgsql;
using Testcontainers.MySql;
using Blog.Catalog.Infrastructure;
using Blog.Catalog.Infrastructure.Persistence;
using Blog.Catalog.Infrastructure.Schema;
using Xunit;

namespace Blog.IntegrationTests.Catalog;

[Collection(InfrastructureCollection.Name)]
public class StreamingSchemaEquivalenceTests : IAsyncLifetime
{
    private static readonly string[] Tables =
    {
        "actor:actor_id", "comment:comment_id", "content:content_id", "contentactor:content_id, actor_id",
        "contentdistributingcompanies:content_id, distributing_company_id", "contentgenres:content_id, genre_id",
        "contentlanguages:content_id, language_id", "currentcontentrecommendations:recommendation_id",
        "distributingcompanies:distributing_company_id", "episode:series_id, season_number, episode_number, content_id",
        "genres:genre_id", "languages:language_id", "movie:movie_id", "notification_reasons:reason_id",
        "notifications:notification_id", "payments:payment_id", "productioncompanies:production_company_id",
        "season:series_id, season_number", "series:series_id", "subscriptionplan:subscription_plan_id",
        "subtitle:subtitle_id", "user:user_id", "watchhistory:1"
    };

    private readonly InfrastructureFixture _infrastructure;

    private readonly MySqlContainer _mysqlContainer = new MySqlBuilder()
        .WithImage("mysql:8.0")
        .WithUsername("root")
        .WithPassword("equivalence")
        .WithCommand("--default-time-zone=+00:00")
        .Build();

    private MySqlConnection _mysql = null!;
    private NpgsqlConnection _postgres = null!;
    private NpgsqlDataSource _postgresSource = null!;

    public StreamingSchemaEquivalenceTests(InfrastructureFixture infrastructure)
    {
        _infrastructure = infrastructure;
    }

    public async Task InitializeAsync()
    {
        await _mysqlContainer.StartAsync();

        var loaded = await _mysqlContainer.ExecScriptAsync(Resource("Original.create_insert.sql"));

        Assert.True(loaded.ExitCode == 0, "The original dump did not load into MySQL: " + loaded.Stderr);

        _mysql = new MySqlConnection(new MySqlConnectionStringBuilder(_mysqlContainer.GetConnectionString())
        {
            Database = "streamingservice"
        }.ConnectionString);

        await _mysql.OpenAsync();

        var database = "equivalence_" + Guid.NewGuid().ToString("N");

        await using (var admin = new NpgsqlConnection(_infrastructure.PostgresConnectionString))
        {
            await admin.OpenAsync();

            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", admin);

            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(_infrastructure.PostgresConnectionString)
        {
            Database = database
        }.ConnectionString;

        StreamingSchema.Apply(connectionString, NullLogger.Instance);

        _postgresSource = NpgsqlDataSource.Create(CatalogServices.WithSearchPath(connectionString));
        _postgres = await _postgresSource.OpenConnectionAsync();
    }

    public async Task DisposeAsync()
    {
        await _mysql.DisposeAsync();
        await _postgres.DisposeAsync();
        await _postgresSource.DisposeAsync();
        await _mysqlContainer.DisposeAsync();
    }

    [Fact]
    public async Task TheTranslation_BehavesLikeTheOriginal()
    {
        var differences = new List<string>();

        foreach (var entry in Tables)
        {
            var parts = entry.Split(':');

            await CompareAsync(differences, "table " + parts[0],
                $"SELECT * FROM `{parts[0]}` ORDER BY {parts[1]}",
                $"SELECT * FROM \"{parts[0]}\" ORDER BY {parts[1]}");
        }

        await CompareAsync(differences, "table usercontentinteraction",
            "SELECT content_id, user_id, liked + 0, rating FROM usercontentinteraction ORDER BY content_id, user_id",
            "SELECT content_id, user_id, liked::int, rating FROM usercontentinteraction ORDER BY content_id, user_id");

        await CompareAsync(differences, "calculatesimilarity for every pair",
            "SELECT a.content_id, b.content_id, calculatesimilarity(a.content_id, b.content_id) FROM content a, content b ORDER BY 1, 2");

        await CompareAsync(differences, "checkactivesubscription for every user",
            "SELECT user_id, checkactivesubscription(user_id) + 0 FROM user ORDER BY 1",
            "SELECT user_id, checkactivesubscription(user_id)::int FROM \"user\" ORDER BY 1");

        await CompareAsync(differences, "checkactivesubscription for an unknown user and NULL",
            "SELECT checkactivesubscription(999999) + 0, checkactivesubscription(NULL) + 0",
            "SELECT checkactivesubscription(999999)::int, checkactivesubscription(NULL)::int");

        await CompareAsync(differences, "calculatealltimespending for every user",
            "SELECT user_id, calculatealltimespending(user_id) FROM user ORDER BY 1",
            "SELECT user_id, calculatealltimespending(user_id) FROM \"user\" ORDER BY 1");

        await CompareAsync(differences, "calculatealltimespending for an unknown user",
            "SELECT calculatealltimespending(999999)");

        await CompareRankedAsync(differences, "view popularcontent",
            "SELECT content_id, title, totalviews FROM popularcontent",
            row => Number(row[2]));

        await CompareRankedAsync(differences, "view activeusers",
            "SELECT user_id, full_name, total_views, total_interactions, comments_left FROM activeusers",
            row => row.Skip(2).Any(cell => Number(cell) == 0)
                ? double.NegativeInfinity
                : Number(row[2]) + Number(row[3]) + Number(row[4]));

        await CompareRankedAsync(differences, "view engagingcontent",
            "SELECT content_id, title, total_views, total_interactions, comments_left, engagement_ratio FROM engagingcontent",
            row => Number(row[5]));

        await CompareAsync(differences, "view engagingcontent, order of ratios", "SELECT engagement_ratio FROM engagingcontent");

        const string countsAreReal =
            "SELECT COUNT(*) FROM popularcontent pc " +
            "WHERE pc.totalviews <> (SELECT COUNT(w.user_id) FROM watchhistory w WHERE w.content_id = pc.content_id)";

        await CompareAsync(differences, "view popularcontent, counts are real", countsAreReal);

        var original = SplitQueries(Resource("Original.queries.sql"));
        var translated = InsightsReader.Parse(TranslatedQueries()).Select(q => q.Sql).ToList();

        Assert.Equal(10, original.Count);
        Assert.Equal(10, translated.Count);

        foreach (var number in new[] { 2, 3, 9 })
        {
            await CompareAsync(differences, $"query {number}", original[number - 1], translated[number - 1], ordered: false);
        }

        foreach (var number in new[] { 7, 8 })
        {
            await CompareAsync(differences, $"query {number}", original[number - 1], translated[number - 1]);
        }

        await CompareAsync(differences, "query 4, ranked values",
            $"SELECT interactions, ranking FROM ({original[3]}) q",
            $"SELECT interactions, ranking FROM ({translated[3]}) q", ordered: false);

        await CompareAsync(differences, "query 5, ranked values",
            $"SELECT alltimespending FROM ({original[4]}) q",
            $"SELECT alltimespending FROM ({translated[4]}) q", ordered: false);

        foreach (var number in new[] { 1, 6, 10 })
        {
            var mysqlRows = await RowsAsync(_mysql, original[number - 1]);
            var postgresRows = await RowsAsync(_postgres, translated[number - 1]);

            if (mysqlRows.Count != postgresRows.Count)
            {
                differences.Add($"query {number}: {mysqlRows.Count} rows in MySQL, {postgresRows.Count} in Postgres");
            }
        }

        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences));
    }

    [Fact]
    public async Task TheTriggers_ReactTheSameWay()
    {
        var differences = new List<string>();

        await ExecuteBothAsync("INSERT INTO watchhistory (content_id, user_id, date) VALUES (3, 21, '2026-10-13 10:00:00')");

        await CompareAsync(differences, "after_watch_completion: similarity of the ten recommendations",
            "SELECT calculatesimilarity(3, content_id), recommendation_reason FROM currentcontentrecommendations WHERE user_id = 21",
            ordered: false);

        await CompareAsync(differences, "after_watch_completion: stamped with today's date",
            "SELECT COUNT(*) FROM currentcontentrecommendations WHERE user_id = 21 AND recommendation_date = CURDATE()",
            "SELECT COUNT(*) FROM currentcontentrecommendations WHERE user_id = 21 AND recommendation_date = CURRENT_DATE");

        await CompareAsync(differences, "watchhistory: next generated id",
            "SELECT MAX(`watched-content_id`) FROM watchhistory",
            "SELECT MAX(\"watched-content_id\") FROM watchhistory");

        await ExecuteAsync(_mysql, "INSERT INTO usercontentinteraction (content_id, user_id, liked, rating) VALUES (5, 21, 1, 7.5)");
        await ExecuteAsync(_postgres, "INSERT INTO usercontentinteraction (content_id, user_id, liked, rating) VALUES (5, 21, B'1', 7.5)");

        await CompareAsync(differences, "update_content_rating_after_feedback",
            "SELECT content_id, average_rating FROM content ORDER BY content_id");

        await ExecuteBothAsync(
            "INSERT INTO content (description, title, duration, release_date, production_company_id) " +
            "SELECT NULL, CONCAT('Filler ', genre_id), 1, '2020-01-01', 1 FROM genres ORDER BY genre_id LIMIT 30");

        var watched = (await RowsAsync(_mysql, "SELECT DISTINCT content_id FROM watchhistory WHERE user_id = 22"))
            .Select(row => Convert.ToInt32(row[0]))
            .ToList();

        var unwatched = (await RowsAsync(_mysql, "SELECT content_id FROM content ORDER BY content_id"))
            .Select(row => Convert.ToInt32(row[0]))
            .Where(id => !watched.Contains(id))
            .ToList();

        Assert.True(watched.Count < 50 && watched.Count + unwatched.Count > 50);

        foreach (var contentId in unwatched.Take(50 - watched.Count))
        {
            await ExecuteBothAsync(
                $"INSERT INTO watchhistory (content_id, user_id, date) VALUES ({contentId}, 22, '2026-10-13 11:00:00')");
        }

        await ExecuteBothAsync(
            $"INSERT INTO watchhistory (content_id, user_id, date) VALUES ({watched[0]}, 22, '2026-10-13 11:05:00')");

        await ExecuteBothAsync(
            $"INSERT INTO watchhistory (content_id, user_id, date) VALUES ({unwatched[50 - watched.Count]}, 22, '2026-10-13 11:10:00')");

        await CompareAsync(differences, "after_user_watch_count_update: milestones reached",
            "SELECT user_id, reason_id FROM notifications ORDER BY notification_id");

        await CompareAsync(differences, "distinct titles watched by user 22",
            "SELECT COUNT(DISTINCT content_id) FROM watchhistory WHERE user_id = 22");

        var milestones = await RowsAsync(_postgres, "SELECT reason_id FROM notifications WHERE user_id = 22");

        Assert.Equal(2, milestones.Count);

        await ExecuteBothAsync("INSERT INTO genres (name) VALUES ('Equivalence')");

        await CompareAsync(differences, "genres: next generated id", "SELECT MAX(genre_id) FROM genres");

        const string duplicateEmail =
            "(first_name, last_name, dob, email, password, gender, subscription_plan_id, subscription_start_date, subscription_end_date) " +
            "VALUES ('X', 'Y', '1990-01-01', 'JOHN.DOE@GMAIL.COM', 'p', 'Male', 4, '2024-01-01', '2024-12-31')";

        Assert.True(await FailsAsync(_mysql, "INSERT INTO user " + duplicateEmail), "MySQL accepted a duplicate e-mail in another case.");
        Assert.True(await FailsAsync(_postgres, "INSERT INTO \"user\" " + duplicateEmail), "Postgres accepted a duplicate e-mail in another case.");

        Assert.True(await FailsAsync(_mysql, "DELETE FROM user WHERE user_id = 21"), "MySQL deleted a user who has comments.");
        Assert.True(await FailsAsync(_postgres, "DELETE FROM \"user\" WHERE user_id = 21"), "Postgres deleted a user who has comments.");

        await ExecuteBothAsync("DELETE FROM actor WHERE actor_id = 21");

        await CompareAsync(differences, "deleting an actor cascades to contentactor",
            "SELECT content_id, actor_id FROM contentactor ORDER BY 1, 2");

        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences));
    }

    private async Task CompareAsync(
        List<string> differences, string name, string mysqlSql, string? postgresSql = null, bool ordered = true)
    {
        var expected = await RowsAsync(_mysql, mysqlSql);
        var actual = await RowsAsync(_postgres, postgresSql ?? mysqlSql);

        if (!ordered)
        {
            expected = expected.OrderBy(Key, StringComparer.Ordinal).ToList();
            actual = actual.OrderBy(Key, StringComparer.Ordinal).ToList();
        }

        if (expected.Count != actual.Count)
        {
            differences.Add($"{name}: {expected.Count} rows in MySQL, {actual.Count} in Postgres");

            return;
        }

        for (var i = 0; i < expected.Count; i++)
        {
            if (!SameRow(expected[i], actual[i]))
            {
                differences.Add($"{name}: row {i} is [{Key(expected[i])}] in MySQL and [{Key(actual[i])}] in Postgres");

                return;
            }
        }
    }

    private async Task CompareRankedAsync(
        List<string> differences, string name, string sql, Func<object?[], double> score)
    {
        var expected = await RowsAsync(_mysql, sql);
        var actual = await RowsAsync(_postgres, sql);

        if (expected.Count != actual.Count)
        {
            differences.Add($"{name}: {expected.Count} rows in MySQL, {actual.Count} in Postgres");

            return;
        }

        var expectedScores = expected.Select(score).OrderByDescending(value => value).ToList();
        var actualScores = actual.Select(score).OrderByDescending(value => value).ToList();

        if (!expectedScores.SequenceEqual(actualScores))
        {
            differences.Add(
                $"{name}: ranked values are [{string.Join(", ", expectedScores)}] in MySQL and [{string.Join(", ", actualScores)}] in Postgres");

            return;
        }

        if (expected.Count == 0)
        {
            return;
        }

        var cutOff = expectedScores[^1];
        var expectedById = expected.ToDictionary(row => Number(row[0]));
        var actualById = actual.ToDictionary(row => Number(row[0]));

        foreach (var (id, row) in expectedById)
        {
            if (actualById.TryGetValue(id, out var other))
            {
                if (!SameRow(row, other))
                {
                    differences.Add($"{name}: [{Key(row)}] in MySQL and [{Key(other)}] in Postgres");
                }
            }
            else if (score(row) != cutOff)
            {
                differences.Add($"{name}: [{Key(row)}] ranks above the last place in MySQL and is missing in Postgres");
            }
        }

        foreach (var (id, row) in actualById)
        {
            if (!expectedById.ContainsKey(id) && score(row) != cutOff)
            {
                differences.Add($"{name}: [{Key(row)}] ranks above the last place in Postgres and is missing in MySQL");
            }
        }
    }

    private static double Number(object? cell) => cell is double number ? number : double.NegativeInfinity;

    private async Task ExecuteBothAsync(string sql)
    {
        await ExecuteAsync(_mysql, sql);
        await ExecuteAsync(_postgres, sql);
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();

        command.CommandText = sql;

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> FailsAsync(DbConnection connection, string sql)
    {
        try
        {
            await ExecuteAsync(connection, sql);

            return false;
        }
        catch (DbException)
        {
            return true;
        }
    }

    private static async Task<List<object?[]>> RowsAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();

        command.CommandText = sql;

        var rows = new List<object?[]>();

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];

            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : Normalize(reader.GetValue(i));
            }

            rows.Add(row);
        }

        return rows;
    }

    private static object Normalize(object value) => value switch
    {
        bool flag => flag ? 1d : 0d,
        BitArray bits => bits.Count > 0 && bits[0] ? 1d : 0d,
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            Convert.ToDouble(value, CultureInfo.InvariantCulture),
        DateOnly date => date.ToDateTime(TimeOnly.MinValue),
        MySqlDateTime date => date.GetDateTime(),
        _ => value
    };

    private static bool SameRow(object?[] left, object?[] right) =>
        left.Length == right.Length && left.Zip(right).All(pair => SameCell(pair.First, pair.Second));

    private static bool SameCell(object? left, object? right) => (left, right) switch
    {
        (null, null) => true,
        (double a, double b) => Math.Abs(a - b) <= 1e-4 * Math.Max(1d, Math.Max(Math.Abs(a), Math.Abs(b))),
        (DateTime a, DateTime b) => Math.Abs((a - b).TotalSeconds) <= 5,
        (string a, string b) => a == b,
        _ => Equals(left, right)
    };

    private static string Key(object?[] row) => string.Join(" | ", row.Select(cell => cell switch
    {
        null => "NULL",
        double number => Math.Round(number, 3).ToString("F3", CultureInfo.InvariantCulture),
        DateTime date => date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        _ => cell.ToString()
    }));

    private static List<string> SplitQueries(string text) =>
        text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length > 0)
            .ToList();

    private static string TranslatedQueries()
    {
        using var stream = typeof(InsightsReader).Assembly.GetManifestResourceStream(InsightsReader.QueriesResource)!;
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    private static string Resource(string name)
    {
        using var stream = typeof(StreamingSchemaEquivalenceTests).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Embedded resource {name} is missing.");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
