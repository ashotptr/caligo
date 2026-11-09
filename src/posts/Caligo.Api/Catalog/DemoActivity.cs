using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Application.Accounts;
using Caligo.Catalog.Application.Contents;
using Caligo.Catalog.Infrastructure.Persistence;
using Caligo.Posts.Infrastructure.Identity;
using Caligo.SharedKernel;

namespace Caligo.Api.Catalog;

public record DemoSeedSummary(
    int People,
    int Created,
    int AlreadyThere,
    int Failed,
    int Watches,
    int Ratings,
    int Comments,
    int Collections,
    int TitlesAdded,
    int TitlesMissing,
    int StepsLater)
{
    public override string ToString() =>
        $"Demo people: {People}; created: {Created}; already there: {AlreadyThere}; failed: {Failed}; " +
        $"watches: {Watches}; ratings: {Ratings}; comments: {Comments}; collections: {Collections}; " +
        $"titles added to collections: {TitlesAdded}; titles not in the catalog: {TitlesMissing}; " +
        $"steps left for later: {StepsLater}";
}

public record DemoAdvanceSummary(int Steps, int Watches, int Ratings, int Comments, int Collections, int TitlesAdded, DateTime Until)
{
    public override string ToString() =>
        $"Demo steps applied: {Steps} (watches: {Watches}; ratings: {Ratings}; comments: {Comments}; " +
        $"collections: {Collections}; titles added: {TitlesAdded}) up to {Until:yyyy-MM-dd HH:mm} UTC";
}

public record DemoUnseedSummary(int People, int Watches, int Ratings, int Comments, int Collections, int Averages)
{
    public override string ToString() =>
        $"Demo people removed: {People}; watches: {Watches}; ratings: {Ratings}; comments: {Comments}; " +
        $"collections: {Collections}; rating averages restored: {Averages}";
}

public record DemoPlan(int Version, List<DemoTitle> Titles, List<DemoPerson> Users);

public record DemoTitle(string Kind, string? Title, int? Year, string? Series, int? Season, int? Episode, string? Uri);

public record DemoPerson(
    string UserName,
    string Email,
    string FirstName,
    string LastName,
    DateOnly Dob,
    string Gender,
    string Joined,
    List<JsonElement[]> Watches,
    List<JsonElement[]> Ratings,
    List<JsonElement[]> Comments,
    List<DemoShelf> Collections);

public record DemoShelf(string Name, string Created, List<JsonElement[]> Titles);

public record DemoReceipt(List<string> Users, List<DemoAverage> Averages, DateTime? AppliedUntil = null);

public record DemoAverage(int ContentId, float? Before, float? After, bool Changed = false);

public record DemoScheduled(string UserName, DateTime At, string Kind);

public class DemoActivity
{
    public const string SeedCommand = "seed-demo";
    public const string AdvanceCommand = "advance-demo";
    public const string UnseedCommand = "unseed-demo";
    public const string EmailDomain = "@demo.invalid";
    public const string ReceiptName = "seeded.json";

    private static readonly string[] Commands = [SeedCommand, AdvanceCommand, UnseedCommand];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    private readonly UserManager<ApplicationUser> _users;
    private readonly IStreamingAccounts _accounts;
    private readonly IBlogAccounts _blog;
    private readonly IActivityStore _activity;
    private readonly ICollectionStore _collections;
    private readonly StreamingDbContext _db;

    public DemoActivity(
        UserManager<ApplicationUser> users,
        IStreamingAccounts accounts,
        IBlogAccounts blog,
        IActivityStore activity,
        ICollectionStore collections,
        StreamingDbContext db)
    {
        _users = users;
        _accounts = accounts;
        _blog = blog;
        _activity = activity;
        _collections = collections;
        _db = db;
    }

    public static bool Requested(string[] args) => Commands.Any(args.Contains);

