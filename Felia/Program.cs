namespace Felia
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            await InitWhisper();
        }

        public static async Task InitWhisper()
        {
            var whisper = new Whisper();
            whisper.SpeakEvent += SpeakInvoked;
            await whisper.Init();
        }

        private static void SpeakInvoked(object? sender, string speak)
        {
            Console.WriteLine($"You said: {speak}");
        }
    }
}