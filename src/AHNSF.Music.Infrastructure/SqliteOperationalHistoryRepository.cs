using AHNSF.Music.Core;
using Microsoft.Data.Sqlite;

namespace AHNSF.Music.Infrastructure;

public sealed class SqliteOperationalHistoryRepository(string databasePath, SqliteLibraryRepository library) : IOperationalHistoryRepository
{
    private readonly SemaphoreSlim migration = new(1, 1);
    private readonly SemaphoreSlim writes = new(1, 1);
    private bool initialized;
    public Task InitializeAsync(CancellationToken token) => Task.Run(async () => { await using var db = await OpenAsync(token); }, token);
    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        await library.InitializeAsync(token).ConfigureAwait(false);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, DefaultTimeout = 1 }.ToString());
        try
        {
            await db.OpenAsync(token).ConfigureAwait(false); await migration.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (!initialized)
                {
                    using var tx = db.BeginTransaction();
                    using var cmd = Command(db, tx, """
                        CREATE TABLE IF NOT EXISTS OperationalSessions(Id TEXT PRIMARY KEY,StartedUtc INTEGER NOT NULL,EndedUtc INTEGER,DurationSeconds REAL,FinalState TEXT NOT NULL,RelevantErrors INTEGER NOT NULL);
                        CREATE INDEX IF NOT EXISTS IX_OperationalSessions_Start ON OperationalSessions(StartedUtc DESC,Id);
                        CREATE TABLE IF NOT EXISTS PlaybackPeriods(Id TEXT PRIMARY KEY,SessionId TEXT NOT NULL,TrackId TEXT,PlaylistId TEXT,RuleId TEXT,DeviceId TEXT,TrackTitle TEXT NOT NULL,PlaylistName TEXT NOT NULL,Origin INTEGER NOT NULL,StartedUtc INTEGER NOT NULL,EndedUtc INTEGER,EffectiveSeconds REAL,EndReason TEXT NOT NULL,Confirmed INTEGER NOT NULL);
                        CREATE INDEX IF NOT EXISTS IX_PlaybackPeriods_Start ON PlaybackPeriods(StartedUtc DESC,Id);
                        CREATE INDEX IF NOT EXISTS IX_PlaybackPeriods_Session ON PlaybackPeriods(SessionId);
                        CREATE TABLE IF NOT EXISTS OperationalEvents(Id TEXT PRIMARY KEY,TimestampUtc INTEGER NOT NULL,Type INTEGER NOT NULL,Severity INTEGER NOT NULL,Component TEXT NOT NULL,Description TEXT NOT NULL,SessionId TEXT NOT NULL,Origin INTEGER NOT NULL,TrackId TEXT,PlaylistId TEXT,RuleId TEXT,DeviceId TEXT,PlaybackId TEXT,DataJson TEXT NOT NULL CHECK(length(DataJson)<=2048));
                        CREATE INDEX IF NOT EXISTS IX_OperationalEvents_Time ON OperationalEvents(TimestampUtc DESC,Id DESC);
                        CREATE INDEX IF NOT EXISTS IX_OperationalEvents_TypeTime ON OperationalEvents(Type,TimestampUtc DESC,Id DESC);
                        CREATE INDEX IF NOT EXISTS IX_OperationalEvents_SeverityTime ON OperationalEvents(Severity,TimestampUtc DESC,Id DESC);
                        CREATE INDEX IF NOT EXISTS IX_OperationalEvents_SessionSeverity ON OperationalEvents(SessionId,Severity);
                        INSERT OR IGNORE INTO LibraryMigrations(Version) VALUES(4);
                        """);
                    cmd.ExecuteNonQuery(); tx.Commit(); initialized = true;
                }
            }
            finally { migration.Release(); }
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    { var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql; foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value); return cmd; }
    private static long Stamp(DateTimeOffset time) => time.UtcTicks;
    private static object? Stamp(DateTimeOffset? time) => time?.UtcTicks;
    private static string? Id(Guid? id) => id?.ToString("D");
    private static Guid? GuidAt(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Guid.Parse(r.GetString(i));
    private static DateTimeOffset TimeAt(SqliteDataReader r, int i) => new(r.GetInt64(i), TimeSpan.Zero);
    private static DateTimeOffset? NullableTime(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : TimeAt(r, i);
    private static double? Number(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
    private static OperationalEvent ReadEvent(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), TimeAt(r, 1), (OperationalEventType)r.GetInt32(2), (OperationalEventSeverity)r.GetInt32(3), r.GetString(4), r.GetString(5), Guid.Parse(r.GetString(6)), (OperationalActionOrigin)r.GetInt32(7), GuidAt(r, 8), GuidAt(r, 9), GuidAt(r, 10), r.IsDBNull(11) ? null : r.GetString(11), GuidAt(r, 12), r.GetString(13));
    private static PlaybackPeriod ReadPeriod(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), GuidAt(r, 2), GuidAt(r, 3), GuidAt(r, 4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.GetString(7), (OperationalActionOrigin)r.GetInt32(8), TimeAt(r, 9), NullableTime(r, 10), Number(r, 11), r.GetString(12), r.GetInt32(13) == 1);
    public Task MarkUnconfirmedAsync(Guid currentSession, CancellationToken token) => Task.Run(async () =>
    {
        await writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
        await using var db = await OpenAsync(token); using var tx = db.BeginTransaction();
        using (var cmd = Command(db, tx, "INSERT INTO OperationalEvents SELECT lower(hex(randomblob(4)))||'-'||lower(hex(randomblob(2)))||'-'||lower(hex(randomblob(2)))||'-'||lower(hex(randomblob(2)))||'-'||lower(hex(randomblob(6))),$now,$type,$severity,'Aplicativo','Execução anterior sem encerramento confirmado; causa desconhecida.',Id,0,NULL,NULL,NULL,NULL,NULL,'{}' FROM OperationalSessions WHERE EndedUtc IS NULL AND FinalState='Em execução' AND Id<>$id", ("$id", currentSession.ToString()), ("$now", Stamp(DateTimeOffset.UtcNow)), ("$type", (int)OperationalEventType.PreviousSessionUnconfirmed), ("$severity", (int)OperationalEventSeverity.Warning))) cmd.ExecuteNonQuery();
        using (var cmd = Command(db, tx, "UPDATE PlaybackPeriods SET Confirmed=0,EffectiveSeconds=NULL,EndReason='Sem encerramento confirmado' WHERE EndedUtc IS NULL AND SessionId<>$id", ("$id", currentSession.ToString()))) cmd.ExecuteNonQuery();
        using (var cmd = Command(db, tx, "UPDATE OperationalSessions SET FinalState='Sem encerramento confirmado',DurationSeconds=NULL WHERE EndedUtc IS NULL AND Id<>$id", ("$id", currentSession.ToString()))) cmd.ExecuteNonQuery();
        token.ThrowIfCancellationRequested(); tx.Commit();
        }
        finally { writes.Release(); }
    }, token);
    public Task AppendAsync(IReadOnlyList<OperationalWrite> entries, CancellationToken token) => Task.Run(async () =>
    {
        await writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
        await using var db = await OpenAsync(token); using var tx = db.BeginTransaction();
        foreach (var item in entries)
        {
            token.ThrowIfCancellationRequested(); var e = item.Event;
            using (var cmd = Command(db, tx, "INSERT OR IGNORE INTO OperationalEvents VALUES($id,$time,$type,$severity,$component,$description,$session,$origin,$track,$playlist,$rule,$device,$playback,$data)",
                ("$id", e.Id.ToString()), ("$time", Stamp(e.TimestampUtc)), ("$type", (int)e.Type), ("$severity", (int)e.Severity), ("$component", OperationalHistoryService.Limit(e.Component, 64)), ("$description", OperationalHistoryService.Limit(e.Description, 512)), ("$session", e.SessionId.ToString()), ("$origin", (int)e.Origin), ("$track", Id(e.TrackId)), ("$playlist", Id(e.PlaylistId)), ("$rule", Id(e.RuleId)), ("$device", e.DeviceId is null ? null : OperationalHistoryService.Limit(e.DeviceId, 256)), ("$playback", Id(e.PlaybackId)), ("$data", OperationalHistoryService.SafeJson(e.DataJson)))) cmd.ExecuteNonQuery();
            if (item.Session is { } s)
            { using var cmd = Command(db, tx, "INSERT INTO OperationalSessions VALUES($id,$start,$end,$duration,$state,$errors) ON CONFLICT(Id) DO UPDATE SET EndedUtc=excluded.EndedUtc,DurationSeconds=excluded.DurationSeconds,FinalState=excluded.FinalState,RelevantErrors=excluded.RelevantErrors", ("$id", s.Id.ToString()), ("$start", Stamp(s.StartedUtc)), ("$end", Stamp(s.EndedUtc)), ("$duration", s.DurationSeconds), ("$state", s.FinalState), ("$errors", s.RelevantErrors)); cmd.ExecuteNonQuery(); }
            if (item.Playback is { } p)
            { using var cmd = Command(db, tx, "INSERT INTO PlaybackPeriods VALUES($id,$session,$track,$playlist,$rule,$device,$title,$name,$origin,$start,$end,$duration,$reason,$confirmed) ON CONFLICT(Id) DO UPDATE SET EndedUtc=excluded.EndedUtc,EffectiveSeconds=excluded.EffectiveSeconds,EndReason=excluded.EndReason,Confirmed=excluded.Confirmed", ("$id", p.Id.ToString()), ("$session", p.SessionId.ToString()), ("$track", Id(p.TrackId)), ("$playlist", Id(p.PlaylistId)), ("$rule", Id(p.RuleId)), ("$device", p.DeviceId), ("$title", p.TrackTitle), ("$name", p.PlaylistName), ("$origin", (int)p.Origin), ("$start", Stamp(p.StartedUtc)), ("$end", Stamp(p.EndedUtc)), ("$duration", p.EffectiveSeconds), ("$reason", p.EndReason), ("$confirmed", p.Confirmed ? 1 : 0)); cmd.ExecuteNonQuery(); }
        }
        foreach (var sessionId in entries.Select(e => e.Event.SessionId).Distinct())
        { using var cmd = Command(db, tx, "UPDATE OperationalSessions SET RelevantErrors=(SELECT count(*) FROM OperationalEvents WHERE SessionId=$id AND Severity=2) WHERE Id=$id", ("$id", sessionId.ToString())); cmd.ExecuteNonQuery(); }
        tx.Commit();
        }
        finally { writes.Release(); }
    }, token);
    private static SqliteCommand Filter(SqliteConnection db, SqliteTransaction? tx, string select, OperationalHistoryQuery q, bool periods = false)
    {
        if (q.Page < 0 || q.PageSize is < 1 or > 200 || q.Page > 1_000_000) throw new ArgumentOutOfRangeException(nameof(q));
        var column = periods ? "StartedUtc" : "TimestampUtc";
        var cmd = Command(db, tx, select + $" WHERE ($from IS NULL OR {column}>=$from) AND ($until IS NULL OR {column}<$until)" +
            (periods ? " AND ($search='' OR TrackTitle LIKE $pattern ESCAPE '!' OR PlaylistName LIKE $pattern ESCAPE '!')" : " AND ($type IS NULL OR Type=$type) AND ($severity IS NULL OR Severity=$severity) AND ($search='' OR Description LIKE $pattern ESCAPE '!' OR Component LIKE $pattern ESCAPE '!' OR DataJson LIKE $pattern ESCAPE '!')"),
            ("$from", Stamp(q.FromUtc)), ("$until", Stamp(q.UntilUtc)), ("$search", q.Search), ("$pattern", "%" + OperationalHistoryService.Limit(q.Search, 256).Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%"));
        if (!periods) { cmd.Parameters.AddWithValue("$type", q.Type is { } type ? (int)type : DBNull.Value); cmd.Parameters.AddWithValue("$severity", q.Severity is { } severity ? (int)severity : DBNull.Value); }
        if (!periods && q.BeforeUtc is { } before && q.BeforeId is { } beforeId)
        { cmd.CommandText += " AND (TimestampUtc<$cursor OR (TimestampUtc=$cursor AND Id<$cursorId))"; cmd.Parameters.AddWithValue("$cursor", Stamp(before)); cmd.Parameters.AddWithValue("$cursorId", beforeId.ToString()); }
        return cmd;
    }
    public Task<OperationalHistoryPage> QueryAsync(OperationalHistoryQuery query, CancellationToken token) => Task.Run(async () =>
    {
        await using var db = await OpenAsync(token); using var tx = db.BeginTransaction(deferred: true); int count;
        using (var cmd = Filter(db, tx, "SELECT count(*) FROM OperationalEvents", query)) count = Convert.ToInt32(cmd.ExecuteScalar());
        using var rows = Filter(db, tx, "SELECT * FROM OperationalEvents", query); rows.CommandText += " ORDER BY TimestampUtc DESC,Id DESC LIMIT $take OFFSET $skip"; rows.Parameters.AddWithValue("$take", query.PageSize); rows.Parameters.AddWithValue("$skip", (long)query.Page * query.PageSize);
        List<OperationalEvent> events = []; using var reader = rows.ExecuteReader(); while (reader.Read()) { token.ThrowIfCancellationRequested(); events.Add(ReadEvent(reader)); }
        return new OperationalHistoryPage(events, count);
    }, token);
    public Task<IReadOnlyList<OperationalSession>> SessionsAsync(int page, CancellationToken token) => Task.Run<IReadOnlyList<OperationalSession>>(async () =>
    {
        if (page < 0) throw new ArgumentOutOfRangeException(nameof(page)); await using var db = await OpenAsync(token);
        using var cmd = Command(db, null, "SELECT * FROM OperationalSessions ORDER BY StartedUtc DESC,Id DESC LIMIT 50 OFFSET $skip", ("$skip", (long)page * 50)); List<OperationalSession> result = [];
        using var reader = cmd.ExecuteReader(); while (reader.Read()) { token.ThrowIfCancellationRequested(); result.Add(new(Guid.Parse(reader.GetString(0)), TimeAt(reader, 1), NullableTime(reader, 2), Number(reader, 3), reader.GetString(4), reader.GetInt32(5))); } return result;
    }, token);
    public Task<IReadOnlyList<PlaybackPeriod>> PlaybackAsync(OperationalHistoryQuery query, CancellationToken token) => Task.Run<IReadOnlyList<PlaybackPeriod>>(async () =>
    {
        await using var db = await OpenAsync(token); using var cmd = Filter(db, null, "SELECT * FROM PlaybackPeriods", query, true); cmd.CommandText += " ORDER BY StartedUtc DESC,Id DESC LIMIT $take OFFSET $skip"; cmd.Parameters.AddWithValue("$take", query.PageSize); cmd.Parameters.AddWithValue("$skip", (long)query.Page * query.PageSize);
        List<PlaybackPeriod> result = []; using var reader = cmd.ExecuteReader(); while (reader.Read()) { token.ThrowIfCancellationRequested(); result.Add(ReadPeriod(reader)); } return result;
    }, token);
    public async Task<OperationalHistorySummary> SummaryAsync(DateTimeOffset since, CancellationToken token)
    {
        var errors = await QueryAsync(new(FromUtc: since, Severity: OperationalEventSeverity.Error, PageSize: 1), token);
        var lastError = await QueryAsync(new(Severity: OperationalEventSeverity.Error, PageSize: 1), token);
        var recovery = await QueryAsync(new(Type: OperationalEventType.RecoverySucceeded, PageSize: 1), token);
        var disconnect = await QueryAsync(new(Type: OperationalEventType.DeviceDisconnected, PageSize: 1), token);
        return new(errors.Total, lastError.Events.FirstOrDefault(), recovery.Events.FirstOrDefault(), disconnect.Events.FirstOrDefault());
    }
    public Task<IReadOnlyList<PlaybackPeriod>> PlaybackByIdsAsync(IReadOnlyList<Guid> ids, CancellationToken token) => Task.Run<IReadOnlyList<PlaybackPeriod>>(async () =>
    {
        if (ids.Count == 0) return Array.Empty<PlaybackPeriod>();
        if (ids.Count > 200) throw new ArgumentOutOfRangeException(nameof(ids));
        await using var db = await OpenAsync(token); using var cmd = Command(db, null, "SELECT * FROM PlaybackPeriods WHERE Id IN (" + string.Join(',', ids.Select((_, i) => "$id" + i)) + ")");
        for (int i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue("$id" + i, ids[i].ToString());
        List<PlaybackPeriod> result = []; using var reader = cmd.ExecuteReader(); while (reader.Read()) { token.ThrowIfCancellationRequested(); result.Add(ReadPeriod(reader)); } return result;
    }, token);
    public Task<int> RetainAsync(DateTimeOffset cutoff, CancellationToken token) => Task.Run(async () =>
    {
        await writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
        await using var db = await OpenAsync(token); using var tx = db.BeginTransaction(); int count = 0;
        foreach (var table in new[] { ("OperationalEvents", "TimestampUtc"), ("PlaybackPeriods", "StartedUtc"), ("OperationalSessions", "StartedUtc") })
        { token.ThrowIfCancellationRequested(); using var cmd = Command(db, tx, $"DELETE FROM {table.Item1} WHERE Id IN (SELECT Id FROM {table.Item1} WHERE {table.Item2}<$cutoff" + (table.Item1 == "OperationalEvents" ? "" : table.Item1 == "PlaybackPeriods" ? " AND (EndedUtc IS NOT NULL OR Confirmed=0)" : " AND FinalState<>'Em execução'") + " LIMIT 500)", ("$cutoff", Stamp(cutoff))); count += cmd.ExecuteNonQuery(); }
        tx.Commit(); return count;
        }
        finally { writes.Release(); }
    }, token);
}
