using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

namespace Felia
{
    internal class Program
    {
        const string ModelPath = "ggml-base.en.bin";
        const int SampleRate = 16000;
        const double SilenceRms = 0.015;   // tune up/down if too sensitive
        const int SilenceMs = 1200;    // ms of silence before transcribing

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            // ── 1. Download model on first run ───────────────────────────────
            if (!File.Exists(ModelPath))
            {
                Console.WriteLine("Downloading Whisper base.en model…");
                await using var src = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Base);
                await using var dest = File.OpenWrite(ModelPath);
                await src.CopyToAsync(dest);
                Console.WriteLine("Model saved.\n");
            }

            // ── 2. Build Whisper processor ───────────────────────────────────
            using var factory = WhisperFactory.FromPath(ModelPath, new WhisperFactoryOptions
            {
                UseGpu = true,
                GpuDevice = 0   // 0 = first GPU
            });
            using var processor = factory.CreateBuilder()
                                         .WithLanguage("en")
                                         .Build();

            // ── 3. Shared state ──────────────────────────────────────────────
            var audioBuffer = new List<float>();
            var @lock = new object();
            bool isSpeaking = false;
            bool isTranscribing = false;
            DateTime? silenceAt = null;

            // ── 4. Wire up microphone ────────────────────────────────────────
            var waveFormat = new WaveFormat(SampleRate, 16, 1);
            using var mic = new WaveInEvent
            {
                WaveFormat = waveFormat,
                BufferMilliseconds = 30
            };

            mic.DataAvailable += (_, e) =>
            {
                // Convert 16-bit PCM → float samples
                int count = e.BytesRecorded / 2;
                var samples = new float[count];
                for (int i = 0; i < count; i++)
                    samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;

                // Compute RMS (volume)
                double sumSq = 0;
                foreach (var s in samples) sumSq += s * s;
                double rms = Math.Sqrt(sumSq / count);

                lock (@lock)
                {
                    if (rms > SilenceRms)                     // ── voice detected
                    {
                        if (!isSpeaking)
                        {
                            isSpeaking = true;
                            Console.Write("\r🎙  Speaking…        ");
                        }
                        silenceAt = null;
                        audioBuffer.AddRange(samples);
                    }
                    else if (isSpeaking)                      // ── trailing silence
                    {
                        audioBuffer.AddRange(samples);        // keep tail for natural endings
                        silenceAt ??= DateTime.UtcNow;

                        bool silenceLongEnough =
                            (DateTime.UtcNow - silenceAt.Value).TotalMilliseconds >= SilenceMs;

                        if (silenceLongEnough && !isTranscribing)
                        {
                            isSpeaking = false;
                            isTranscribing = true;
                            silenceAt = null;

                            float[] clip = audioBuffer.ToArray();
                            audioBuffer.Clear();

                            // Fire transcription off the audio thread
                            Task.Run(async () =>
                            {
                                Console.Write("\r⏳ Transcribing…    ");

                                var sb = new System.Text.StringBuilder();
                                await foreach (var seg in processor.ProcessAsync(clip))
                                    sb.Append(seg.Text);

                                string text = sb.ToString().Trim();
                                if (text.Length > 0)
                                    Console.WriteLine($"📝 {text}");

                                lock (@lock) isTranscribing = false;
                                Console.Write("\r🎤 Listening…        ");
                            });
                        }
                    }
                }
            };

            // ── 5. Start ─────────────────────────────────────────────────────
            Console.WriteLine("\r🎤 Listening…  (press Enter to quit)");
            mic.StartRecording();
            Console.ReadLine();
            mic.StopRecording();
        }
    }
}