using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace FjernRelay
{
    // Blindt relay.
    //
    // Parer en "H" (vaert) og en "V" (seer) i samme rum og kopierer derefter
    // bytes begge veje. Indholdet er ende-til-ende-krypteret i Fjern.exe, saa
    // relayet ser kun chiffer og kan ikke laese skaermen eller tasterne.
    //
    //   FjernRelay.exe [port]        standard: 7331
    internal static class Program
    {
        private const int DefaultPort = 7331;
        private const int UnpairedTimeoutMs = 120000;
        private const int MaxRooms = 512;

        private const byte ReplyWaiting = (byte)'W';
        private const byte ReplyPaired = (byte)'P';
        private const byte ReplyError = (byte)'E';

        private sealed class Room
        {
            public TcpClient Host;
            public TcpClient Viewer;
            public readonly AutoResetEvent Paired = new AutoResetEvent(false);
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            public readonly DateTime Created = DateTime.UtcNow;
        }

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, Room> Rooms = new Dictionary<string, Room>();

        private static int Main(string[] args)
        {
            int port = DefaultPort;
            if (args.Length > 0)
            {
                int parsed;
                if (int.TryParse(args[0], out parsed) && parsed > 0 && parsed < 65536) port = parsed;
            }

            Thread janitor = new Thread(Janitor);
            janitor.IsBackground = true;
            janitor.Start();

            TcpListener listener = new TcpListener(IPAddress.Any, port);
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("kunne ikke lytte paa port " + port + ": " + ex.Message);
                return 1;
            }

            Console.WriteLine("FjernRelay " + port);
            Console.WriteLine("venter paa forbindelser - ctrl+c stopper");

            while (true)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    break;
                }

                client.NoDelay = true;
                Thread worker = new Thread(delegate() { Handle(client); });
                worker.IsBackground = true;
                worker.Start();
            }

            return 0;
        }

        private static void Handle(TcpClient client)
        {
            string roomCode = null;
            Room entry = null;
            bool isHost = false;

            try
            {
                NetworkStream stream = client.GetStream();

                byte[] header = new byte[14];
                int got = 0;
                while (got < header.Length)
                {
                    int read = stream.Read(header, got, header.Length - got);
                    if (read <= 0) return;
                    got += read;
                }

                if (header[0] != (byte)'F' || header[1] != (byte)'J' ||
                    header[2] != (byte)'R' || header[3] != (byte)'N')
                {
                    stream.WriteByte(ReplyError);
                    return;
                }

                byte role = header[5];
                if (role != 'H' && role != 'V')
                {
                    stream.WriteByte(ReplyError);
                    return;
                }
                isHost = role == 'H';

                roomCode = Encoding.ASCII.GetString(header, 6, 8).TrimEnd(' ');
                if (roomCode.Length == 0)
                {
                    stream.WriteByte(ReplyError);
                    return;
                }

                TcpClient peer;

                lock (Sync)
                {
                    if (!Rooms.TryGetValue(roomCode, out entry))
                    {
                        if (Rooms.Count >= MaxRooms)
                        {
                            stream.WriteByte(ReplyError);
                            return;
                        }
                        entry = new Room();
                        Rooms[roomCode] = entry;
                    }

                    if (isHost)
                    {
                        if (entry.Host != null)
                        {
                            stream.WriteByte(ReplyError);
                            return;
                        }
                        entry.Host = client;
                    }
                    else
                    {
                        if (entry.Viewer != null)
                        {
                            stream.WriteByte(ReplyError);
                            return;
                        }
                        entry.Viewer = client;
                    }

                    peer = isHost ? entry.Viewer : entry.Host;
                }

                if (peer == null)
                {
                    stream.WriteByte(ReplyWaiting);
                    stream.Flush();

                    if (!entry.Paired.WaitOne(UnpairedTimeoutMs))
                    {
                        try { stream.WriteByte(ReplyError); } catch (Exception) { }
                        return;
                    }

                    // Den anden side ejer begge kopieringsretninger og lukker
                    // forbindelsen naar sessionen er slut. Her venter vi bare.
                    entry.Done.WaitOne(24 * 60 * 60 * 1000);
                    return;
                }

                // Vi er nummer to og har begge streams.
                stream.WriteByte(ReplyPaired);
                stream.Flush();

                NetworkStream peerStream = peer.GetStream();
                peerStream.WriteByte(ReplyPaired);
                peerStream.Flush();

                entry.Paired.Set();

                Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  parret rum " + roomCode);

                Thread pipe = new Thread(delegate() { Pump(stream, peerStream); });
                pipe.IsBackground = true;
                pipe.Start();

                Pump(peerStream, stream);
            }
            catch (Exception)
            {
            }
            finally
            {
                lock (Sync)
                {
                    if (entry != null)
                    {
                        if (entry.Host == client) entry.Host = null;
                        if (entry.Viewer == client) entry.Viewer = null;
                        if (entry.Host == null && entry.Viewer == null && roomCode != null)
                        {
                            Room current;
                            if (Rooms.TryGetValue(roomCode, out current) && current == entry) Rooms.Remove(roomCode);
                        }
                    }
                }

                if (entry != null) entry.Done.Set();
                try { client.Close(); } catch (Exception) { }
            }
        }

        private static void Pump(Stream from, Stream to)
        {
            byte[] buffer = new byte[64 * 1024];
            try
            {
                while (true)
                {
                    int read = from.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    to.Write(buffer, 0, read);
                    to.Flush();
                }
            }
            catch (Exception)
            {
            }

            try { to.Close(); } catch (Exception) { }
            try { from.Close(); } catch (Exception) { }
        }

        private static void Janitor()
        {
            while (true)
            {
                Thread.Sleep(5000);
                List<TcpClient> stale = new List<TcpClient>();
                lock (Sync)
                {
                    List<string> dead = new List<string>();
                    foreach (KeyValuePair<string, Room> pair in Rooms)
                    {
                        if ((DateTime.UtcNow - pair.Value.Created).TotalMilliseconds > UnpairedTimeoutMs * 2)
                        {
                            if (pair.Value.Host != null) stale.Add(pair.Value.Host);
                            if (pair.Value.Viewer != null) stale.Add(pair.Value.Viewer);
                            dead.Add(pair.Key);
                        }
                    }
                    for (int i = 0; i < dead.Count; i++) Rooms.Remove(dead[i]);
                }
                for (int i = 0; i < stale.Count; i++)
                {
                    try { stale[i].Close(); } catch (Exception) { }
                }
            }
        }
    }
}
