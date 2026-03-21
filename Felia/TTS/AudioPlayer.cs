using NAudio.Wave;

namespace Felia;

/// <summary>
/// Plays in-memory WAV audio synchronously using NAudio.
/// Works on Windows with any default audio device.
/// </summary>
internal static class AudioPlayer
{
    /// <summary>
    /// Plays <paramref name="wavBytes"/> synchronously (blocks until playback finishes).
    /// </summary>
    public static void Play(byte[] wavBytes)
    {
        using var ms     = new MemoryStream(wavBytes);
        using var reader = new WaveFileReader(ms);
        using var output = new WaveOutEvent();

        output.Init(reader);
        output.Volume = 0.5f; // Max volume
        // Use a ManualResetEvent so we can wait for playback to finish cleanly.
        using var finished = new ManualResetEventSlim(false);
        output.PlaybackStopped += (_, _) => finished.Set();

        output.Play();
        finished.Wait();
    }

    /// <summary>
    /// Saves <paramref name="wavBytes"/> to <paramref name="filePath"/>.
    /// </summary>
    public static async Task SaveAsync(byte[] wavBytes, string filePath, CancellationToken ct = default)
    {
        await File.WriteAllBytesAsync(filePath, wavBytes, ct);
    }
}
