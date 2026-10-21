using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Caligo.Catalog.Application.Abstractions;
using Caligo.Catalog.Application.Contents;
using Caligo.Catalog.Application.Music;
using Caligo.Catalog.Domain.Collections;
using Caligo.Catalog.Infrastructure;
using Caligo.Catalog.Infrastructure.Engine;
using Caligo.Catalog.Infrastructure.Persistence;
using Caligo.Posts.Infrastructure.Identity;

namespace Caligo.Api.Catalog;

public record CaligoAccountImportSummary(
    int Accounts,
    int Created,
    int AlreadyThere,
    int Failed,
    int Collections,
    int TitlesAdded,
    int TracksNotFound)
{
    public override string ToString() =>
        $"Caligo accounts: {Accounts}; created: {Created}; already there: {AlreadyThere}; failed: {Failed}; " +
        $"collections: {Collections}; titles added: {TitlesAdded}; tracks not in the Thesaurarium: {TracksNotFound}";
}

public class CaligoAccountImport
{
    public const string Command = "import-caligo-accounts";

    private readonly UserManager<ApplicationUser> _users;
    private readonly IPasswordHasher<ApplicationUser> _hasher;
    private readonly ICaligoEngine _engine;
    private readonly MusicImportService _music;
    private readonly ICollectionStore _collections;
    private readonly StreamingDbContext _db;
    private readonly CatalogOptions _options;
    private readonly TimeProvider _time;

    public CaligoAccountImport(
        UserManager<ApplicationUser> users,
        IPasswordHasher<ApplicationUser> hasher,
        ICaligoEngine engine,
        MusicImportService music,
        ICollectionStore collections,
        StreamingDbContext db,
        IOptions<CatalogOptions> options,
        TimeProvider time)
    {
        _users = users;
        _hasher = hasher;
        _engine = engine;
        _music = music;
        _collections = collections;
        _db = db;
        _options = options.Value;
        _time = time;
    }

    public async Task<CaligoAccountImportSummary> RunAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.CaligoDataRoot))
        {
            throw new InvalidOperationException("Catalog:CaligoDataRoot is not set, so Caligo's users.json can't be read.");
        }

        if (_music.IsConfigured)
        {
            await _music.ImportAsync(cancellationToken);
        }

        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(_options.CaligoDataRoot, "users.json"), cancellationToken));

        int accounts = 0, created = 0, alreadyThere = 0, failed = 0, collections = 0, titles = 0, notFound = 0;

        foreach (var account in document.RootElement.EnumerateObject())
        {
            accounts++;

            var password = account.Value.TryGetProperty("password", out var stored) && stored.ValueKind == JsonValueKind.String
                ? stored.GetString()
                : null;

            var user = await _users.FindByNameAsync(account.Name);

            if (user is null)
            {
                if (string.IsNullOrEmpty(password))
                {
                    failed++;

                    continue;
                }

                user = new ApplicationUser { UserName = account.Name };
                user.PasswordHash = _hasher.HashPassword(user, password);

                if (!(await _users.CreateAsync(user)).Succeeded)
                {
                    failed++;

                    continue;
                }

                await _users.AddToRoleAsync(user, "Reader");
                created++;
            }
            else
            {
                alreadyThere++;
            }

            var playlists = await _engine.GetPlaylistsAsync(account.Name, cancellationToken);
            var owned = await _collections.ListAsync(user.Id, HiddenMusic.None, cancellationToken);

            foreach (var (name, tracks) in playlists)
            {
                if (Collection.CleanName(name) is not { } cleaned)
                {
                    continue;
                }

                var collectionId = owned.FirstOrDefault(c => c.Name == cleaned)?.CollectionId
                    ?? (await _collections.CreateAsync(user.Id, cleaned, Now(), cancellationToken))?.CollectionId;

                if (collectionId is not { } id)
                {
                    continue;
                }

                collections++;

                var contentIds = new List<int>();

                foreach (var track in tracks)
                {
                    if (await ContentOfAsync(track.AudioUrl, cancellationToken) is { } contentId)
                    {
                        contentIds.Add(contentId);
                    }
                    else
                    {
                        notFound++;
                    }
                }

                if (contentIds.Count > 0)
                {
                    titles += (await _collections.AddAsync(id, contentIds.Distinct().ToList(), Now(), cancellationToken)).Added;
                }
            }
        }

        return new CaligoAccountImportSummary(accounts, created, alreadyThere, failed, collections, titles, notFound);
    }

    private async Task<int?> ContentOfAsync(string audioUrl, CancellationToken cancellationToken)
    {
        if (MixtapePaths.Split(audioUrl, _options.MixtapeUrlPrefix) is not { } path)
        {
            return null;
        }

        var uri = _options.MixtapeUrlPrefix + MixtapePaths.Segment(path.Folder) + "/" + MixtapePaths.Segment(path.File);

        return await _db.Songs.AsNoTracking()
            .Where(s => s.AudioUri == uri)
            .OrderBy(s => s.SongId)
            .Select(s => (int?)s.ContentId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private DateTime Now() => DateTime.SpecifyKind(_time.GetUtcNow().UtcDateTime, DateTimeKind.Unspecified);
}
