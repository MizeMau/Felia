using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Felia
{
    public class TCPConnection
    {
        public string? DataToSend;

        private readonly ushort Port;
        private int ConnectedClients = 0;
        private bool WaitForClient = false;
        private readonly TcpListener _Listener;

        public TCPConnection(ushort port)
        {
            Port = port;
            _Listener = new TcpListener(IPAddress.Any, Port);
        }

        public void Start()
        {
            _Listener.Start();
            Program.WriteLine($"Listening on port {Port}...");
            new Task(ClientListener).Start();
        }

        private void Stop()
        {
            _Listener.Stop();
        }

        private async void ClientListener()
        {
            ConnectedClients++;
            WaitForClient = true;

            Program.WriteLine("Waiting for ESP32 to connect...");
            using TcpClient client = _Listener.AcceptTcpClient();
            Socket socket = client.Client;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 2);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
            Program.WriteLine($"ESP32 {client.Client.RemoteEndPoint} connected!");

            WaitForClient = false;
            if (ConnectedClients < Config.MaxConnectionCount)
                new Task(ClientListener).Start();

            await HandleClient(client);
            ConnectedClients--;

            if (!WaitForClient && ConnectedClients < Config.MaxConnectionCount)
                new Task(ClientListener).Start();
        }

        private async Task HandleClient(TcpClient client)
        {
            using NetworkStream stream = client.GetStream();

            try
            {
                using var cts = new CancellationTokenSource();

                Task receiveTask = ReceiveLoop(stream, cts.Token);
                Task sendTask = SendLoop(stream, cts.Token);

                await Task.WhenAny(receiveTask, sendTask);

                cts.Cancel();
            }
            catch
            {
                Program.WriteLine($"Exeption occured");
            }
            finally
            {
                client.Dispose();
            }
        }
        async Task ReceiveLoop(NetworkStream stream, CancellationToken token)
        {
            byte[] buffer = new byte[4096];

            while (!token.IsCancellationRequested)
            {
                int count = await stream.ReadAsync(buffer, token);

                if (count == 0)
                {
                    break;
                }

                string test = Encoding.ASCII.GetString(buffer, 0, count);
                Console.Write(test);
                //for (int i = 0; i < count; i++)
                //{
                //    string test = Encoding.ASCII.GetString(buffer);
                //    Console.WriteLine(test);
                //}
            }
        }
        async Task SendLoop(NetworkStream stream, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (!string.IsNullOrEmpty(DataToSend))
                {
                    byte[] data = Encoding.ASCII.GetBytes(DataToSend);

                    await stream.WriteAsync(data, token);

                    DataToSend = null;
                }

                await Task.Delay(1, token);
            }
        }
    }
}
