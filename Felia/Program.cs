namespace Felia
{
    public class Program
    {
        private static TCPConnection _TCPConnection;
        public static void WriteLine(string s)
        {
#if DEBUG
            Console.WriteLine(s);
#endif
        }
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== PC => ESP32 => STM32 - test ===");

            _TCPConnection = new TCPConnection(5000);
            _TCPConnection.Start();

            while (true)
            {
                string test = Console.ReadLine();
                if (string.IsNullOrEmpty(test))
                    continue;
                _TCPConnection.DataToSend = test;
            }
        }
    }
}