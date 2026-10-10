using AHNSF.Music.Core;
using AHNSF.Music.Desktop;
using AHNSF.Music.Desktop.Theming;
using AHNSF.Music.Infrastructure;
using Avalonia.Media;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AHNSF.Music.Tests;

// Fase 0.8.3: appearance preferences, theme palette and runtime application. No visual tree is required.
public sealed class AppearanceTests
{
    private sealed class Scanner : IMusicScanner
    { public Task<ScanResult> ScanAsync(string directory, CancellationToken cancellationToken) => Task.FromResult(new ScanResult([], [])); }
    private sealed class RecordingTarget : IThemeTarget
    {
        public List<(ThemePalette Palette, AppearanceSettings Appearance)> Applied { get; } = [];
        public void Apply(ThemePalette palette, AppearanceSettings appearance) => Applied.Add((palette, appearance));
    }
    private sealed class FakeSystem : ISystemThemeSource
    {
        public bool? PrefersDark { get; set; }
        public event EventHandler? Changed;
        public void Change(bool? dark) { PrefersDark = dark; Changed?.Invoke(this, EventArgs.Empty); }
    }
    private static readonly string[] Backgrounds = ["BackgroundPrimary", "BackgroundSecondary", "Surface"];
    private static readonly string[] TextSurfaces = ["BackgroundPrimary", "BackgroundSecondary", "Surface", "SurfaceHover", "SurfaceSelected", "AccentSubtle"];
    private static double C(ThemePalette p, string a, string b) => ThemePalette.Contrast(p[a], p[b]);
    private static async Task<(LibraryFixture Fixture, SqliteSettingsStore Store, MainViewModel ViewModel)> CreateAsync(ThemeService? theme = null)
    {
        var f = new LibraryFixture(); var store = new SqliteSettingsStore(f.Database);
        var vm = new MainViewModel(new QueueTests.TestAudio(), new Scanner(), store, f.Logger);
        if (theme is not null) vm.AttachTheme(theme);
        await vm.InitializeAsync();
        return (f, store, vm);
    }

