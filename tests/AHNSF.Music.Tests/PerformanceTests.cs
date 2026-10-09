using System.Diagnostics;
using AHNSF.Music.Core;
using Xunit;
using Xunit.Abstractions;

namespace AHNSF.Music.Tests;
public sealed class PerformanceTests(ITestOutputHelper output)
{
    [Fact] public async Task MeasureTwoThousandLocalMp3AndUnchangedReconciliation()
    {
        await using var f = new LibraryFixture(watcher:false);
        for (int i=0;i<2000;i++) f.WriteTrack($"disc{i/100}/song{i:D4}.mp3", $"Faixa {i:D4}", $"Artista {i%20}", $"Álbum {i%40}");
        var clock=Stopwatch.StartNew(); var first=await f.StartAsync(); var initialMs=clock.ElapsedMilliseconds;
        Assert.Equal(2000,first.Count); Assert.Equal(2000,f.Metadata.Count);
        clock.Restart(); var unchanged=await f.Service.ReconcileAsync(false,CancellationToken.None); var unchangedMs=clock.ElapsedMilliseconds;
        Assert.Equal(0,unchanged.Processed); Assert.Equal(2000,f.Metadata.Count);
        clock.Restart(); var search=await f.Service.QueryAsync(new("Faixa 1999"),CancellationToken.None); var searchMs=clock.ElapsedMilliseconds;
        Assert.Single(search.Tracks);
        output.WriteLine($"2000 MP3 silenciosos locais: indexação inicial={initialMs} ms; reconciliação inalterada={unchangedMs} ms (0 metadados relidos); busca={searchMs} ms. Ensaio local, não benchmark de produção.");
    }
}
