using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace F5TTS_Console;

/// <summary>
/// Thin HTTP client for the local F5-TTS FastAPI server.
/// </summary>
internal sealed class TtsClient : IDisposable
{
    private readonly HttpClient _http;

    public TtsClient(string baseUrl)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            // F5-TTS inference on large texts can take a few seconds even on a 4090
            Timeout = TimeSpan.FromSeconds(120),
        };
    }

    // -----------------------------------------------------------------------
    // /health
    // -----------------------------------------------------------------------

    public async Task<HealthInfo> GetHealthAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync("/health", ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<HealthInfo>(ct))!;
    }

    // -----------------------------------------------------------------------
    // /tts  —  returns raw WAV bytes
    // -----------------------------------------------------------------------

    /// <summary>
    /// Sends <paramref name="text"/> to the TTS server and returns WAV audio bytes.
    /// </summary>
    /// <param name="text">The text to synthesise.</param>
    /// <param name="speed">Playback speed multiplier (0.5 – 2.0).</param>
    public async Task<byte[]> SynthesiseAsync(
        string text,
        float  speed = 1.0f,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text must not be empty.", nameof(text));

        var payload = new TtsRequest { Text = text, Speed = speed };

        var resp = await _http.PostAsJsonAsync("/tts", payload, ct);

        if (!resp.IsSuccessStatusCode)
        {
            string body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"TTS server returned {(int)resp.StatusCode}: {body}");
        }

        var ttsResp = (await resp.Content.ReadFromJsonAsync<TtsResponse>(ct))!;

        return Convert.FromBase64String(ttsResp.AudioB64);
    }

    public void Dispose() => _http.Dispose();

    // -----------------------------------------------------------------------
    // DTOs
    // -----------------------------------------------------------------------

    private sealed class TtsRequest
    {
        [JsonPropertyName("text")]  public string Text  { get; init; } = "";
        [JsonPropertyName("speed")] public float  Speed { get; init; } = 1.0f;
    }

    private sealed class TtsResponse
    {
        [JsonPropertyName("audio_b64")]   public string AudioB64   { get; init; } = "";
        [JsonPropertyName("sample_rate")] public int    SampleRate { get; init; }
        [JsonPropertyName("device")]      public string Device     { get; init; } = "";
    }

    public sealed class HealthInfo
    {
        [JsonPropertyName("status")]           public string Status         { get; init; } = "";
        [JsonPropertyName("device")]           public string Device         { get; init; } = "";
        [JsonPropertyName("cuda_device_name")] public string CudaDeviceName { get; init; } = "";
        [JsonPropertyName("model_loaded")]     public bool   ModelLoaded    { get; init; }
    }
}
