using AHNSF.Music.Core;
using NAudio.Wave;

namespace AHNSF.Music.Audio;
public sealed class Mp3DurationReader : ITrackDurationReader
{
    public TimeSpan Read(string path)
    {
        using var reader = new Mp3FileReader(path);
        return reader.TotalTime;
    }
}
