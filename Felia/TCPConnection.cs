using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using static Felia.TCPConnection;

namespace Felia
{
    public class TCPConnection
    {
        public EventHandler<List<byte>> AudioRecived;

        public enum Command
        {
            RecordStart = 100,
            RecordData = 110,
            RecordStop = 120,
            Error = 400
        }
        public readonly ushort Port;

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

            List<byte> data = new List<byte>();

            try
            {
                while (client.Connected)
                {
                    Command command = ReadCommand(stream);
                    int length = ReadLength(stream);
                    byte[] payload = Array.Empty<byte>();
                    if (length > 0)
                        payload = ReadByte(stream, length);

                    switch (command)
                    {
                        case Command.RecordStart:
                            data.Clear();
                            break;
                        case Command.RecordData:
                            data.AddRange(payload);
                            break;
                        case Command.RecordStop:
                            Program.WriteLine($"Audo recieved:\n{String.Join("\n", data)}");
                            AudioRecived?.Invoke(this, data);
                            data.Clear();
                            break;
                        default:
                            Program.WriteLine($"Send command not known: {command}");
                            break;
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

        private Command ReadCommand(NetworkStream stream)
        {
            int b = stream.ReadByte();
            if (b == -1) b = 400;
            return (Command)b;
        }
        private int ReadLength(NetworkStream stream)
        {
            byte[] buf = ReadByte(stream, 4);
            int length = 0;
            length |= buf[0];
            length |= (buf[1] << 8);
            length |= (buf[2] << 16);
            length |= (buf[3] << 24);
            return length;
        }
        private byte[] ReadByte(NetworkStream stream, int count)
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

        #region Obsolete
        [Obsolete]
        const int SampleRate = 16000;
        [Obsolete]
        const short BitsPerSample = 16;
        [Obsolete]
        const short Channels = 1;

        [Obsolete]
        const byte PKT_AUDIO = 0;
        [Obsolete]
        const byte PKT_START = 1;
        [Obsolete]
        const byte PKT_END = 2;
        [Obsolete]
        void Mainn()
        {
            TcpListener listener = new TcpListener(IPAddress.Any, Port);
            listener.Start();
            Console.WriteLine($"Listening on port {Port}...");

            while (true)
            {
                Console.WriteLine("Waiting for ESP32 to connect...");
                using TcpClient client = listener.AcceptTcpClient();
                Console.WriteLine("ESP32 connected!");

                HandleClientt(client);

                Console.WriteLine("ESP32 disconnected, waiting for reconnect...");
            }
        }

        [Obsolete]
        void HandleClientt(TcpClient client)
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

        [Obsolete]
        byte ReadByte(NetworkStream stream)
        {
            int b = stream.ReadByte();
            if (b == -1) throw new IOException("Connection closed");
            return (byte)b;
        }

        [Obsolete]
        uint ReadUInt32LE(NetworkStream stream)
        {
            byte[] buf = ReadExact(stream, 4);
            return (uint)(buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24));
        }

        [Obsolete]
        byte[] ReadExact(NetworkStream stream, int count)
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

        [Obsolete]
        void WriteWavHeaderPlaceholder(FileStream fs)
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

        [Obsolete]
        void FinalizeWavHeader(FileStream fs, long dataBytes)
        {
            long chunkSize = 36 + dataBytes;

            fs.Seek(4, SeekOrigin.Begin);
            fs.Write(BitConverter.GetBytes((int)chunkSize), 0, 4);

            fs.Seek(40, SeekOrigin.Begin);
            fs.Write(BitConverter.GetBytes((int)dataBytes), 0, 4);

            fs.Flush();
        }
        #endregion
    }
}