    [Theory]
    [InlineData("#3cc8b4", "#3CC8B4")] [InlineData("3CC8B4", "#3CC8B4")] [InlineData(" #abc ", "#AABBCC")] [InlineData("#000", "#000000")] [InlineData("ffffff", "#FFFFFF")]
    public void HexAcceptsSixOrThreeDigitsAndNormalizes(string input, string expected)
    { Assert.True(AppearanceSettings.TryNormalizeHex(input, out var hex)); Assert.Equal(expected, hex); }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("#12345")] [InlineData("#1234567")] [InlineData("#GGGGGG")] [InlineData("#80FFFFFF")] [InlineData("red")] [InlineData("# 12345")] [InlineData("12 345")]
    public void HexRejectsInvalidValues(string? input) => Assert.False(AppearanceSettings.TryNormalizeHex(input, out _));

    [Fact] public void PaletteKeepsTextAndControlsLegibleForEveryPresetAndArbitraryColours()
    {
        var random = new Random(803);
        var accents = AppearanceSettings.Presets.Select(p => p.Hex)
            .Concat(["#000000", "#FFFFFF", "#808080", "#FFFF00", "#0000FF", "#00FF00", "#FF0000", "#7F7F00", "#101010", "#F0F0F0"])
            .Concat(Enumerable.Range(0, 300).Select(_ => $"#{random.Next(0x1000000):X6}")).ToArray();
        foreach (var dark in new[] { true, false })
        foreach (var accent in accents)
        {
            var p = ThemePalette.Create(dark, accent);
            Assert.Equal(dark, p.IsDark);
            Assert.All(ThemePalette.TokenNames, token => Assert.True(p.Tokens.ContainsKey(token), token));
            Assert.True(C(p, "AccentForeground", "AccentPrimary") >= 4.5, $"{accent} {dark}: texto do botão principal");
            Assert.True(C(p, "AccentForeground", "AccentHover") >= 4.5, $"{accent} {dark}: hover do botão principal");
            Assert.True(C(p, "AccentForeground", "AccentPressed") >= 4.5, $"{accent} {dark}: botão pressionado");
            foreach (var background in Backgrounds) Assert.True(C(p, "AccentPrimary", background) >= 3, $"{accent} {dark}: destaque sobre {background}");
            foreach (var surface in TextSurfaces)
            {
                Assert.True(C(p, "AccentText", surface) >= 4.5, $"{accent} {dark}: texto de destaque sobre {surface}");
                Assert.True(C(p, "TextPrimary", surface) >= 7, $"{accent} {dark}: texto principal sobre {surface}");
                Assert.True(C(p, "TextSecondary", surface) >= 4.5, $"{accent} {dark}: texto secundário sobre {surface}");
            }
            Assert.Equal(p["AccentText"], p["FocusRing"]);
        }
    }

    [Fact] public void StatusColoursAreReadableOnSurfacesAndBadgesInBothThemes()
    {
        foreach (var dark in new[] { true, false })
        {
            var p = ThemePalette.Create(dark, null);
            foreach (var (tone, subtle) in new[] { ("Success", "SuccessSubtle"), ("Warning", "WarningSubtle"), ("Error", "ErrorSubtle") })
            {
                Assert.True(C(p, tone, "Surface") >= 4.5, $"{tone} {dark}");
                Assert.True(C(p, tone, subtle) >= 4.5, $"{tone} sobre {subtle} {dark}");
                Assert.True(C(p, tone, "BackgroundPrimary") >= 4.5, $"{tone} fundo {dark}");
            }
        }
    }

    [Fact] public void DerivedColoursFollowAccentAndAdjustmentIsReported()
    {
        var blue = ThemePalette.Create(true, "#4C8DF6"); var red = ThemePalette.Create(true, "#E5484D");
        Assert.NotEqual(blue["SurfaceSelected"], red["SurfaceSelected"]); Assert.NotEqual(blue["AccentSubtle"], red["AccentSubtle"]);
        Assert.Equal(Color.Parse("#4C8DF6"), blue["AccentPrimary"]); Assert.False(blue.AccentAdjusted);
        var nearBlack = ThemePalette.Create(true, "#101010");
        Assert.True(nearBlack.AccentAdjusted); Assert.True(ThemePalette.Luminance(nearBlack["AccentPrimary"]) > ThemePalette.Luminance(Color.Parse("#101010")));
        var nearWhite = ThemePalette.Create(false, "#F0F0F0");
        Assert.True(nearWhite.AccentAdjusted); Assert.True(ThemePalette.Luminance(nearWhite["AccentPrimary"]) < ThemePalette.Luminance(Color.Parse("#F0F0F0")));
        Assert.Equal("#3CC8B4", ThemePalette.Create(true, "não é cor").RequestedAccent);
        Assert.Equal(21, ThemePalette.Contrast(Colors.Black, Colors.White), 3);
    }

    [Fact] public void LightAndDarkSwitchProducesTheExpectedVariant()
    {
        var target = new RecordingTarget(); var service = new ThemeService(target);
        var light = service.Apply(new AppearanceSettings(ThemeMode.Light));
        Assert.False(light.IsDark); Assert.False(service.EffectiveDark); Assert.True(ThemePalette.Luminance(light["BackgroundPrimary"]) > 0.8);
        var dark = service.Apply(new AppearanceSettings(ThemeMode.Dark));
        Assert.True(dark.IsDark); Assert.True(ThemePalette.Luminance(dark["BackgroundPrimary"]) < 0.02);
        Assert.Equal(2, target.Applied.Count); Assert.Same(dark, service.Palette);
        Assert.True(ThemePalette.Luminance(light["TextPrimary"]) < ThemePalette.Luminance(dark["TextPrimary"]));
    }

    [Fact] public void FollowingWindowsReactsToSystemChangesOnlyInSystemMode()
    {
        var target = new RecordingTarget(); var system = new FakeSystem { PrefersDark = false }; using var service = new ThemeService(target, system);
        Assert.False(service.Apply(new AppearanceSettings(ThemeMode.System)).IsDark);
        system.Change(true);
        Assert.True(service.EffectiveDark); Assert.Equal(2, target.Applied.Count);
        system.Change(null); Assert.True(service.EffectiveDark); // no report from Windows → dark default
        service.Apply(new AppearanceSettings(ThemeMode.Light)); var count = target.Applied.Count;
        system.Change(true); Assert.Equal(count, target.Applied.Count); Assert.False(service.EffectiveDark);
        Assert.True(ThemeService.ResolveDark(ThemeMode.Dark, false)); Assert.False(ThemeService.ResolveDark(ThemeMode.Light, true));
    }

    [Fact] public async Task PreferencesPersistInExistingSettingsAndAreRestoredOnNextStart()
    {
        var (f, store, vm) = await CreateAsync();
        await using (f)
        {
            await store.SaveAsync(vm.Settings with { Volume = .41f, YouTubeCookieFile = @"C:\pasta com espaço\cookies.txt" });
            await vm.DisposeAsync();
            await using (var first = new MainViewModel(new QueueTests.TestAudio(), new Scanner(), store, f.Logger))
            {
                await first.InitializeAsync();
                first.ThemeModeIndex = (int)ThemeMode.Light; first.SelectedAccent = first.AccentChoices.Single(c => c.Key == "purple");
                first.DensityIndex = (int)UiDensity.Compact; first.ReducedMotion = true;
            }
            var saved = await store.LoadAsync();
            Assert.Equal(new AppearanceSettings(ThemeMode.Light, "purple", null, UiDensity.Compact, true), saved.Appearance);
            Assert.Equal(.41f, saved.Volume); Assert.Equal(@"C:\pasta com espaço\cookies.txt", saved.YouTubeCookieFile);
            await using var second = new MainViewModel(new QueueTests.TestAudio(), new Scanner(), store, f.Logger);
            await second.InitializeAsync();
            Assert.Equal((int)ThemeMode.Light, second.ThemeModeIndex); Assert.Equal("purple", second.SelectedAccent?.Key);
            Assert.True(second.CompactDensity); Assert.True(second.ReducedMotion); Assert.Equal("Claro", second.EffectiveThemeLabel);
        }
    }

    [Fact] public async Task CustomColourIsPersistedOnlyWhenValid()
    {
        var (f, store, vm) = await CreateAsync();
        await using (f) await using (vm)
        {
            vm.SelectedAccent = vm.AccentChoices.Single(c => c.IsCustom);
            Assert.True(vm.IsCustomAccent); Assert.True(vm.AppearanceNeedsAttention); Assert.Equal("ahnsf", vm.Appearance.AccentPreset);
            vm.CustomAccentText = "#12345Z";
            Assert.True(vm.AppearanceNeedsAttention); Assert.Contains("inválida", vm.AppearanceFeedback); Assert.Null(vm.Appearance.CustomAccent);
            vm.CustomAccentText = "#e5a";
            Assert.False(vm.AppearanceNeedsAttention); Assert.Equal("custom", vm.Appearance.AccentPreset); Assert.Equal("#EE55AA", vm.Appearance.CustomAccent);
            Assert.Equal("#EE55AA", vm.Appearance.AccentHex);
            vm.CustomAccentText = "lixo";
            Assert.Equal("#EE55AA", vm.Appearance.CustomAccent);
            await vm.DisposeAsync();
            Assert.Equal("#EE55AA", (await store.LoadAsync()).Appearance?.CustomAccent);
        }
    }

    [Fact] public async Task OlderSettingsWithoutAppearanceMigrateToDefaultsAndInvalidValuesAreRepaired()
    {
        await using var f = new LibraryFixture();
        async Task WriteJson(string json)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.Database }.ToString());
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS Settings (Id INTEGER PRIMARY KEY CHECK(Id=1), Json TEXT NOT NULL); INSERT INTO Settings(Id,Json) VALUES(1,$json) ON CONFLICT(Id) DO UPDATE SET Json=$json";
            command.Parameters.AddWithValue("$json", json); await command.ExecuteNonQueryAsync();
        }
        var store = new SqliteSettingsStore(f.Database);
        // JSON written by 0.8.2 (no Appearance property).
        await WriteJson("""{"MusicDirectory":null,"Volume":0.22,"Width":1200,"Height":800,"X":null,"Y":null,"Shuffle":true,"Repeat":1,"AutoStartEnabled":false,"StartMinimized":false,"CloseBehavior":0,"CloseNoticeShown":true,"AudioDevice":null,"HistoryRetentionDays":180,"YouTubeCookieFile":null}""");
        var old = await store.LoadAsync();
        Assert.Null(old.Appearance); Assert.Equal(.22f, old.Volume); Assert.Equal(180, old.HistoryRetentionDays); Assert.True(old.Shuffle);
        await using (var vm = new MainViewModel(new QueueTests.TestAudio(), new Scanner(), store, f.Logger))
        {
            await vm.InitializeAsync();
            Assert.Equal(new AppearanceSettings(), vm.Appearance); Assert.Equal(0, vm.ThemeModeIndex); Assert.Equal("ahnsf", vm.SelectedAccent?.Key);
            Assert.False(vm.CompactDensity); Assert.False(vm.ReducedMotion); Assert.Equal(.22f, vm.Settings.Volume);
        }
        // Hand-edited or future values.
        await WriteJson("""{"Volume":0.3,"Appearance":{"Theme":99,"AccentPreset":"inexistente","CustomAccent":"#zzz","Density":7,"ReducedMotion":true}}""");
        await using (var vm = new MainViewModel(new QueueTests.TestAudio(), new Scanner(), store, f.Logger))
        {
            await vm.InitializeAsync();
            Assert.Equal(new AppearanceSettings(ReducedMotion: true), vm.Appearance);
            Assert.Equal(0, vm.ThemeModeIndex); Assert.Equal(0, vm.DensityIndex); Assert.True(vm.ReducedMotion);
        }
        // "Personalizada" without a valid hex falls back to the AHNSF preset.
        Assert.Equal("ahnsf", new AppearanceSettings(AccentPreset: "custom").Normalize().AccentPreset);
    }

    [Fact] public async Task ThemeChangesApplyAtRuntimeWithoutRestart()
    {
        var target = new RecordingTarget(); var system = new FakeSystem { PrefersDark = true };
        var (f, _, vm) = await CreateAsync(new ThemeService(target, system));
        await using (f) await using (vm)
        {
            var initial = target.Applied.Count; Assert.True(initial >= 1); Assert.True(target.Applied[^1].Palette.IsDark);
            var changed = new List<string?>(); vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            vm.ThemeModeIndex = (int)ThemeMode.Light;
            Assert.Equal(initial + 1, target.Applied.Count); Assert.False(target.Applied[^1].Palette.IsDark); Assert.Equal("Claro", vm.EffectiveThemeLabel);
            vm.SelectedAccent = vm.AccentChoices.Single(c => c.Key == "orange");
            Assert.Equal("#F08A3C", target.Applied[^1].Palette.RequestedAccent);
            vm.ThemeModeIndex = (int)ThemeMode.System; Assert.True(target.Applied[^1].Palette.IsDark);
            system.Change(false); Assert.False(target.Applied[^1].Palette.IsDark); Assert.Equal("Claro", vm.EffectiveThemeLabel);
            Assert.Contains(nameof(MainViewModel.EffectiveThemeLabel), changed);
            vm.RestoreAppearanceCommand.Execute(null);
            Assert.Equal(new AppearanceSettings(), vm.Appearance); Assert.True(target.Applied[^1].Palette.IsDark); Assert.Equal("ahnsf", vm.SelectedAccent?.Key);
        }
    }

    [Fact] public async Task DensityAndReducedMotionAreExposedForTheWindow()
    {
        var target = new RecordingTarget();
        var (f, _, vm) = await CreateAsync(new ThemeService(target));
        await using (f) await using (vm)
        {
            var changed = new List<string?>(); vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            Assert.False(vm.CompactDensity);
            vm.DensityIndex = (int)UiDensity.Compact;
            Assert.True(vm.CompactDensity); Assert.Contains(nameof(MainViewModel.CompactDensity), changed); Assert.Equal(UiDensity.Compact, target.Applied[^1].Appearance.Density);
            vm.ReducedMotion = true; Assert.True(target.Applied[^1].Appearance.ReducedMotion);
            vm.DensityIndex = (int)UiDensity.Comfortable; Assert.False(vm.CompactDensity); Assert.Equal(UiDensity.Comfortable, vm.Appearance.Density);
        }
    }

    [Fact] public async Task AppearanceChangesDoNotTouchThePlayer()
    {
        await using var f = new LibraryFixture();
        f.WriteTrack("a.mp3", "A"); f.WriteTrack("b.mp3", "B"); await f.StartAsync();
        var store = new SqliteSettingsStore(f.Database); await store.SaveAsync(new(f.Music, Volume: .5f));
        var audio = new QueueTests.TestAudio(); var playlists = new SqlitePlaylistService(f.Database, f.Repository);
        await using var vm = new MainViewModel(audio, new Scanner(), store, f.Logger, f.Service, playlists);
        vm.AttachTheme(new ThemeService(new RecordingTarget()));
        await vm.InitializeAsync();
        vm.SelectedTrack = vm.Tracks.First(); vm.PlaySelectedCommand.Execute(null);
        audio.Position = TimeSpan.FromSeconds(17);
        var (state, loads, volume, position, current, queue) = (audio.State, audio.Loads, audio.Volume, audio.Position, vm.CurrentTrack, vm.QueueRows.Select(r => r.Id).ToArray());
        Assert.Equal(PlayerState.Playing, state);
        vm.ThemeModeIndex = 1; vm.SelectedAccent = vm.AccentChoices[3]; vm.DensityIndex = 1; vm.ReducedMotion = true;
        vm.SelectedAccent = vm.AccentChoices.Single(c => c.IsCustom); vm.CustomAccentText = "#123456"; vm.RestoreAppearanceCommand.Execute(null);
        Assert.Equal(state, audio.State); Assert.Equal(loads, audio.Loads); Assert.Equal(volume, audio.Volume); Assert.Equal(position, audio.Position);
        Assert.Same(current, vm.CurrentTrack); Assert.Equal(queue, vm.QueueRows.Select(r => r.Id).ToArray()); Assert.True(vm.IsPlaying);
    }

    [Fact] public async Task AppearanceChangesAreNotManualInterventionsForTheScheduler()
    {
        await using var f = new LibraryFixture();
        var playlists = new SqlitePlaylistService(f.Database, f.Repository); var schedule = new SqliteScheduleService(f.Database, playlists);
        var audio = new QueueTests.TestAudio { Volume = .22f }; using var player = new PlaybackService(audio, playlists);
        var clock = new FixedClock();
        await using var coordinator = new ScheduleCoordinator(schedule, playlists, player, clock, new ScheduleEvaluator());
        var playlist = await playlists.CreateAsync("Ambiente");
        f.WriteTrack("a.mp3", "A"); f.WriteTrack("b.mp3", "B");
        await playlists.AddAsync(playlist.Id, (await f.StartAsync()).Tracks.Select(t => t.Id).ToArray());
        var now = DateTime.UtcNow;
        await schedule.SaveAsync(new ScheduleRule(Guid.NewGuid(), "Dia", playlist.Id, ScheduleDays.All, new TimeOnly(8, 0), new TimeOnly(18, 0), 62, 0, true, now, now));
        await schedule.SetModeAsync(OperationMode.Automatic);
        var store = new SqliteSettingsStore(f.Database); await store.SaveAsync(new(Volume: .22f));
        await using var vm = new MainViewModel(audio, new Scanner(), store, f.Logger, playlists: playlists, playback: player, scheduleStore: schedule, scheduler: coordinator);
        vm.AttachTheme(new ThemeService(new RecordingTarget()));
        await vm.InitializeAsync();
        Assert.Equal(PlayerState.Playing, player.State); Assert.False(coordinator.Status.IsSuspended);
        var loads = audio.Loads;
        vm.ThemeModeIndex = 2; vm.SelectedAccent = vm.AccentChoices[1]; vm.DensityIndex = 1; vm.ReducedMotion = true; vm.RestoreAppearanceCommand.Execute(null);
        await coordinator.EvaluateAsync();
        Assert.False(coordinator.Status.IsSuspended); Assert.Equal(OperationMode.Automatic, coordinator.Status.Mode);
        Assert.Equal(PlayerState.Playing, player.State); Assert.Equal(loads, audio.Loads); Assert.Equal(.62f, audio.Volume, 3);
    }
    private sealed class FixedClock : IScheduleClock
    { public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-10-05T12:00+00:00"); public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Utc; }
}
