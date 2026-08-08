using F5TTS_Console;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Felia
{
    public class Program
    {
        private static TCPConnection _TCPConnection;
        private static Whisper _Whisper;
        public static void WriteLine(string s)
        {
#if DEBUG
            Console.WriteLine(s);
#endif
        }
        static async Task Main()
        {
            _Whisper = new Whisper();
            await _Whisper.Init();

            _TCPConnection = new TCPConnection(5000);
            _TCPConnection.AudioRecived += AudioRecived;
            _TCPConnection.Start();

            await Task.Delay(-1);
        }

        public static async void AudioRecived(object? sender, List<byte> audio)
        {
            string text = await _Whisper.Transcribe(audio);
            WriteLine(text);
        }
    }
}