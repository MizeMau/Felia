namespace Felia
{
    internal class Program
    {
        private static LlmService llm;
        private static Whisper whisper;
        public static string Appdata = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Felia");
        static async Task Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            llm = await LlmService.CreateAsync();

            Console.WriteLine("LLM ready.\n");

            whisper = new Whisper();
            whisper.SpeakEvent += SpeakInvoked;
            await whisper.Init();

            await Task.Delay(-1);
        }

        private static async void SpeakInvoked(object? sender, string speak)
        {
            whisper.Pause();
            var reply = await llm.ChatAsync(speak);
            Console.WriteLine($"Felia answered: {reply}");
            whisper.Resume();
        }
    }
}