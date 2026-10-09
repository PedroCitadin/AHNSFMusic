using System.Text.Json;
using AHNSF.Music.Core;
using Microsoft.Data.Sqlite;

namespace AHNSF.Music.Infrastructure;

public sealed class SqlitePlaylistService(string databasePath, SqliteLibraryRepository library) : IPlaylistService
{
    private readonly SemaphoreSlim migration = new(1, 1);
    private bool migrated;
    public Task InitializeAsync(CancellationToken token = default) => Task.Run(async () =>
    { await using var db = await OpenAsync(token).ConfigureAwait(false); }, token);
    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        await library.InitializeAsync(token).ConfigureAwait(false);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true }.ToString());
        try
        {
            await db.OpenAsync(token).ConfigureAwait(false); await migration.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!migrated)
                {
                    using var tx = db.BeginTransaction();
                    Execute(db, tx, """
                        CREATE TABLE IF NOT EXISTS Playlists(Id TEXT PRIMARY KEY,Name TEXT NOT NULL CHECK(length(trim(Name))>0),
                            Description TEXT NOT NULL,CreatedAtUtc TEXT NOT NULL,UpdatedAtUtc TEXT NOT NULL,IsDefault INTEGER NOT NULL CHECK(IsDefault IN(0,1)));
                        CREATE UNIQUE INDEX IF NOT EXISTS IX_Playlists_OneDefault ON Playlists(IsDefault) WHERE IsDefault=1;
                        CREATE TABLE IF NOT EXISTS PlaylistItems(Id TEXT PRIMARY KEY,PlaylistId TEXT NOT NULL REFERENCES Playlists(Id) ON DELETE CASCADE,
                            TrackId TEXT NOT NULL REFERENCES Tracks(Id) ON DELETE RESTRICT,Position INTEGER NOT NULL,AddedAtUtc TEXT NOT NULL);
                        CREATE UNIQUE INDEX IF NOT EXISTS IX_PlaylistItems_Position ON PlaylistItems(PlaylistId,Position);
                        CREATE INDEX IF NOT EXISTS IX_PlaylistItems_Track ON PlaylistItems(TrackId);
                        INSERT OR IGNORE INTO LibraryMigrations(Version) VALUES(2);
                        """);
                    tx.Commit(); migrated = true;
                }
            }
            finally { migration.Release(); }
            return db;
        }
        catch { await db.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    private static int Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    { using var cmd = Command(db, tx, sql, args); return cmd.ExecuteNonQuery(); }
    private static Playlist ReadPlaylist(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2),
        DateTime.Parse(r.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
        DateTime.Parse(r.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind), r.GetBoolean(5));
    private static void Validate(string name, string description)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Informe o nome da playlist.");
        if (name.Trim().Length > 200 || description.Length > 2000) throw new ArgumentException("Nome limitado a 200 caracteres e descrição a 2.000.");
    }
    public Task<IReadOnlyList<Playlist>> ListAsync(CancellationToken token = default) => Task.Run(async () =>
    {
        await using var db = await OpenAsync(token); using var cmd = Command(db, null, "SELECT * FROM Playlists ORDER BY IsDefault DESC,Name COLLATE NOCASE,Id");
        using var r = cmd.ExecuteReader(); var result = new List<Playlist>();
        while (r.Read()) { token.ThrowIfCancellationRequested(); result.Add(ReadPlaylist(r)); }
        return (IReadOnlyList<Playlist>)result;
    }, token);
    public Task<Playlist?> GetDefaultAsync(CancellationToken token = default) => Task.Run(async () =>
    {
        await using var db = await OpenAsync(token); using var cmd = Command(db, null, "SELECT * FROM Playlists WHERE IsDefault=1");
        using var r = cmd.ExecuteReader(); return r.Read() ? ReadPlaylist(r) : null;
    }, token);
    public Task<PlaylistDetail?> GetAsync(Guid id, CancellationToken token = default) => Task.Run(async () =>
    {
        await using var db = await OpenAsync(token); using var tx = db.BeginTransaction();
        Playlist? playlist;
        using (var cmd = Command(db, tx, "SELECT * FROM Playlists WHERE Id=$id", ("$id", id.ToString())))
        { using var r = cmd.ExecuteReader(); playlist = r.Read() ? ReadPlaylist(r) : null; }
        if (playlist is null) return null;
        using var items = Command(db, tx, "SELECT i.Id,i.TrackId,i.Position,i.AddedAtUtc,t.Payload FROM PlaylistItems i JOIN Tracks t ON t.Id=i.TrackId WHERE i.PlaylistId=$id ORDER BY i.Position", ("$id", id.ToString()));
        using var reader = items.ExecuteReader(); var result = new List<PlaylistItem>(); var tracks = new Dictionary<Guid, Track>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            var trackId = Guid.Parse(reader.GetString(1));
            if (!tracks.TryGetValue(trackId, out var track)) { track = ReadTrack(reader.GetString(4)); tracks[trackId] = track; }
            result.Add(new(Guid.Parse(reader.GetString(0)), id, trackId, reader.GetInt32(2),
                DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind), track));
        }
        return new PlaylistDetail(playlist, result);
    }, token);
    private static Track ReadTrack(string payload)
    {
        var track = JsonSerializer.Deserialize<Track>(payload) ?? throw new InvalidDataException("Registro musical inválido.");
        return File.Exists(track.Path) ? track : track with { Availability = AvailabilityStatus.Missing };
    }
    public Task<IReadOnlyDictionary<Guid, Track>> ResolveAsync(IReadOnlyList<Guid> ids, CancellationToken token = default) => Task.Run(async () =>
    {
        await using var db = await OpenAsync(token); var result = new Dictionary<Guid, Track>();
        using var cmd = Command(db, null, "SELECT Payload FROM Tracks WHERE Id=$id"); var p = cmd.Parameters.Add("$id", SqliteType.Text);
        foreach (var id in ids.Distinct()) { token.ThrowIfCancellationRequested(); p.Value = id.ToString(); if (cmd.ExecuteScalar() is string json) result[id] = ReadTrack(json); }
        return (IReadOnlyDictionary<Guid, Track>)result;
    }, token);
    public Task<Playlist> CreateAsync(string name, string description = "", CancellationToken token = default) => Task.Run(async () =>
    {
        Validate(name, description); await using var db = await OpenAsync(token); token.ThrowIfCancellationRequested();
        var now = DateTime.UtcNow; var playlist = new Playlist(Guid.NewGuid(), name.Trim(), description.Trim(), now, now, false);
        Execute(db, null, "INSERT INTO Playlists VALUES($id,$name,$description,$created,$updated,0)", ("$id", playlist.Id.ToString()),
            ("$name", playlist.Name), ("$description", playlist.Description), ("$created", now.ToString("O")), ("$updated", now.ToString("O")));
        return playlist;
    }, token);
    public Task UpdateAsync(Guid id, string name, string description, bool isDefault, CancellationToken token = default) => MutateAsync(id, (db, tx) =>
    {
        Validate(name, description);
        if (isDefault) Execute(db, tx, "UPDATE Playlists SET IsDefault=0,UpdatedAtUtc=$now WHERE IsDefault=1 AND Id<>$id", ("$now", DateTime.UtcNow.ToString("O")), ("$id", id.ToString()));
        Execute(db, tx, "UPDATE Playlists SET Name=$name,Description=$description,IsDefault=$default WHERE Id=$id", ("$name", name.Trim()), ("$description", description.Trim()), ("$default", isDefault), ("$id", id.ToString()));
    }, token);
    public Task DeleteAsync(Guid id, CancellationToken token = default) => MutateAsync(id, (db, tx) =>
        Execute(db, tx, "DELETE FROM Playlists WHERE Id=$id", ("$id", id.ToString())), token);
    public Task AddAsync(Guid id, IReadOnlyList<Guid> tracks, CancellationToken token = default) => MutateAsync(id, (db, tx) =>
    {
        using var count = Command(db, tx, "SELECT COUNT(*) FROM PlaylistItems WHERE PlaylistId=$id", ("$id", id.ToString()));
        var position = Convert.ToInt32(count.ExecuteScalar());
        foreach (var track in tracks)
        {
            token.ThrowIfCancellationRequested();
            Execute(db, tx, "INSERT INTO PlaylistItems VALUES($item,$id,$track,$position,$added)", ("$item", Guid.NewGuid().ToString()), ("$id", id.ToString()), ("$track", track.ToString()), ("$position", position++), ("$added", DateTime.UtcNow.ToString("O")));
        }
    }, token);
    public Task RemoveAsync(Guid id, IReadOnlyList<Guid> items, CancellationToken token = default) => MutateAsync(id, (db, tx) =>
    {
        foreach (var item in items) { token.ThrowIfCancellationRequested(); Execute(db, tx, "DELETE FROM PlaylistItems WHERE PlaylistId=$id AND Id=$item", ("$id", id.ToString()), ("$item", item.ToString())); }
        Reorder(db, tx, id, ItemIds(db, tx, id), token);
    }, token);
    public Task ReorderAsync(Guid id, IReadOnlyList<Guid> items, CancellationToken token = default) => MutateAsync(id, (db, tx) => Reorder(db, tx, id, items, token), token);
    private static List<Guid> ItemIds(SqliteConnection db, SqliteTransaction tx, Guid id)
    {
        using var cmd = Command(db, tx, "SELECT Id FROM PlaylistItems WHERE PlaylistId=$id ORDER BY Position", ("$id", id.ToString()));
        using var r = cmd.ExecuteReader(); var result = new List<Guid>(); while (r.Read()) result.Add(Guid.Parse(r.GetString(0))); return result;
    }
    private static void Reorder(SqliteConnection db, SqliteTransaction tx, Guid id, IReadOnlyList<Guid> items, CancellationToken token)
    {
        var existing = ItemIds(db, tx, id);
        if (items.Count != existing.Count || items.Distinct().Count() != items.Count || !existing.ToHashSet().SetEquals(items)) throw new ArgumentException("A ordem deve conter exatamente os itens atuais da playlist.");
        Execute(db, tx, "UPDATE PlaylistItems SET Position=-Position-1 WHERE PlaylistId=$id", ("$id", id.ToString()));
        for (int n = 0; n < items.Count; n++) { token.ThrowIfCancellationRequested(); Execute(db, tx, "UPDATE PlaylistItems SET Position=$position WHERE PlaylistId=$id AND Id=$item", ("$position", n), ("$id", id.ToString()), ("$item", items[n].ToString())); }
    }
    private Task MutateAsync(Guid id, Action<SqliteConnection, SqliteTransaction> action, CancellationToken token) => Task.Run(async () =>
    {
        await using var db = await OpenAsync(token); using var tx = db.BeginTransaction();
        using var check = Command(db, tx, "SELECT COUNT(*) FROM Playlists WHERE Id=$id", ("$id", id.ToString()));
        if (Convert.ToInt32(check.ExecuteScalar()) != 1) throw new KeyNotFoundException("A playlist não existe mais.");
        action(db, tx); token.ThrowIfCancellationRequested();
        Execute(db, tx, "UPDATE Playlists SET UpdatedAtUtc=$now WHERE Id=$id", ("$now", DateTime.UtcNow.ToString("O")), ("$id", id.ToString()));
        tx.Commit();
    }, token);
}
