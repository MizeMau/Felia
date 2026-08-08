using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Text;
using Whisper.net;
using Whisper.net.Ggml;

namespace Felia
{
    public class Whisper
    {
        readonly string ModelPath = "ggml-base.en.bin";

        private WhisperFactory? _Factory;
        private WhisperProcessor? _Processor;

        public Whisper(string? modelDirectory = null)
        {
            if (modelDirectory == null)
            {
                string appdataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                modelDirectory = Path.Combine(appdataPath, "Felia", "models", "whisper");
            }
            Directory.CreateDirectory(modelDirectory);

            ModelPath = Path.Combine(modelDirectory, "ggml-base.en.bin");
        }

        public async Task Init()
        {
            if (!File.Exists(ModelPath))
            {
                Program.WriteLine("Downloading Whisper base.en model…");
                await using var src = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.Base);
                await using var dest = File.OpenWrite(ModelPath);
                await src.CopyToAsync(dest);
                Program.WriteLine("Model saved.\n");
            }

            _Factory = WhisperFactory.FromPath(ModelPath, new WhisperFactoryOptions
            {
                UseGpu = true,
                GpuDevice = 0
            });
            _Processor = _Factory.CreateBuilder()
                                .WithLanguage("en")
                                .Build();
        }
        public void Dispose()
        {
            _Processor?.Dispose();
            _Factory?.Dispose();
        }

        public async Task<string> Transcribe(List<byte> audio)
        {
            int count = audio.Count / 2;
            var samples = new float[count];
            for (int i = 0; i < count; i++)
            {
                short s = (short)(audio[i * 2] | (audio[i * 2 + 1] << 8)); // little-endian int16
                samples[i] = s / 32768f;
            }

            var sb = new StringBuilder();
            await foreach (var seg in _Processor!.ProcessAsync(samples))
                sb.Append(seg.Text);

            return sb.ToString().Trim();
        }
    }
}