    public static string ReceiptFor(string planPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(planPath))!, ReceiptName);

    public static bool IsDue(DateTime at, DateTime? after, DateTime until) => (after is null || at > after.Value) && at <= until;

    public static IReadOnlyList<DemoScheduled> Schedule(DemoPlan plan)
    {
        var titles = plan.Titles.Select(_ => (int?)0).ToList();

        return plan.Users
            .SelectMany(person => StepsOf(person, string.Empty, 0, titles)
                .Select(step => new DemoScheduled(person.UserName, step.At, step.Kind.ToString())))
            .OrderBy(step => step.At)
            .ToList();
    }

    public async Task<object> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var command = Commands.First(args.Contains);
        var position = Array.IndexOf(args, command);
        var path = position + 1 < args.Length ? args[position + 1] : null;

        return command switch
        {
            SeedCommand => await SeedAsync(path ?? throw Usage(command), cancellationToken),
            AdvanceCommand => await AdvanceAsync(
                path ?? throw Usage(command),
                position + 2 < args.Length ? Moment(args[position + 2]) : Now(),
                cancellationToken),
            _ => await UnseedAsync(path, cancellationToken)
        };
    }

    public async Task<DemoSeedSummary> SeedAsync(string planPath, CancellationToken cancellationToken = default)
    {
        var plan = ParsePlan(await File.ReadAllTextAsync(planPath, cancellationToken));
        var contents = await ResolveAsync(plan.Titles, cancellationToken);
        var steps = new List<DemoStep>();
        var seeded = new List<string>();
        int created = 0, alreadyThere = 0, failed = 0;

        foreach (var person in plan.Users)
        {
            var existing = await _users.FindByNameAsync(person.UserName) ?? await _users.FindByEmailAsync(person.Email);

            if (existing is not null)
            {
                if (IsDemo(existing.Email))
                {
                    alreadyThere++;
                }
                else
                {
                    failed++;
                }

                continue;
            }

            if (!IsDemo(person.Email))
            {
                failed++;

                continue;
            }

            var user = new ApplicationUser { UserName = person.UserName, Email = person.Email, EmailConfirmed = true };

            if (!(await _users.CreateAsync(user)).Succeeded)
            {
                failed++;

                continue;
            }

            seeded.Add(user.Id);
            await _users.AddToRoleAsync(user, "Reader");

            var joined = Moment(person.Joined);
            var profiles = new StreamingAccountService(_accounts, _blog, new FixedTime(joined));
            var profile = await profiles.CreateProfileAsync(
                new Caller(user.Id, false),
                new CreateProfileCommand(person.FirstName, person.LastName, person.Dob, person.Gender),
                cancellationToken);

            if (!profile.IsSuccess || await _accounts.FindUserIdAsync(user.Id, cancellationToken) is not { } userId)
            {
                failed++;

                continue;
            }

            created++;
            steps.AddRange(StepsOf(person, user.Id, userId, contents));
        }

        var until = Now();
        var due = Ordered(steps.Where(step => IsDue(step.At, null, until)));
        var (counts, averages) = await ApplyAsync(due, new Dictionary<string, int>(), new Dictionary<int, DemoAverage>(), cancellationToken);

        if (seeded.Count > 0)
        {
            await WriteReceiptAsync(ReceiptFor(planPath), seeded, averages, until, cancellationToken);
        }

        return new DemoSeedSummary(
            plan.Users.Count, created, alreadyThere, failed, counts.Watches, counts.Ratings, counts.Comments,
            counts.Collections, counts.Added, contents.Count(id => id is null), steps.Count - due.Count);
    }

    public async Task<DemoAdvanceSummary> AdvanceAsync(
        string planPath, DateTime until, CancellationToken cancellationToken = default)
    {
        var receiptPath = ReceiptFor(planPath);

        if (!File.Exists(planPath) || await ReadReceiptAsync(receiptPath, cancellationToken) is not { } receipt)
        {
            return new DemoAdvanceSummary(0, 0, 0, 0, 0, 0, until);
        }

        var plan = ParsePlan(await File.ReadAllTextAsync(planPath, cancellationToken));
        var titles = plan.Titles.Select(_ => (int?)0).ToList();
        var ours = receipt.Users.ToHashSet();
        IReadOnlyList<int?>? contents = null;
        var steps = new List<DemoStep>();
        var shelves = new Dictionary<string, int>();

        foreach (var person in plan.Users)
        {
            if (!StepsOf(person, string.Empty, 0, titles).Any(step => IsDue(step.At, receipt.AppliedUntil, until)) ||
                await _users.FindByNameAsync(person.UserName) is not { } user ||
                !ours.Contains(user.Id) ||
                await _accounts.FindUserIdAsync(user.Id, cancellationToken) is not { } userId)
            {
                continue;
            }

            contents ??= await ResolveAsync(plan.Titles, cancellationToken);
            steps.AddRange(StepsOf(person, user.Id, userId, contents)
                .Where(step => IsDue(step.At, receipt.AppliedUntil, until)));

            if (person.Collections.Count > 0)
            {
                foreach (var owned in await _collections.ListAsync(user.Id, HiddenMusic.None, cancellationToken))
                {
                    shelves[$"{user.Id}/{owned.Name}"] = owned.CollectionId;
                }
            }
        }

        var due = Ordered(steps);
        var (counts, averages) = await ApplyAsync(
            due, shelves, receipt.Averages.ToDictionary(a => a.ContentId), cancellationToken);

        await SaveReceiptAsync(receiptPath, receipt with { Averages = averages, AppliedUntil = until }, cancellationToken);

        return new DemoAdvanceSummary(
            due.Count, counts.Watches, counts.Ratings, counts.Comments, counts.Collections, counts.Added, until);
    }

    public static DemoPlan ParsePlan(string json)
    {
        var plan = JsonSerializer.Deserialize<DemoPlan>(json, Json)
            ?? throw new InvalidOperationException("The demo plan is empty.");

        return plan.Version == 1
            ? plan
            : throw new InvalidOperationException($"Demo plan version {plan.Version} is not supported.");
    }

    public async Task<DemoUnseedSummary> UnseedAsync(string? receiptPath, CancellationToken cancellationToken = default)
    {
        var domain = EmailDomain.ToUpperInvariant();
        var people = await _users.Users
            .Where(u => u.NormalizedEmail != null && u.NormalizedEmail.EndsWith(domain))
            .ToListAsync(cancellationToken);
        var identityIds = people.Select(u => u.Id).ToList();
        var userIds = await _db.BlogUsers.AsNoTracking()
            .Where(b => identityIds.Contains(b.IdentityId))
            .Select(b => b.UserId)
            .ToListAsync(cancellationToken);
        var touched = await _db.Interactions.AsNoTracking()
            .Where(i => userIds.Contains(i.UserId))
            .Select(i => i.ContentId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var receipt = receiptPath is null ? null : await ReadReceiptAsync(receiptPath, cancellationToken);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        await _db.Notifications.Where(n => userIds.Contains(n.UserId)).ExecuteDeleteAsync(cancellationToken);
        await _db.Recommendations.Where(r => userIds.Contains(r.UserId)).ExecuteDeleteAsync(cancellationToken);
        var comments = await _db.Comments.Where(c => userIds.Contains(c.UserId)).ExecuteDeleteAsync(cancellationToken);
        var ratings = await _db.Interactions.Where(i => userIds.Contains(i.UserId)).ExecuteDeleteAsync(cancellationToken);
        await _db.PlaybackPositions.Where(p => userIds.Contains(p.UserId)).ExecuteDeleteAsync(cancellationToken);
        await _db.PlaybackSessions.Where(s => userIds.Contains(s.UserId)).ExecuteDeleteAsync(cancellationToken);
        var watches = await _db.WatchHistory.Where(w => userIds.Contains(w.UserId)).ExecuteDeleteAsync(cancellationToken);
        await _db.Payments.Where(p => userIds.Contains(p.UserId)).ExecuteDeleteAsync(cancellationToken);
        await _db.BlogUsers.Where(b => userIds.Contains(b.UserId)).ExecuteDeleteAsync(cancellationToken);
        await _db.Users.Where(u => userIds.Contains(u.UserId)).ExecuteDeleteAsync(cancellationToken);
        var collections = await _db.Collections
            .Where(c => identityIds.Contains(c.IdentityId))
            .ExecuteDeleteAsync(cancellationToken);
        var averages = await RestoreAveragesAsync(touched, receipt, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        foreach (var person in people)
        {
            await _users.DeleteAsync(person);
        }

        if (receipt is not null)
        {
            File.Delete(receiptPath!);
        }

        return new DemoUnseedSummary(people.Count, watches, ratings, comments, collections, averages);
    }

    private async Task<(DemoCounts Counts, List<DemoAverage> Averages)> ApplyAsync(
        IReadOnlyList<DemoStep> ordered,
        Dictionary<string, int> shelves,
        IReadOnlyDictionary<int, DemoAverage> known,
        CancellationToken cancellationToken)
    {
        var rated = ordered.Where(step => step.Kind == DemoStepKind.Rate).Select(step => step.ContentId).Distinct().ToList();
        var before = await AveragesAsync(rated, cancellationToken);
        var counts = new DemoCounts();
        var done = 0;

        foreach (var step in ordered)
        {
            switch (step.Kind)
            {
                case DemoStepKind.Shelve:
                    if ((await _collections.CreateAsync(step.IdentityId, step.Text!, step.At, cancellationToken))
                        ?.CollectionId is { } collectionId)
                    {
                        shelves[step.Shelf!] = collectionId;
                        counts.Collections++;
                    }

                    break;
                case DemoStepKind.Watch:
                    await _activity.LogWatchAsync(step.ContentId, step.UserId, step.Position, step.At, cancellationToken);
                    counts.Watches++;

                    break;
                case DemoStepKind.Rate:
                    await _activity.SetInteractionAsync(step.ContentId, step.UserId, step.Liked, step.Rating, cancellationToken);
                    counts.Ratings++;

                    break;
                case DemoStepKind.Comment:
                    await _activity.AddCommentAsync(step.ContentId, step.UserId, step.Text!, cancellationToken);
                    counts.Comments++;

                    break;
                case DemoStepKind.File:
                    if (shelves.TryGetValue(step.Shelf!, out var shelf))
                    {
                        counts.Added += (await _collections.AddAsync(shelf, [step.ContentId], step.At, cancellationToken)).Added;
                    }

                    break;
            }

            _db.ChangeTracker.Clear();

            if (++done % 500 == 0)
            {
                Console.WriteLine($"Demo activity: {done} of {ordered.Count}");
            }
        }

        var after = await AveragesAsync(rated, cancellationToken);
        var averages = known.Values.Where(average => !rated.Contains(average.ContentId)).ToList();

        foreach (var contentId in rated)
        {
            var earlier = before.GetValueOrDefault(contentId);

            averages.Add(known.TryGetValue(contentId, out var old)
                ? old with { After = after.GetValueOrDefault(contentId), Changed = old.Changed || !Same(earlier, old.After) }
                : new DemoAverage(contentId, earlier, after.GetValueOrDefault(contentId)));
        }

        return (counts, averages);
    }

    private static List<DemoStep> Ordered(IEnumerable<DemoStep> steps) =>
        steps.OrderBy(step => step.At).ThenBy(step => step.Kind).ToList();

    private static IEnumerable<DemoStep> StepsOf(
        DemoPerson person, string identityId, int userId, IReadOnlyList<int?> contents)
    {
        foreach (var watch in person.Watches)
        {
            if (contents[watch[0].GetInt32()] is { } contentId)
            {
                yield return new DemoStep(Moment(watch[1].GetString()!), DemoStepKind.Watch, identityId, userId, contentId)
                {
                    Position = watch[2].ValueKind == JsonValueKind.Number ? watch[2].GetSingle() : null
                };
            }
        }

        foreach (var rating in person.Ratings)
        {
            if (contents[rating[0].GetInt32()] is { } contentId)
            {
                yield return new DemoStep(Moment(rating[3].GetString()!), DemoStepKind.Rate, identityId, userId, contentId)
                {
                    Liked = rating[1].ValueKind == JsonValueKind.True ? true : null,
                    Rating = rating[2].ValueKind == JsonValueKind.Number ? rating[2].GetSingle() : null
                };
            }
        }

        foreach (var comment in person.Comments)
        {
            if (contents[comment[0].GetInt32()] is { } contentId)
            {
                yield return new DemoStep(Moment(comment[2].GetString()!), DemoStepKind.Comment, identityId, userId, contentId)
                {
                    Text = comment[1].GetString()
                };
            }
        }

        foreach (var shelf in person.Collections)
        {
            var key = $"{identityId}/{shelf.Name}";

            yield return new DemoStep(Moment(shelf.Created), DemoStepKind.Shelve, identityId, userId, 0)
            {
                Text = shelf.Name,
                Shelf = key
            };

            foreach (var title in shelf.Titles)
            {
                if (contents[title[0].GetInt32()] is { } contentId)
                {
                    yield return new DemoStep(Moment(title[1].GetString()!), DemoStepKind.File, identityId, userId, contentId)
                    {
                        Shelf = key
                    };
                }
            }
        }
    }

    private async Task<IReadOnlyList<int?>> ResolveAsync(IReadOnlyList<DemoTitle> titles, CancellationToken cancellationToken)
    {
        var films = await (
            from m in _db.Movies.AsNoTracking()
            join c in _db.Contents.AsNoTracking() on m.ContentId equals c.ContentId
            select new { c.ContentId, c.Title, c.ReleaseDate }).ToListAsync(cancellationToken);
        var episodes = await (
            from e in _db.Episodes.AsNoTracking()
            join s in _db.Series.AsNoTracking() on e.SeriesId equals s.SeriesId
            select new { e.ContentId, s.Title, e.SeasonNumber, e.EpisodeNumber }).ToListAsync(cancellationToken);
        var songs = (await _db.Songs.AsNoTracking()
                .Where(s => s.AudioUri != null)
                .OrderBy(s => s.SongId)
                .Select(s => new { s.ContentId, s.AudioUri })
                .ToListAsync(cancellationToken))
            .GroupBy(s => s.AudioUri!)
            .ToDictionary(g => g.Key, g => g.First().ContentId);

        return titles.Select(title => title.Kind switch
        {
            "film" => films
                .Where(f => f.Title == title.Title && (title.Year is null || f.ReleaseDate.Year == title.Year))
                .Select(f => (int?)f.ContentId)
                .FirstOrDefault(),
            "episode" => episodes
                .Where(e => e.Title == title.Series && e.SeasonNumber == title.Season && e.EpisodeNumber == title.Episode)
                .Select(e => (int?)e.ContentId)
                .FirstOrDefault(),
            "song" => title.Uri is not null && songs.TryGetValue(title.Uri, out var contentId) ? contentId : null,
            _ => null
        }).ToList();
    }

    private async Task<Dictionary<int, float?>> AveragesAsync(IReadOnlyCollection<int> contentIds, CancellationToken cancellationToken) =>
        await _db.Contents.AsNoTracking()
            .Where(c => contentIds.Contains(c.ContentId))
            .ToDictionaryAsync(c => c.ContentId, c => c.AverageRating, cancellationToken);

    private async Task<int> RestoreAveragesAsync(
        IReadOnlyCollection<int> touched, DemoReceipt? receipt, CancellationToken cancellationToken)
    {
        var saved = receipt?.Averages.ToDictionary(a => a.ContentId) ?? new Dictionary<int, DemoAverage>();
        var current = await AveragesAsync(touched, cancellationToken);
        var remaining = await _db.Interactions.AsNoTracking()
            .Where(i => touched.Contains(i.ContentId))
            .GroupBy(i => i.ContentId)
            .Select(g => new { ContentId = g.Key, Average = g.Average(i => i.Rating) })
            .ToDictionaryAsync(r => r.ContentId, r => r.Average, cancellationToken);

        foreach (var contentId in touched)
        {
            var known = saved.GetValueOrDefault(contentId);
            float? value = known is not null && !known.Changed && Same(current.GetValueOrDefault(contentId), known.After)
                ? known.Before
                : remaining.TryGetValue(contentId, out var average) ? average : known?.Before ?? 0f;

            await _db.Contents
                .Where(c => c.ContentId == contentId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.AverageRating, value), cancellationToken);
        }

        return touched.Count;
    }

    private static async Task<DemoReceipt?> ReadReceiptAsync(string path, CancellationToken cancellationToken) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<DemoReceipt>(await File.ReadAllTextAsync(path, cancellationToken), Json)
            : null;

    private static async Task SaveReceiptAsync(string path, DemoReceipt receipt, CancellationToken cancellationToken)
    {
        var partial = path + ".part";

        await File.WriteAllTextAsync(partial, JsonSerializer.Serialize(receipt, Json), cancellationToken);
        File.Move(partial, path, overwrite: true);
    }

    private static async Task WriteReceiptAsync(
        string path, List<string> seeded, List<DemoAverage> averages, DateTime until, CancellationToken cancellationToken)
    {
        var earlier = await ReadReceiptAsync(path, cancellationToken);
        var first = earlier?.Averages.ToDictionary(a => a.ContentId) ?? new Dictionary<int, DemoAverage>();
        var merged = averages
            .Select(a => first.TryGetValue(a.ContentId, out var old) ? a with { Before = old.Before } : a)
            .Concat(first.Values.Where(old => averages.All(a => a.ContentId != old.ContentId)))
            .ToList();
        var users = (earlier?.Users ?? new List<string>()).Concat(seeded).ToList();

        await SaveReceiptAsync(path, new DemoReceipt(users, merged, until), cancellationToken);
    }

    private static InvalidOperationException Usage(string command) => new($"Usage: {command} <plan.json>");

    private static bool Same(float? left, float? right) =>
        left is null ? right is null : right is not null && Math.Abs(left.Value - right.Value) < 1e-4f;

    private static bool IsDemo(string? email) =>
        email is not null && email.EndsWith(EmailDomain, StringComparison.OrdinalIgnoreCase);

    private static DateTime Now() => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

    private static DateTime Moment(string value) =>
        DateTime.SpecifyKind(
            DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            DateTimeKind.Unspecified);

    private enum DemoStepKind
    {
        Shelve,
        Watch,
        Rate,
        Comment,
        File
    }

    private sealed class DemoCounts
    {
        public int Watches { get; set; }

        public int Ratings { get; set; }

        public int Comments { get; set; }

        public int Collections { get; set; }

        public int Added { get; set; }
    }

    private record DemoStep(DateTime At, DemoStepKind Kind, string IdentityId, int UserId, int ContentId)
    {
        public float? Position { get; init; }

        public bool? Liked { get; init; }

        public float? Rating { get; init; }

        public string? Text { get; init; }

        public string? Shelf { get; init; }
    }

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }
}
