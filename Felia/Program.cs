// Program.cs
//
// Streams a .wav file to the STM32 amp-test firmware over the serial port
// wired to USART2 (PA2/PA3, ST-Link VCP on Nucleo boards). The STM32 side
// receives raw 16-bit stereo PCM @ 16kHz and forwards it directly to the
// MAX98357 I2S amp. USART1 is left free for the ESP32 / debug use.
//
// Protocol (must match the STM32 firmware):
//   1. PC   -> STM32 : 0xAA                          (handshake)
//   2. STM32 -> PC   : 0x55                          (ready)
//   3. PC   -> STM32 : 4 bytes little-endian uint32   (total frame count)
//   4. PC   -> STM32 : raw 16-bit stereo interleaved PCM data
//   5. STM32 -> PC   : 0x44 ('D')                     (done)
//
// NuGet package required: NAudio  (Install-Package NAudio)

using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using NAudio.Wave;

namespace Felia
{
    public class Program
    {
        // ---- Config: match these to the STM32 firmware ----
        const int TargetSampleRate = 16000;   // matches I2S_AUDIOFREQ_16K
        const int TargetChannels = 2;          // stereo interleaved frames
        const int TargetBitsPerSample = 16;    // I2S_DATAFORMAT_16B

        const byte HANDSHAKE_BYTE = 0xAA;
        const byte READY_BYTE = 0x55;
        const byte DONE_BYTE = 0x44; // 'D'

        const int ChunkFrames = 256; // must match AUDIO_CHUNK_FRAMES on STM32

