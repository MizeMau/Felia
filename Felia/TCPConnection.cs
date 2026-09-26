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

        private void ClientListener()
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

            HandleClient(client);
            ConnectedClients--;

            if (!WaitForClient && ConnectedClients < Config.MaxConnectionCount)
                new Task(ClientListener).Start();
        }

        private void HandleClient(TcpClient client)
        {
            using NetworkStream stream = client.GetStream();

            try
            {
                while (client.Connected)
                {
                    if (!string.IsNullOrEmpty(DataToSend))
                    {
                        byte[] data = Encoding.ASCII.GetBytes(DataToSend);
                        client.Client.Send(data);
                        DataToSend = null;
                    }
                }
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
    }
}
