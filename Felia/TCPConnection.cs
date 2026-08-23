//using NAudio.CoreAudioApi;
//using System;
//using System.Collections.Generic;
//using System.Net;
//using System.Net.Sockets;
//using System.Text;
//using static Felia.TCPConnection;

//namespace Felia
//{
//    public class TCPConnection
//    {
//        public EventHandler<List<byte>> AudioRecived;

//        public enum Command
//        {
//            RecordStart = 100,
//            RecordData = 110,
//            RecordStop = 120,
//            Error = 400
//        }
//        public readonly ushort Port;

//        private int ConnectedClients = 0;
//        private bool WaitForClient = false;
//        private readonly TcpListener _Listener;

//        public TCPConnection(ushort port)
//        {
//            Port = port;
//            _Listener = new TcpListener(IPAddress.Any, Port);
//        }

//        public void Start()
//        {
//            _Listener.Start();
//            Program.WriteLine($"Listening on port {Port}...");
//            new Task(ClientListener).Start();
//        }

//        private void Stop()
//        {
//            _Listener.Stop();
//        }

//        private void ClientListener()
//        {
//            ConnectedClients++;
//            WaitForClient = true;

//            Program.WriteLine("Waiting for ESP32 to connect...");
//            using TcpClient client = _Listener.AcceptTcpClient();
//            Socket socket = client.Client;
//            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
//            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
//            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 2);
//            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
//            Program.WriteLine($"ESP32 {client.Client.RemoteEndPoint} connected!");

//            WaitForClient = false;
//            if (ConnectedClients < Config.MaxConnectionCount)
//                new Task(ClientListener).Start();

//            HandleClient(client);
//            ConnectedClients--;

//            if (!WaitForClient && ConnectedClients < Config.MaxConnectionCount)
//                new Task(ClientListener).Start();
//        }

//        private void HandleClient(TcpClient client)
//        {
//            using NetworkStream stream = client.GetStream();

//            List<byte> data = new List<byte>();

//            try
//            {
//                while (client.Connected)
//                {
//                    Command command = ReadCommand(stream);
//                    int length = ReadLength(stream);
//                    byte[] payload = Array.Empty<byte>();
//                    if (length > 0)
//                        payload = ReadByte(stream, length);

//                    switch (command)
//                    {
//                        case Command.RecordStart:
//                            data.Clear();
//                            break;
//                        case Command.RecordData:
//                            data.AddRange(payload);
//                            break;
//                        case Command.RecordStop:
//                            Program.WriteLine($"Audo recieved:\n{String.Join("\n", data)}");
//                            AudioRecived?.Invoke(this, data);
//                            data.Clear();
//                            break;
//                        default:
//                            Program.WriteLine($"Send command not known: {command}");
//                            break;
//                    }
//                }
//            }
//            catch
//            {
//                Program.WriteLine($"Exeption occured");
//            }
//            finally
//            {
//                client.Dispose();
//            }
//        }

//        private Command ReadCommand(NetworkStream stream)
//        {
//            int b = stream.ReadByte();
//            if (b == -1) b = 400;
//            return (Command)b;
//        }
//        private int ReadLength(NetworkStream stream)
//        {
//            byte[] buf = ReadByte(stream, 4);
//            int length = 0;
//            length |= buf[0];
//            length |= (buf[1] << 8);
//            length |= (buf[2] << 16);
//            length |= (buf[3] << 24);
//            return length;
//        }
//        private byte[] ReadByte(NetworkStream stream, int count)
//        {
//            byte[] buf = new byte[count];
//            int offset = 0;
//            while (offset < count)
//            {
//                int read = stream.Read(buf, offset, count - offset);
//                if (read == 0) throw new IOException("Connection closed mid-read");
//                offset += read;
//            }
//            return buf;
//        }

        
//    }
//}
