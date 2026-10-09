using System.Text.Json;
using AHNSF.Music.Core;
using Microsoft.Data.Sqlite;

namespace AHNSF.Music.Infrastructure;

public sealed class SqliteLibraryRepository(string databasePath) : ILibraryRepository
{
    private readonly SemaphoreSlim migration = new(1, 1);
    private bool migrated;
    public Task InitializeAsync(CancellationToken token = default) => Task.Run(async () =>
    { await using var db = await OpenAsync(token).ConfigureAwait(false); }, token);
    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        try
        {
            await db.OpenAsync(token).ConfigureAwait(false);
            await migration.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!migrated)
                {
                    using var transaction = db.BeginTransaction();
                    using var command = db.CreateCommand(); command.Transaction = transaction;
                    command.CommandText = """
                        CREATE TABLE IF NOT EXISTS LibraryMigrations(Version INTEGER PRIMARY KEY);
                        CREATE TABLE IF NOT EXISTS Tracks(
                            Id TEXT PRIMARY KEY, NormalizedPath TEXT NOT NULL UNIQUE,
                            RootPath TEXT NOT NULL, Title TEXT NOT NULL, Artist TEXT NOT NULL,
                            Album TEXT NOT NULL, FileName TEXT NOT NULL, Duration REAL NOT NULL,
                            IndexedAtUtc TEXT NOT NULL, Availability INTEGER NOT NULL,
                            ArtworkCacheKey TEXT, Payload TEXT NOT NULL);
                        CREATE INDEX IF NOT EXISTS IX_Tracks_Root_Availability ON Tracks(RootPath,Availability);
                        CREATE INDEX IF NOT EXISTS IX_Tracks_Title ON Tracks(RootPath,Title);
                        CREATE INDEX IF NOT EXISTS IX_Tracks_Artist ON Tracks(RootPath,Artist);
                        CREATE INDEX IF NOT EXISTS IX_Tracks_Album ON Tracks(RootPath,Album);
                        INSERT OR IGNORE INTO LibraryMigrations(Version) VALUES(1);
                        """;
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    transaction.Commit(); migrated = true;
                }
            }
            finally { migration.Release(); }
            return db;
        }
        catch { await db.DisposeAsync().ConfigureAwait(false); throw; }
    }
    // SQLite async methods execute synchronously: schedule all database work away from UI.
    public Task<IReadOnlyList<Track>> LoadAsync(string root, CancellationToken token, bool includeMissing = true) => Task.Run(async () =>
    {
        await using var db = await OpenAsync(token).ConfigureAwait(false);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT Payload FROM Tracks WHERE RootPath=$root AND ($includeMissing=1 OR Availability<>$missing)";
        command.Parameters.AddWithValue("$root", LibraryText.NormalizePath(root));
        command.Parameters.AddWithValue("$includeMissing", includeMissing ? 1 : 0);
        command.Parameters.AddWithValue("$missing", (int)AvailabilityStatus.Missing);
        return await ReadAsync(command, token).ConfigureAwait(false);
    }, token);
    private static async Task<IReadOnlyList<Track>> ReadAsync(SqliteCommand command, CancellationToken token)
    {
        var tracks = new List<Track>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        { token.ThrowIfCancellationRequested(); tracks.Add(JsonSerializer.Deserialize<Track>(reader.GetString(0)) ?? throw new InvalidDataException("Registro musical inválido.")); }
        return tracks;
    }
    public Task<LibraryResult> QueryAsync(string root, LibraryQuery query, CancellationToken token) => Task.Run(async () =>
    {
        await using var db = await OpenAsync(token).ConfigureAwait(false);
        using var command = db.CreateCommand();
        var column = query.Sort switch { LibrarySort.Artist => "Artist", LibrarySort.Album => "Album", LibrarySort.Duration => "Duration", LibrarySort.Added => "IndexedAtUtc", _ => "Title" };
        var order = query.Descending ? "DESC" : "ASC";
        command.CommandText = $"SELECT Payload FROM Tracks WHERE RootPath=$root AND Availability<>$missing AND ($artist IS NULL OR Artist=$artist) AND ($album IS NULL OR Album=$album) AND ($search='' OR instr(Title,$search)>0 OR instr(Artist,$search)>0 OR instr(Album,$search)>0 OR instr(FileName,$search)>0) ORDER BY {column} {order}, NormalizedPath ASC";
        command.Parameters.AddWithValue("$root", LibraryText.NormalizePath(root));
        command.Parameters.AddWithValue("$missing", (int)AvailabilityStatus.Missing);
        command.Parameters.AddWithValue("$artist", query.Artist is null ? DBNull.Value : LibraryText.Fold(query.Artist));
        command.Parameters.AddWithValue("$album", query.Album is null ? DBNull.Value : LibraryText.Fold(query.Album));
        command.Parameters.AddWithValue("$search", LibraryText.Fold(query.Search.Trim()));
        var tracks = await ReadAsync(command, token).ConfigureAwait(false);
        using var facets = db.CreateCommand();
        facets.CommandText = """
            SELECT 0, CAST(COUNT(*) AS TEXT) FROM Tracks WHERE RootPath=$root AND Availability<>$missing
            UNION ALL SELECT 1, json_extract(Payload,'$.Artist') FROM Tracks WHERE RootPath=$root AND Availability<>$missing GROUP BY Artist
            UNION ALL SELECT 2, json_extract(Payload,'$.Album') FROM Tracks WHERE RootPath=$root AND Availability<>$missing GROUP BY Album;
            """;
        facets.Parameters.AddWithValue("$root", LibraryText.NormalizePath(root)); facets.Parameters.AddWithValue("$missing", (int)AvailabilityStatus.Missing);
        int count = 0; var artists = new List<string>(); var albums = new List<string>();
        await using var facetReader = await facets.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await facetReader.ReadAsync(token).ConfigureAwait(false))
        {
            if (facetReader.GetInt32(0) == 0) count = int.Parse(facetReader.GetString(1), System.Globalization.CultureInfo.InvariantCulture);
            else if (facetReader.GetInt32(0) == 1) artists.Add(facetReader.GetString(1));
            else albums.Add(facetReader.GetString(1));
        }
        return new LibraryResult(tracks, count, artists.Order(StringComparer.CurrentCultureIgnoreCase).ToArray(), albums.Order(StringComparer.CurrentCultureIgnoreCase).ToArray());
    }, token);
    public Task ApplyAsync(IReadOnlyList<Track> changed, CancellationToken token) => Task.Run(async () =>
    {
        if (changed.Count == 0) return;
        await using var db = await OpenAsync(token).ConfigureAwait(false);
        using var transaction = db.BeginTransaction();
        foreach (var track in changed)
        {
            token.ThrowIfCancellationRequested();
            using var identity = db.CreateCommand(); identity.Transaction = transaction;
            identity.CommandText = "SELECT Payload FROM Tracks WHERE NormalizedPath=$path";
            identity.Parameters.AddWithValue("$path", LibraryText.NormalizePath(track.Path));
            var existingJson = await identity.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            var existing = existingJson is null ? null : JsonSerializer.Deserialize<Track>(existingJson);
            var saved = existing is null ? track : track with { Id = existing.Id, IndexedAtUtc = existing.IndexedAtUtc };
            using var command = db.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Tracks VALUES($id,$path,$root,$title,$artist,$album,$file,$duration,$indexed,$availability,$art,$payload)
                ON CONFLICT(NormalizedPath) DO UPDATE SET RootPath=$root,Title=$title,Artist=$artist,Album=$album,FileName=$file,
                    Duration=$duration,IndexedAtUtc=$indexed,Availability=$availability,ArtworkCacheKey=$art,Payload=$payload;
                """;
            command.Parameters.AddWithValue("$id", saved.Id.ToString()); command.Parameters.AddWithValue("$path", LibraryText.NormalizePath(track.Path));
            command.Parameters.AddWithValue("$root", LibraryText.NormalizePath(track.RootPath));
            command.Parameters.AddWithValue("$title", LibraryText.Fold(track.Title)); command.Parameters.AddWithValue("$artist", LibraryText.Fold(track.Artist));
            command.Parameters.AddWithValue("$album", LibraryText.Fold(track.Album)); command.Parameters.AddWithValue("$file", LibraryText.Fold(track.FileName));
            command.Parameters.AddWithValue("$duration", track.Duration.TotalSeconds); command.Parameters.AddWithValue("$indexed", saved.IndexedAtUtc.ToString("O"));
            command.Parameters.AddWithValue("$availability", (int)track.Availability); command.Parameters.AddWithValue("$art", (object?)track.ArtworkCacheKey ?? DBNull.Value);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(saved));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested(); transaction.Commit();
    }, token);
    public Task<IReadOnlySet<string>> ArtworkKeysAsync(CancellationToken token) => Task.Run(async () =>
    {
        await using var db = await OpenAsync(token).ConfigureAwait(false);
        using var command = db.CreateCommand(); command.CommandText = "SELECT DISTINCT ArtworkCacheKey FROM Tracks WHERE ArtworkCacheKey IS NOT NULL AND Availability<>2";
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(token).ConfigureAwait(false)) keys.Add(reader.GetString(0));
        return (IReadOnlySet<string>)keys;
    }, token);
}
