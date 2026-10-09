using System.Text.Json;
using AHNSF.Music.Core;
using Microsoft.Data.Sqlite;

namespace AHNSF.Music.Infrastructure;

public sealed class SqliteSettingsStore(string databasePath) : ISettingsStore
{
    private async Task<SqliteConnection> OpenAsync()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(databasePath))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        try
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS Settings (Id INTEGER PRIMARY KEY CHECK(Id=1), Json TEXT NOT NULL)";
            await command.ExecuteNonQueryAsync();
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async Task<AppSettings> LoadAsync()
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Json FROM Settings WHERE Id=1";
        var json = await command.ExecuteScalarAsync() as string;
        return json is null ? new AppSettings() : JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }
    public async Task SaveAsync(AppSettings settings)
    {
        await using var connection = await OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Settings(Id,Json) VALUES(1,$json) ON CONFLICT(Id) DO UPDATE SET Json=$json";
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(settings));
        await command.ExecuteNonQueryAsync();
    }
}
