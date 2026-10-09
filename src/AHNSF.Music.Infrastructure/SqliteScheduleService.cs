using AHNSF.Music.Core;
using Microsoft.Data.Sqlite;

namespace AHNSF.Music.Infrastructure;

public sealed class SqliteScheduleService(string databasePath, SqlitePlaylistService playlists) : IScheduleService
{
    private readonly SemaphoreSlim migration = new(1, 1);
    private bool migrated;
    public event EventHandler? Changed;
    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        await playlists.InitializeAsync(token).ConfigureAwait(false);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true }.ToString());
        try
        {
            await db.OpenAsync(token).ConfigureAwait(false); await migration.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!migrated)
                {
                    using var tx = db.BeginTransaction();
                    using var cmd = Command(db, tx, """
                        CREATE TABLE IF NOT EXISTS ScheduleRules(Id TEXT PRIMARY KEY,Name TEXT NOT NULL CHECK(length(trim(Name))>0),
                            PlaylistId TEXT REFERENCES Playlists(Id) ON DELETE SET NULL,Days INTEGER NOT NULL CHECK(Days BETWEEN 1 AND 127),
                            StartTicks INTEGER NOT NULL CHECK(StartTicks>=0 AND StartTicks<864000000000),
                            EndTicks INTEGER NOT NULL CHECK(EndTicks>=0 AND EndTicks<864000000000 AND EndTicks<>StartTicks),
                            Volume REAL NOT NULL CHECK(Volume BETWEEN 0 AND 100),Priority INTEGER NOT NULL CHECK(Priority BETWEEN 0 AND 1000),
                            IsEnabled INTEGER NOT NULL CHECK(IsEnabled IN(0,1)),CreatedAtUtc TEXT NOT NULL,UpdatedAtUtc TEXT NOT NULL);
                        CREATE INDEX IF NOT EXISTS IX_ScheduleRules_Enabled_Priority ON ScheduleRules(IsEnabled,Priority);
                        CREATE INDEX IF NOT EXISTS IX_ScheduleRules_Playlist ON ScheduleRules(PlaylistId);
                        CREATE TABLE IF NOT EXISTS ScheduleSettings(Id INTEGER PRIMARY KEY CHECK(Id=1),Mode INTEGER NOT NULL CHECK(Mode IN(0,1)));
                        INSERT OR IGNORE INTO ScheduleSettings VALUES(1,0);
                        INSERT OR IGNORE INTO LibraryMigrations(Version) VALUES(3);
                        """);
                    cmd.ExecuteNonQuery(); tx.Commit(); migrated = true;
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
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value); return cmd;
    }
    public Task<ScheduleConfiguration> LoadAsync(CancellationToken token = default) => Task.Run(async () =>
    {
        await using var db = await OpenAsync(token); using var tx = db.BeginTransaction();
        using var modeCommand = Command(db, tx, "SELECT Mode FROM ScheduleSettings WHERE Id=1");
        var mode = (OperationMode)Convert.ToInt32(modeCommand.ExecuteScalar());
        using var cmd = Command(db, tx, "SELECT * FROM ScheduleRules ORDER BY Priority DESC,Name,Id");
        using var r = cmd.ExecuteReader(); var rules = new List<ScheduleRule>();
        while (r.Read())
        {
            token.ThrowIfCancellationRequested();
            rules.Add(new(Guid.Parse(r.GetString(0)), r.GetString(1), r.IsDBNull(2) ? null : Guid.Parse(r.GetString(2)), (ScheduleDays)r.GetInt32(3),
                new TimeOnly(r.GetInt64(4)), new TimeOnly(r.GetInt64(5)), r.GetDouble(6), r.GetInt32(7), r.GetBoolean(8),
                DateTime.Parse(r.GetString(9), null, System.Globalization.DateTimeStyles.RoundtripKind), DateTime.Parse(r.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }
        return new ScheduleConfiguration(mode, rules);
    }, token);
    public async Task<ScheduleRule> SaveAsync(ScheduleRule rule, CancellationToken token = default)
    {
        ScheduleRule.Validate(rule);
        var saved = await Task.Run(async () =>
        {
            await using var db = await OpenAsync(token); using var tx = db.BeginTransaction();
            var id = rule.Id == Guid.Empty ? Guid.NewGuid() : rule.Id; var now = DateTime.UtcNow;
            using var original = Command(db, tx, "SELECT CreatedAtUtc FROM ScheduleRules WHERE Id=$id", ("$id", id.ToString()));
            var created = original.ExecuteScalar() is string text ? DateTime.Parse(text, null, System.Globalization.DateTimeStyles.RoundtripKind) : now;
            var result = rule with { Id = id, Name = rule.Name.Trim(), CreatedAtUtc = created, UpdatedAtUtc = now };
            using var cmd = Command(db, tx, """
                INSERT INTO ScheduleRules VALUES($id,$name,$playlist,$days,$start,$end,$volume,$priority,$enabled,$created,$updated)
                ON CONFLICT(Id) DO UPDATE SET Name=$name,PlaylistId=$playlist,Days=$days,StartTicks=$start,EndTicks=$end,
                    Volume=$volume,Priority=$priority,IsEnabled=$enabled,UpdatedAtUtc=$updated;
                """, ("$id", id.ToString()), ("$name", result.Name), ("$playlist", result.PlaylistId?.ToString()), ("$days", (int)result.Days),
                ("$start", result.StartTime.Ticks), ("$end", result.EndTime.Ticks), ("$volume", result.Volume), ("$priority", result.Priority),
                ("$enabled", result.IsEnabled), ("$created", created.ToString("O")), ("$updated", now.ToString("O")));
            cmd.ExecuteNonQuery(); token.ThrowIfCancellationRequested(); tx.Commit(); return result;
        }, token).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty); return saved;
    }
    public Task DeleteAsync(Guid id, CancellationToken token = default) => MutateAsync("DELETE FROM ScheduleRules WHERE Id=$id", token, ("$id", id.ToString()));
    public Task SetEnabledAsync(Guid id, bool enabled, CancellationToken token = default) => MutateAsync("UPDATE ScheduleRules SET IsEnabled=$enabled,UpdatedAtUtc=$now WHERE Id=$id", token,
        ("$enabled", enabled), ("$now", DateTime.UtcNow.ToString("O")), ("$id", id.ToString()));
    public Task SetModeAsync(OperationMode mode, CancellationToken token = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Modo operacional inválido.");
        return MutateAsync("UPDATE ScheduleSettings SET Mode=$mode WHERE Id=1", token, ("$mode", (int)mode));
    }
    private async Task MutateAsync(string sql, CancellationToken token, params (string, object?)[] args)
    {
        await Task.Run(async () =>
        {
            await using var db = await OpenAsync(token); using var tx = db.BeginTransaction(); using var cmd = Command(db, tx, sql, args);
            if (cmd.ExecuteNonQuery() != 1) throw new KeyNotFoundException("A regra não existe mais.");
            token.ThrowIfCancellationRequested(); tx.Commit();
        }, token).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