        static async Task Main(string[] args)
        {
            Console.WriteLine("=== STM32 MAX98357 Amp Streaming Test ===");

            string wavPath = @"C:\Users\Mize\Project\Felia\Felia\TTS\ref.wav";
            string portName = "COM4";

            if (!File.Exists(wavPath))
            {
                Console.WriteLine($"File not found: {wavPath}");
                return;
            }

            Console.WriteLine("Loading and converting audio...");
            byte[] pcmData = LoadAndConvertWav(wavPath, out int frameCount);
            Console.WriteLine($"Prepared {frameCount} stereo frames " +
                               $"({pcmData.Length} bytes, {(double)pcmData.Length / 1024:F1} KB)");

            using var port = new SerialPort(portName, 921600, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 3000,
                WriteTimeout = 5000,
                Handshake = Handshake.None,
                DtrEnable = false,
                RtsEnable = false
            };

            try
            {
                port.Open();
                Console.WriteLine($"Opened {portName} @ 921600 baud");

                // Opening the port can reset the board (ST-Link VCP) or the
                // board may still be mid-boot; give it a moment then flush
                // any bytes that arrived before we start the handshake.
                Thread.Sleep(300);
                port.DiscardInBuffer();

                bool ok = Handshake_(port);
                if (!ok)
                {
                    Console.WriteLine("First handshake attempt failed, retrying...");
                    port.DiscardInBuffer();
                    Thread.Sleep(200);
                    ok = Handshake_(port);
                }

                if (!ok)
                {
                    Console.WriteLine("STM32 did not respond to handshake.");
                    Console.WriteLine("Checklist:");
                    Console.WriteLine("  - Audio protocol must be on USART2 (PA2/PA3), not USART1");
                    Console.WriteLine("  - Baud rate here (921600) must match MX_USART2_UART_Init in main.c");
                    Console.WriteLine("  - No debug text should ever be sent on USART2 - only USART1");
                    Console.WriteLine("  - Confirm the STM32 is actually running (check USART1 debug output in a terminal)");
                    return;
                }

                Console.WriteLine("Handshake OK. Sending frame count...");
                SendFrameCount(port, frameCount);

                Console.WriteLine("Streaming audio...");
                StreamAudio(port, pcmData, frameCount);

                Console.WriteLine("Waiting for STM32 done signal...");
                int result = port.ReadByte();
                if (result == DONE_BYTE)
                {
                    Console.WriteLine("Done! Amp should have played the audio.");
                }
                else
                {
                    Console.WriteLine($"Unexpected response: 0x{result:X2}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }

        static string PromptForPath()
        {
            Console.Write("Path to .wav file: ");
            return Console.ReadLine()?.Trim('"') ?? "";
        }

        static string PromptForPort()
        {
            Console.WriteLine("Available ports: " + string.Join(", ", SerialPort.GetPortNames()));
            Console.Write("COM port (e.g. COM5): ");
            return Console.ReadLine()?.Trim() ?? "";
        }

        /// <summary>
        /// Loads a wav file of any format and converts it to 16kHz, 16-bit,
        /// stereo PCM (mono sources are duplicated to both channels).
        /// </summary>
        static byte[] LoadAndConvertWav(string path, out int frameCount)
        {
            using var reader = new AudioFileReader(path); // decodes to IEEE float internally

            var targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(TargetSampleRate, TargetChannels);

            // Resample (and remix channels) to the target format using the
            // media foundation resampler, then convert float -> 16-bit PCM.
            using var resampler = new MediaFoundationResampler(reader, targetFormat)
            {
                ResamplerQuality = 60
            };

            using var pcmStream = new MemoryStream();
            var floatBuffer = new byte[targetFormat.AverageBytesPerSecond];
            int bytesRead;
            while ((bytesRead = resampler.Read(floatBuffer, 0, floatBuffer.Length)) > 0)
            {
                pcmStream.Write(floatBuffer, 0, bytesRead);
            }

            // pcmStream now holds interleaved 32-bit float samples, stereo, 16kHz.
            // Convert to signed 16-bit PCM.
            byte[] floatBytes = pcmStream.ToArray();
            int sampleCount = floatBytes.Length / 4; // 4 bytes per float sample
            byte[] pcm16 = new byte[sampleCount * 2]; // 2 bytes per 16-bit sample

            for (int i = 0; i < sampleCount; i++)
            {
                float sample = BitConverter.ToSingle(floatBytes, i * 4);
                sample = Math.Clamp(sample, -1.0f, 1.0f);
                short s16 = (short)(sample * short.MaxValue);
                pcm16[i * 2] = (byte)(s16 & 0xFF);
                pcm16[i * 2 + 1] = (byte)((s16 >> 8) & 0xFF);
            }

            frameCount = sampleCount / TargetChannels; // stereo frames
            return pcm16;
        }

        static bool Handshake_(SerialPort port)
        {
            port.DiscardInBuffer();
            port.DiscardOutBuffer();

            port.Write(new byte[] { HANDSHAKE_BYTE }, 0, 1);

            try
            {
                int response = port.ReadByte();
                return response == READY_BYTE;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        static void SendFrameCount(SerialPort port, int frameCount)
        {
            byte[] header = BitConverter.GetBytes((uint)frameCount); // little-endian on x86/x64
            port.Write(header, 0, 4);
        }

        //static void StreamAudio(SerialPort port, byte[] pcmData, int frameCount)
        //{
        //    const int bytesPerFrame = TargetChannels * (TargetBitsPerSample / 8); // 4 bytes/frame
        //    int chunkBytes = ChunkFrames * bytesPerFrame;

        //    int offset = 0;
        //    int framesSent = 0;
        //    var sw = System.Diagnostics.Stopwatch.StartNew();

        //    while (offset < pcmData.Length)
        //    {
        //        int remaining = pcmData.Length - offset;
        //        int thisChunk = Math.Min(chunkBytes, remaining);

        //        port.Write(pcmData, offset, thisChunk);

        //        offset += thisChunk;
        //        framesSent += thisChunk / bytesPerFrame;

        //        if (framesSent % (ChunkFrames * 20) == 0)
        //        {
        //            double pct = 100.0 * framesSent / frameCount;
        //            Console.Write($"\r  {pct:F1}%  ({framesSent}/{frameCount} frames)   ");
        //        }
        //    }

        //    sw.Stop();
        //    Console.WriteLine($"\r  100.0%  ({framesSent}/{frameCount} frames)   ");
        //    Console.WriteLine($"Sent in {sw.Elapsed.TotalSeconds:F2}s " +
        //                       $"({pcmData.Length / 1024.0 / sw.Elapsed.TotalSeconds:F1} KB/s)");
        //}

        
        static void StreamAudio(SerialPort port, byte[] pcmData, int frameCount)
        {
            const int bytesPerFrame =
                TargetChannels * (TargetBitsPerSample / 8); // 4 bytes/frame

            const int chunkBytes = ChunkFrames * bytesPerFrame; // 1024 bytes

            // 16 kHz stereo 16-bit = 64,000 bytes/sec.
            const double BytesPerSecond =
                (double)TargetSampleRate * TargetChannels *
                (TargetBitsPerSample / 8);

            int offset = 0;
            int framesSent = 0;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            /*
             * Each chunk contains 256 stereo frames:
             *
             *   256 frames / 16000 frames/sec = 16 ms
             *
             * We therefore schedule each chunk 16 ms apart.
             *
             * This prevents the PC from dumping the entire WAV into the
             * Windows SerialPort output buffer faster than the STM32 can
             * consume it.
             *
             * We intentionally start immediately with the first chunk.
             */
            long nextSendTimeTicks = sw.ElapsedTicks;

            while (offset < pcmData.Length)
            {
                int remaining = pcmData.Length - offset;
                int thisChunk = Math.Min(chunkBytes, remaining);

                // Wait until this chunk's scheduled transmission time.
                while (true)
                {
                    long nowTicks = sw.ElapsedTicks;

                    if (nowTicks >= nextSendTimeTicks)
                        break;

                    long remainingTicks = nextSendTimeTicks - nowTicks;

                    // Sleep for most of the remaining time, leaving a small
                    // margin for accurate scheduling.
                    double remainingMs =
                        remainingTicks * 1000.0 /
                        System.Diagnostics.Stopwatch.Frequency;

                    if (remainingMs > 2.0)
                    {
                        Thread.Sleep((int)(remainingMs - 1.0));
                    }
                    else
                    {
                        Thread.SpinWait(100);
                    }
                }

                port.Write(pcmData, offset, thisChunk);

                offset += thisChunk;
                framesSent += thisChunk / bytesPerFrame;

                // Schedule the next chunk according to the AUDIO sample clock,
                // rather than according to how long SerialPort.Write() happened
                // to take.
                double chunkDurationSeconds = thisChunk / BytesPerSecond;

                nextSendTimeTicks +=
                    (long)(chunkDurationSeconds *
                           System.Diagnostics.Stopwatch.Frequency);

                if (framesSent % (ChunkFrames * 20) == 0)
                {
                    double pct = 100.0 * framesSent / frameCount;

                    double elapsedSeconds = sw.Elapsed.TotalSeconds;

                    Console.Write(
                        $"\r  {pct:F1}%  " +
                        $"({framesSent}/{frameCount} frames)   " +
                        $"elapsed {elapsedSeconds:F1}s   ");
                }
            }

            sw.Stop();

            Console.WriteLine(
                $"\r  100.0%  ({framesSent}/{frameCount} frames)   ");

            Console.WriteLine(
                $"Streaming completed in {sw.Elapsed.TotalSeconds:F2}s " +
                $"({pcmData.Length / 1024.0 / sw.Elapsed.TotalSeconds:F1} KB/s)");
        }
    }
}