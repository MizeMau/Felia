using F5TTS_Console;
using System.Diagnostics;

namespace Felia
{
    internal class Program
    {
        private static TtsClient ttsClient;
        private static LlmService llm;
        private static Whisper whisper;

        private static CancellationTokenSource cts;

        public static string Appdata = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Felia");
        static async Task Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            cts = new CancellationTokenSource();
            using var server = new ServerManager();

            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("\n[App] Shutting down …");
                server.Dispose();
                Environment.Exit(0);
            };

            try
            {
                await server.StartAsync(cts.Token);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"\n[Error] Could not start server: {ex.Message}");
                Console.Error.WriteLine("  → Did you run install.bat?");
                Console.ResetColor();
                return;
            }

            ttsClient = new TtsClient(server.BaseUrl);
            // Show what GPU is being used
            try
            {
                var health = await ttsClient.GetHealthAsync(cts.Token);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[GPU]  {health.CudaDeviceName} ({health.Device.ToUpper()})");
                Console.ResetColor();
            }
            catch { /* non-fatal */ }

            llm = await LlmService.CreateAsync();

            Console.WriteLine("LLM ready.\n");

            Console.Clear();

            whisper = new Whisper();
            whisper.SpeakEvent += SpeakInvoked;
            await whisper.Init();

            Console.ReadKey();
        }

        private static async void SpeakInvoked(object? sender, string speak)
        {
            if (speak == "[BLANK_AUDIO]") return;

            whisper.Pause();
            var reply = await llm.ChatAsync(speak);
            Console.WriteLine($"Felia answered: {reply}");
            float speed = 1f; // You can adjust the speed as needed
            await SpeakAsync(reply, speed, null, ttsClient, cts.Token);
            whisper.Resume();
        }

        static async Task SpeakAsync(string text, float speed, string? savePath, TtsClient client, CancellationToken ct)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"  [TTS] Synthesising ({speed:F1}×) … ");
            Console.ResetColor();

            var sw = Stopwatch.StartNew();

            byte[] wavBytes;
            try
            {
                wavBytes = await client.SynthesiseAsync(text, speed, ct);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("cancelled.");
                return;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n  [Error] {ex.Message}");
                Console.ResetColor();
                return;
            }

            sw.Stop();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"done ({sw.ElapsedMilliseconds} ms, {wavBytes.Length / 1024} KB)");
            Console.ResetColor();

            if (savePath is not null)
            {
                await AudioPlayer.SaveAsync(wavBytes, savePath, ct);
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine($"  [Saved] {Path.GetFullPath(savePath)}");
                Console.ResetColor();
            }

            AudioPlayer.Play(wavBytes);
        }
    }
}