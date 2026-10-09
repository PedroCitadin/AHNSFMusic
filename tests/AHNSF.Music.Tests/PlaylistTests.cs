using AHNSF.Music.Core;
using AHNSF.Music.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AHNSF.Music.Tests;

public sealed class PlaylistTests
{
    private static SqlitePlaylistService Service(LibraryFixture f) => new(f.Database, f.Repository);
    private static async Task<Track[]> Tracks(LibraryFixture f)
    { f.WriteTrack("a.mp3", "A"); f.WriteTrack("b.mp3", "B"); return (await f.StartAsync()).Tracks.ToArray(); }

    [Fact] public async Task CreateStoresStableIdentityTimestampsAndOptionalDescription()
    {
        await using var f = new LibraryFixture(); var s = Service(f);
        var p = await s.CreateAsync("  Ambiente  ", "Hospital");
        Assert.NotEqual(Guid.Empty, p.Id); Assert.Equal("Ambiente", p.Name); Assert.Equal("Hospital", p.Description);
        Assert.Equal(DateTimeKind.Utc, p.CreatedAtUtc.Kind); Assert.Equal(p, (await s.GetAsync(p.Id))!.Playlist);
        Assert.Empty((await s.GetAsync(p.Id))!.Items);
    }
    [Theory] [InlineData("")] [InlineData(" \t ")] public async Task RejectEmptyNames(string name)
    { await using var f = new LibraryFixture(); var s = Service(f); await Assert.ThrowsAsync<ArgumentException>(() => s.CreateAsync(name)); Assert.Empty(await s.ListAsync()); }
    [Fact] public async Task DuplicateNamesHaveIndependentIds()
    { await using var f = new LibraryFixture(); var s = Service(f); var a = await s.CreateAsync("Igual"); var b = await s.CreateAsync("Igual"); Assert.NotEqual(a.Id, b.Id); Assert.Equal(2, (await s.ListAsync()).Count); }
    [Fact] public async Task RenameAndDescriptionPreserveIdentityAndCreationDate()
    {
        await using var f = new LibraryFixture(); var s = Service(f); var p = await s.CreateAsync("Antes");
        await s.UpdateAsync(p.Id, "Depois", "Descrição", false); var saved = (await s.GetAsync(p.Id))!.Playlist;
        Assert.Equal(p.Id, saved.Id); Assert.Equal(p.CreatedAtUtc, saved.CreatedAtUtc); Assert.Equal("Depois", saved.Name); Assert.Equal("Descrição", saved.Description);
        Assert.True(saved.UpdatedAtUtc >= p.UpdatedAtUtc);
    }
    [Fact] public async Task AddReferencesTracksWithoutCopyingMetadataAndAllowsDuplicates()
    {
        await using var f = new LibraryFixture(); var t = await Tracks(f); var s = Service(f); var p = await s.CreateAsync("P");
        await s.AddAsync(p.Id, [t[0].Id, t[1].Id, t[0].Id]); var items = (await s.GetAsync(p.Id))!.Items;
        Assert.Equal(new[] { t[0].Id, t[1].Id, t[0].Id }, items.Select(i => i.TrackId)); Assert.Equal(3, items.Select(i => i.Id).Distinct().Count());
        Assert.Same(items[0].Track, items[2].Track);
        Assert.Equal(new[] { 0, 1, 2 }, items.Select(i => i.Position)); Assert.All(items, i => Assert.NotEqual(Guid.Empty, i.Id));
    }
    [Fact] public async Task RemoveOnlySpecificOccurrenceAndCompactPositions()
    {
        await using var f = new LibraryFixture(); var t = await Tracks(f); var s = Service(f); var p = await s.CreateAsync("P");
        await s.AddAsync(p.Id, [t[0].Id, t[0].Id, t[1].Id]); var before = (await s.GetAsync(p.Id))!.Items;
        await s.RemoveAsync(p.Id, [before[1].Id]); var after = (await s.GetAsync(p.Id))!.Items;
        Assert.Equal(new[] { before[0].Id, before[2].Id }, after.Select(i => i.Id)); Assert.Equal(new[] { 0, 1 }, after.Select(i => i.Position));
        Assert.Equal(2, (await f.Service.QueryAsync(new(), default)).Count); Assert.True(File.Exists(t[0].Path));
    }
    [Fact] public async Task RemoveMultipleItemsIsScopedToPlaylist()
    {
        await using var f = new LibraryFixture(); var t = await Tracks(f); var s = Service(f); var a = await s.CreateAsync("A"); var b = await s.CreateAsync("B");
        await s.AddAsync(a.Id, [t[0].Id, t[1].Id]); await s.AddAsync(b.Id, [t[0].Id]);
        var aItems = (await s.GetAsync(a.Id))!.Items; var bItem = Assert.Single((await s.GetAsync(b.Id))!.Items);
        await s.RemoveAsync(a.Id, [aItems[0].Id, aItems[1].Id, bItem.Id]); Assert.Empty((await s.GetAsync(a.Id))!.Items); Assert.Single((await s.GetAsync(b.Id))!.Items);
    }
    [Fact] public async Task ReorderPersistsAfterReopeningAndPreservesOccurrenceIds()
    {
        await using var f = new LibraryFixture(); var t = await Tracks(f); var s = Service(f); var p = await s.CreateAsync("P");
        await s.AddAsync(p.Id, [t[0].Id, t[1].Id, t[0].Id]); var items = (await s.GetAsync(p.Id))!.Items;
        var order = items.Select(i => i.Id).Reverse().ToArray(); await s.ReorderAsync(p.Id, order);
        var reopened = new SqlitePlaylistService(f.Database, new(f.Database)); var after = (await reopened.GetAsync(p.Id))!.Items;
        Assert.Equal(order, after.Select(i => i.Id)); Assert.Equal(new[] { 0, 1, 2 }, after.Select(i => i.Position));
        Assert.Equal(items[0].AddedAtUtc, after[2].AddedAtUtc);
    }
    [Fact] public async Task InvalidReorderRollsBackAtomically()
    {
        await using var f = new LibraryFixture(); var t = await Tracks(f); var s = Service(f); var p = await s.CreateAsync("P"); await s.AddAsync(p.Id, [t[0].Id, t[1].Id]);
        var items = (await s.GetAsync(p.Id))!.Items;
        await Assert.ThrowsAsync<ArgumentException>(() => s.ReorderAsync(p.Id, [items[0].Id, items[0].Id]));
        Assert.Equal(items.Select(i => i.Id), (await s.GetAsync(p.Id))!.Items.Select(i => i.Id));
    }
    [Fact] public async Task ForeignKeyFailureRollsBackWholeAddition()
    {
        await using var f = new LibraryFixture(); var t = await Tracks(f); var s = Service(f); var p = await s.CreateAsync("P");
        await Assert.ThrowsAsync<SqliteException>(() => s.AddAsync(p.Id, [t[0].Id, Guid.NewGuid()])); Assert.Empty((await s.GetAsync(p.Id))!.Items);
    }
    [Fact] public async Task DefaultIsUniquePersistsAndCanBeUnset()
    {
        await using var f = new LibraryFixture(); var s = Service(f); var a = await s.CreateAsync("A"); var b = await s.CreateAsync("B");
        Assert.Null(await s.GetDefaultAsync()); await s.UpdateAsync(a.Id, a.Name, "", true); await s.UpdateAsync(b.Id, b.Name, "", true);
        Assert.Equal(b.Id, (await new SqlitePlaylistService(f.Database, new(f.Database)).GetDefaultAsync())!.Id);
        Assert.Single(await s.ListAsync(), p => p.IsDefault); await s.UpdateAsync(b.Id, b.Name, "", false); Assert.Null(await s.GetDefaultAsync());
    }
    [Fact] public async Task ConcurrentDefaultUpdatesStillHaveOnlyOneDefault()
    {
        await using var f = new LibraryFixture(); var s = Service(f); var a = await s.CreateAsync("A"); var b = await s.CreateAsync("B");
        await Task.WhenAll(s.UpdateAsync(a.Id, a.Name, "", true), s.UpdateAsync(b.Id, b.Name, "", true)); Assert.Single(await s.ListAsync(), p => p.IsDefault);
    }
    [Fact] public async Task DeleteCascadesOnlyAssociationsAndPreservesFilesAndOtherPlaylists()
    {
        await using var f = new LibraryFixture(); var t = await Tracks(f); var before = File.ReadAllBytes(t[0].Path); var s = Service(f);
        var a = await s.CreateAsync("A"); var b = await s.CreateAsync("B"); await s.AddAsync(a.Id, [t[0].Id]); await s.AddAsync(b.Id, [t[0].Id]);
        await s.UpdateAsync(a.Id, a.Name, "", true); await s.DeleteAsync(a.Id);
        Assert.Null(await s.GetAsync(a.Id)); Assert.Null(await s.GetDefaultAsync()); Assert.Single((await s.GetAsync(b.Id))!.Items);
        Assert.Equal(before, File.ReadAllBytes(t[0].Path)); Assert.Equal(2, (await f.Service.QueryAsync(new(), default)).Count);
        await using var db = new SqliteConnection($"Data Source={f.Database}"); await db.OpenAsync(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM PlaylistItems"; Assert.Equal(1L, cmd.ExecuteScalar());
    }
    [Fact] public async Task MissingFileKeepsReferenceAndReappearanceRestoresAvailability()
    {
        await using var f = new LibraryFixture(); var t = await Tracks(f); var s = Service(f); var p = await s.CreateAsync("P"); await s.AddAsync(p.Id, [t[0].Id]);
        var item = Assert.Single((await s.GetAsync(p.Id))!.Items); File.Delete(t[0].Path); await f.Service.ReconcileAsync(false, default);
        var missing = Assert.Single((await s.GetAsync(p.Id))!.Items); Assert.Equal(item.Id, missing.Id); Assert.Equal(t[0].Id, missing.TrackId); Assert.Equal(AvailabilityStatus.Missing, missing.Track.Availability);
        f.WriteTrack("a.mp3", "Nova A"); await f.Service.ReconcileAsync(false, default); var restored = Assert.Single((await s.GetAsync(p.Id))!.Items);
        Assert.Equal(t[0].Id, restored.TrackId); Assert.Equal("Nova A", restored.Track.Title); Assert.Equal(AvailabilityStatus.Available, restored.Track.Availability);
    }
    [Fact] public async Task MetadataChangesAreResolvedFromLibrary()
    {
        await using var f = new LibraryFixture(); var t = await Tracks(f); var s = Service(f); var p = await s.CreateAsync("P"); await s.AddAsync(p.Id, [t[0].Id]);
        f.WriteTrack("a.mp3", "Título novo", "Artista novo", "Álbum novo"); await f.Service.ReconcileAsync(true, default);
        var item = Assert.Single((await s.GetAsync(p.Id))!.Items); Assert.Equal("Título novo", item.Track.Title); Assert.Equal("Artista novo", item.Track.Artist);
    }
    [Fact] public async Task MigrationPreservesSettingsTracksAndExistingMigrationAndIsIdempotent()
    {
        await using var f = new LibraryFixture(); var t = await Tracks(f); var store = new SqliteSettingsStore(f.Database);
        var settings = new AppSettings(f.Music, .22f, 1100, 760, 12, 34, true, RepeatMode.Track); await store.SaveAsync(settings);
        var s = Service(f); var p = await s.CreateAsync("Migração"); await s.AddAsync(p.Id, [t[0].Id]);
        Assert.Single(await new SqlitePlaylistService(f.Database, new(f.Database)).ListAsync()); Assert.Equal(settings, await store.LoadAsync());
        Assert.Equal(t.Select(i => i.Id), (await f.Service.QueryAsync(new(), default)).Tracks.Select(i => i.Id));
        await using var db = new SqliteConnection($"Data Source={f.Database}"); await db.OpenAsync(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM LibraryMigrations WHERE Version IN(1,2)"; Assert.Equal(2L, cmd.ExecuteScalar());
        cmd.CommandText = "PRAGMA foreign_key_check"; Assert.Null(cmd.ExecuteScalar());
    }
    [Fact] public async Task LargePlaylistPreservesTwoThousandOccurrences()
    {
        await using var f = new LibraryFixture(); var t = await Tracks(f); var s = Service(f); var p = await s.CreateAsync("Grande");
        await s.AddAsync(p.Id, Enumerable.Range(0, 2000).Select(n => t[n % 2].Id).ToArray()); var items = (await s.GetAsync(p.Id))!.Items;
        Assert.Equal(2000, items.Count); await s.ReorderAsync(p.Id, items.Select(i => i.Id).Reverse().ToArray()); Assert.Equal(items[^1].Id, (await s.GetAsync(p.Id))!.Items[0].Id);
    }
    [Fact] public async Task CancellationLeavesPlaylistUnchanged()
    {
        await using var f = new LibraryFixture(); var t = await Tracks(f); var s = Service(f); var p = await s.CreateAsync("P");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.AddAsync(p.Id, [t[0].Id], new(true))); Assert.Empty((await s.GetAsync(p.Id))!.Items);
    }
}

