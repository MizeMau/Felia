using F5TTS_Console;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Felia
{
    internal class Program
    {
        //private static TtsClient ttsClient;
        //private static LlmService llm;
        //private static Whisper whisper;

        //private static CancellationTokenSource cts;

        //public static string Appdata = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Felia");
        //static async Task Main(string[] args)
        //{
        //    Console.OutputEncoding = System.Text.Encoding.UTF8;

        //    cts = new CancellationTokenSource();
        //    using var server = new ServerManager();

        //    Console.CancelKeyPress += (_, e) =>
        //    {
        //        e.Cancel = true;
        //        Console.WriteLine("\n[App] Shutting down …");
        //        server.Dispose();
        //        Environment.Exit(0);
        //    };

        //    try
        //    {
        //        await server.StartAsync(cts.Token);
        //    }
        //    catch (Exception ex)
        //    {
        //        Console.ForegroundColor = ConsoleColor.Red;
        //        Console.Error.WriteLine($"\n[Error] Could not start server: {ex.Message}");
        //        Console.Error.WriteLine("  → Did you run install.bat?");
        //        Console.ResetColor();
        //        return;
        //    }

        //    ttsClient = new TtsClient(server.BaseUrl);
        //    // Show what GPU is being used
        //    try
        //    {
        //        var health = await ttsClient.GetHealthAsync(cts.Token);
        //        Console.ForegroundColor = ConsoleColor.Cyan;
        //        Console.WriteLine($"[GPU]  {health.CudaDeviceName} ({health.Device.ToUpper()})");
        //        Console.ResetColor();
        //    }
        //    catch { /* non-fatal */ }

        //    llm = await LlmService.CreateAsync();

        //    Console.WriteLine("LLM ready.\n");

        //    Console.Clear();

        //    whisper = new Whisper();
        //    whisper.SpeakEvent += SpeakInvoked;
        //    await whisper.Init();

        //    Console.ReadKey();
        //}

        //private static async void SpeakInvoked(object? sender, string speak)
        //{
        //    if (speak == "[BLANK_AUDIO]") return;

        //    whisper.Pause();
        //    var reply = await llm.ChatAsync(speak);
        //    Console.WriteLine($"Felia answered: {reply}");
        //    float speed = 1f; // You can adjust the speed as needed
        //    await SpeakAsync(reply, speed, null, ttsClient, cts.Token);
        //    whisper.Resume();
        //}

        //static async Task SpeakAsync(string text, float speed, string? savePath, TtsClient client, CancellationToken ct)
        //{
        //    Console.ForegroundColor = ConsoleColor.DarkGray;
        //    Console.Write($"  [TTS] Synthesising ({speed:F1}×) … ");
        //    Console.ResetColor();

        //    var sw = Stopwatch.StartNew();

        //    byte[] wavBytes;
        //    try
        //    {
        //        wavBytes = await client.SynthesiseAsync(text, speed, ct);
        //    }
        //    catch (OperationCanceledException)
        //    {
        //        Console.WriteLine("cancelled.");
        //        return;
        //    }
        //    catch (Exception ex)
        //    {
        //        Console.ForegroundColor = ConsoleColor.Red;
        //        Console.WriteLine($"\n  [Error] {ex.Message}");
        //        Console.ResetColor();
        //        return;
        //    }

        //    sw.Stop();
        //    Console.ForegroundColor = ConsoleColor.Green;
        //    Console.WriteLine($"done ({sw.ElapsedMilliseconds} ms, {wavBytes.Length / 1024} KB)");
        //    Console.ResetColor();

        //    if (savePath is not null)
        //    {
        //        await AudioPlayer.SaveAsync(wavBytes, savePath, ct);
        //        Console.ForegroundColor = ConsoleColor.DarkCyan;
        //        Console.WriteLine($"  [Saved] {Path.GetFullPath(savePath)}");
        //        Console.ResetColor();
        //    }

        //    AudioPlayer.Play(wavBytes);
        //}

        const int Port = 5000;
        const int SampleRate = 16000;
        const short BitsPerSample = 16;
        const short Channels = 1;

        const byte PKT_AUDIO = 0;
        const byte PKT_START = 1;
        const byte PKT_END = 2;

        static void Main()
        {
            TcpListener listener = new TcpListener(IPAddress.Any, Port);
            listener.Start();
            Console.WriteLine($"Listening on port {Port}...");

            while (true)
            {
                Console.WriteLine("Waiting for ESP32 to connect...");
                using TcpClient client = listener.AcceptTcpClient();
                Console.WriteLine("ESP32 connected!");

                HandleClient(client);

                Console.WriteLine("ESP32 disconnected, waiting for reconnect...");
            }
        }

        static void HandleClient(TcpClient client)
        {
            using NetworkStream stream = client.GetStream();

            FileStream currentFile = null;
            long dataBytesWritten = 0;

            try
            {
                while (client.Connected)
                {
                    byte type = ReadByte(stream);
                    uint len = ReadUInt32LE(stream);
                    byte[] payload = len > 0 ? ReadExact(stream, (int)len) : Array.Empty<byte>();

                    switch (type)
                    {
                        case PKT_START:
                            currentFile?.Dispose(); // safety: close any stale file
                            string filename = $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.wav";
                            currentFile = new FileStream(filename, FileMode.Create, FileAccess.Write);
                            WriteWavHeaderPlaceholder(currentFile);
                            dataBytesWritten = 0;
                            Console.WriteLine($"Recording started: {filename}");
                            break;

                        case PKT_AUDIO:
                            if (currentFile != null)
                            {
                                currentFile.Write(payload, 0, payload.Length);
                                dataBytesWritten += payload.Length;
                            }
                            break;

                        case PKT_END:
                            if (currentFile != null)
                            {
                                FinalizeWavHeader(currentFile, dataBytesWritten);
                                currentFile.Dispose();
                                currentFile = null;
                                Console.WriteLine($"Recording saved ({dataBytesWritten} bytes)");
                            }
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Connection error: {ex.Message}");
            }
            finally
            {
                if (currentFile != null)
                {
                    // connection dropped mid-recording; still finalize what we have
                    FinalizeWavHeader(currentFile, dataBytesWritten);
                    currentFile.Dispose();
                }
                client.Close();
            }
        }

        static byte ReadByte(NetworkStream stream)
        {
            int b = stream.ReadByte();
            if (b == -1) throw new IOException("Connection closed");
            return (byte)b;
        }

        static uint ReadUInt32LE(NetworkStream stream)
        {
            byte[] buf = ReadExact(stream, 4);
            return (uint)(buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24));
        }

        static byte[] ReadExact(NetworkStream stream, int count)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buf, offset, count - offset);
                if (read == 0) throw new IOException("Connection closed mid-read");
                offset += read;
            }
            return buf;
        }

        static void WriteWavHeaderPlaceholder(FileStream fs)
        {
            // Write a 44-byte placeholder header; sizes get patched in FinalizeWavHeader
            BinaryWriter bw = new BinaryWriter(fs);
            bw.Write(new char[] { 'R', 'I', 'F', 'F' });
            bw.Write(0); // ChunkSize placeholder
            bw.Write(new char[] { 'W', 'A', 'V', 'E' });
            bw.Write(new char[] { 'f', 'm', 't', ' ' });
            bw.Write(16); // Subchunk1Size for PCM
            bw.Write((short)1); // AudioFormat = 1 (PCM)
            bw.Write(Channels);
            bw.Write(SampleRate);
            bw.Write(SampleRate * Channels * (BitsPerSample / 8)); // ByteRate
            bw.Write((short)(Channels * (BitsPerSample / 8))); // BlockAlign
            bw.Write(BitsPerSample);
            bw.Write(new char[] { 'd', 'a', 't', 'a' });
            bw.Write(0); // Subchunk2Size placeholder
            bw.Flush();
        }

        static void FinalizeWavHeader(FileStream fs, long dataBytes)
        {
            long chunkSize = 36 + dataBytes;

            fs.Seek(4, SeekOrigin.Begin);
            fs.Write(BitConverter.GetBytes((int)chunkSize), 0, 4);

            fs.Seek(40, SeekOrigin.Begin);
            fs.Write(BitConverter.GetBytes((int)dataBytes), 0, 4);

            fs.Flush();
        }
    }
}