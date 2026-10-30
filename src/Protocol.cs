using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Fjern
{
    // ---------------------------------------------------------------- protokol

    internal static class Wire
    {
        // beskedtyper inde i den krypterede kanal
        public const byte MsgVideo = 2;
        public const byte MsgMouse = 3;
        public const byte MsgKey = 4;
        public const byte MsgBye = 6;
        public const byte MsgPing = 7;
        public const byte MsgControl = 8;
        public const byte MsgText = 10;
        public const byte MsgClip = 11;
        public const byte MsgFileStart = 12;
        public const byte MsgFileData = 13;
        public const byte MsgFileEnd = 14;
        public const byte MsgRate = 15;
        public const byte MsgSnapshot = 16;
        public const byte MsgCallInvite = 20;   // admin -> klient: vil tale
        public const byte MsgCallAccept = 21;   // klient -> admin: jes
        public const byte MsgCallReject = 22;   // klient -> admin: nej
        public const byte MsgCallEnd = 23;      // hvert end
        public const byte MsgVoice = 24;        // u-law 8 kHz 40 ms stk
        public const byte MsgVideoFrame = 25;   // jpeg webcam 320x240
        public const byte MsgShotReq = 26;      // admin baer om screenshot
        public const byte MsgShotJpeg = 27;     // klient cv  jpeg spg

        // roller paa relayet
        public const byte RoleHost = (byte)'H';
        public const byte RoleViewer = (byte)'V';

        // relayets svar
        public const byte RelayWaiting = (byte)'W';
        public const byte RelayPaired = (byte)'P';
        public const byte RelayError = (byte)'E';

        public static readonly byte[] Magic = new byte[] { (byte)'F', (byte)'J', (byte)'R', (byte)'N' };
        public const byte Version = 1;

        public const int MaxFrame = 24 * 1024 * 1024;
        public const int MaxBlob = 8192;

        // musebeskeder
        public const byte MouseMove = 0;
        public const byte MouseButton = 1;
        public const byte MouseWheel = 2;
        public const byte ButtonLeft = 1;
        public const byte ButtonRight = 2;
        public const byte ButtonMiddle = 3;
    }

    // ------------------------------------------------------------------- io

    internal static class Io
    {
        public static void WriteBytes(Stream s, byte[] data)
        {
            s.Write(data, 0, data.Length);
            s.Flush();
        }

        public static void WriteBlob(Stream s, byte[] data)
        {
            byte[] head = new byte[4];
            head[0] = (byte)(data.Length >> 24);
            head[1] = (byte)(data.Length >> 16);
            head[2] = (byte)(data.Length >> 8);
            head[3] = (byte)data.Length;
            s.Write(head, 0, 4);
            s.Write(data, 0, data.Length);
            s.Flush();
        }

        public static byte[] ReadBlob(Stream s, int max)
        {
            byte[] head = ReadExact(s, 4);
            int n = (head[0] << 24) | (head[1] << 16) | (head[2] << 8) | head[3];
            if (n < 0 || n > max) throw new InvalidDataException("for stor blob: " + n);
            return ReadExact(s, n);
        }

        public static byte[] ReadExact(Stream s, int n)
        {
            byte[] buf = new byte[n];
            int off = 0;
            while (off < n)
            {
                int r = s.Read(buf, off, n - off);
                if (r <= 0) throw new EndOfStreamException("forbindelsen blev lukket");
                off += r;
            }
            return buf;
        }

        public static void Skip(Stream s, int n)
        {
            byte[] buf = new byte[Math.Min(n, 4096)];
            int left = n;
            while (left > 0)
            {
                int r = s.Read(buf, 0, Math.Min(buf.Length, left));
                if (r <= 0) throw new EndOfStreamException("forbindelsen blev lukket");
                left -= r;
            }
        }
    }

    // ------------------------------------------------------------- kryptografi

    internal static class Crypt
    {
        public static byte[] Sha256(byte[] data)
        {
            using (SHA256 h = SHA256.Create()) return h.ComputeHash(data);
        }

        public static byte[] Hmac(byte[] key, byte[] data)
        {
            using (HMACSHA256 h = new HMACSHA256(key)) return h.ComputeHash(data);
        }

        public static byte[] Cat(byte[] a, byte[] b)
        {
            byte[] r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }

        public static byte[] RandomBytes(int n)
        {
            byte[] b = new byte[n];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(b);
            }
            return b;
        }

        public static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        // RFC 5869, kun expand-delen bruges her (ikm er allerede hoejentropisk ECDH).
        public static byte[] Hkdf(byte[] ikm, byte[] salt, string info, int length)
        {
            byte[] prk;
            using (HMACSHA256 h = new HMACSHA256(salt)) prk = h.ComputeHash(ikm);

            byte[] infoBytes = Encoding.UTF8.GetBytes(info);
            byte[] okm = new byte[length];
            byte[] block = new byte[0];
            int pos = 0;
            byte counter = 1;

            while (pos < length)
            {
                byte[] input = new byte[block.Length + infoBytes.Length + 1];
                Buffer.BlockCopy(block, 0, input, 0, block.Length);
                Buffer.BlockCopy(infoBytes, 0, input, block.Length, infoBytes.Length);
                input[input.Length - 1] = counter;

                using (HMACSHA256 h = new HMACSHA256(prk)) block = h.ComputeHash(input);

                int take = Math.Min(block.Length, length - pos);
                Buffer.BlockCopy(block, 0, okm, pos, take);
                pos += take;
                counter++;
            }
            return okm;
        }

        public static string Hex(byte[] data)
        {
            StringBuilder sb = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++) sb.Append(data[i].ToString("x2"));
            return sb.ToString();
        }
    }

    // ------------------------------------------------------- krypteret kanal

    // Ramme paa traaden: 4 byte laengde | 1 byte type | 16 byte IV | chiffer | 32 byte HMAC
    // HMAC daekker type||IV||chiffer, saa type kan ikke aendres undervejs.
    // IV er en taeller, og noeglen er ny per session -> ingen IV-genbrug.
    internal sealed class SecureChannel : IDisposable
    {
        private readonly Stream _s;
        private readonly AesCryptoServiceProvider _aes;
        private readonly byte[] _macKey;
        private readonly object _sendLock = new object();
        private long _sendCounter;
        private long _recvCounter;
        private bool _closed;

        private SecureChannel(Stream s, byte[] encKey, byte[] macKey)
        {
            _s = s;
            _macKey = macKey;
            _aes = new AesCryptoServiceProvider();
            _aes.KeySize = 256;
            _aes.Mode = CipherMode.CBC;
            _aes.Padding = PaddingMode.PKCS7;
            _aes.Key = encKey;
            _aes.IV = new byte[16];
        }

        public bool IsClosed { get { return _closed; } }

        // ---------------------------------------------------------- handshake

        public static SecureChannel Establish(Stream s, byte role, string password, Action<string> log)
        {
            byte[] myPublic;
            byte[] secret;

            using (ECDiffieHellmanCng ecdh = new ECDiffieHellmanCng(256))
            {
                ecdh.KeyDerivationFunction = ECDiffieHellmanKeyDerivationFunction.Hash;
                ecdh.HashAlgorithm = CngAlgorithm.Sha256;

                myPublic = ecdh.PublicKey.ToByteArray();
                Io.WriteBlob(s, myPublic);

                byte[] peerPublic = Io.ReadBlob(s, Wire.MaxBlob);
                using (ECDiffieHellmanPublicKey peerKey =
                       ECDiffieHellmanCngPublicKey.FromByteArray(peerPublic, CngKeyBlobFormat.EccPublicBlob))
                {
                    secret = ecdh.DeriveKeyMaterial(peerKey);
                }

                // Sessionen skal bindes til parrets offentlige noegler i fast orden,
                // ellers kan en mellemmand genbruge taget i en anden session.
                byte[] transcript = role == Wire.RoleHost
                    ? Crypt.Cat(myPublic, peerPublic)
                    : Crypt.Cat(peerPublic, myPublic);

                byte[] psk = Crypt.Sha256(Encoding.UTF8.GetBytes(password));
                byte[] tag = Crypt.Hmac(psk, Crypt.Cat(Encoding.UTF8.GetBytes("fjern-v1-auth"), transcript));

                Io.WriteBlob(s, tag);
                byte[] peerTag = Io.ReadBlob(s, 64);

                if (!Crypt.FixedTimeEquals(tag, peerTag))
                    throw new CryptographicException("forkert adgangskode");

                byte[] okm = Crypt.Hkdf(secret, psk, "fjern-v1-key", 64);
                byte[] encKey = new byte[32];
                byte[] macKey = new byte[32];
                Buffer.BlockCopy(okm, 0, encKey, 0, 32);
                Buffer.BlockCopy(okm, 32, macKey, 0, 32);

                if (log != null) log("krypteret kanal klar (" + Crypt.Hex(encKey).Substring(0, 8) + "...)");
                return new SecureChannel(s, encKey, macKey);
            }
        }

        // -------------------------------------------------------------- send

        public void Send(byte type, byte[] payload)
        {
            if (payload == null) payload = new byte[0];

            lock (_sendLock)
            {
                if (_closed) throw new IOException("kanalen er lukket");

                byte[] iv = Iv(_sendCounter);
                byte[] cipher;

                using (ICryptoTransform enc = _aes.CreateEncryptor(_aes.Key, iv))
                {
                    cipher = enc.TransformFinalBlock(payload, 0, payload.Length);
                }

                byte[] macInput = new byte[1 + 16 + cipher.Length];
                macInput[0] = type;
                Buffer.BlockCopy(iv, 0, macInput, 1, 16);
                Buffer.BlockCopy(cipher, 0, macInput, 17, cipher.Length);
                byte[] mac = Crypt.Hmac(_macKey, macInput);

                byte[] head = new byte[5];
                int len = cipher.Length;
                head[0] = (byte)(len >> 24);
                head[1] = (byte)(len >> 16);
                head[2] = (byte)(len >> 8);
                head[3] = (byte)len;
                head[4] = type;

                _s.Write(head, 0, 5);
                _s.Write(iv, 0, 16);
                _s.Write(cipher, 0, cipher.Length);
                _s.Write(mac, 0, 32);
                _s.Flush();

                _sendCounter++;
            }
        }

        // ------------------------------------------------------------- modtag

        // Returnerer false naar forbindelsen er lukket.
        public bool Receive(out byte type, out byte[] payload)
        {
            type = 0;
            payload = null;

            byte[] head;
            try
            {
                head = Io.ReadExact(_s, 5);
            }
            catch (EndOfStreamException) { return false; }
            catch (IOException) { return false; }
            catch (ObjectDisposedException) { return false; }

            int len = (head[0] << 24) | (head[1] << 16) | (head[2] << 8) | head[3];
            if (len < 0 || len > Wire.MaxFrame)
            {
                throw new InvalidDataException("ramme for stor: " + len);
            }
            type = head[4];

            byte[] iv = Io.ReadExact(_s, 16);
            byte[] cipher = Io.ReadExact(_s, len);
            byte[] mac = Io.ReadExact(_s, 32);

            byte[] macInput = new byte[1 + 16 + len];
            macInput[0] = type;
            Buffer.BlockCopy(iv, 0, macInput, 1, 16);
            Buffer.BlockCopy(cipher, 0, macInput, 17, len);

            if (!Crypt.FixedTimeEquals(mac, Crypt.Hmac(_macKey, macInput)))
                throw new CryptographicException("dårlig MAC paa ramme " + _recvCounter);

            _recvCounter++;
            using (ICryptoTransform dec = _aes.CreateDecryptor(_aes.Key, iv))
            {
                payload = dec.TransformFinalBlock(cipher, 0, cipher.Length);
            }
            return true;
        }

        private static byte[] Iv(long counter)
        {
            byte[] iv = new byte[16];
            for (int i = 0; i < 8; i++) iv[15 - i] = (byte)(counter >> (8 * i));
            return iv;
        }

        public void Dispose()
        {
            _closed = true;
            try { _s.Close(); } catch (Exception) { }
            try { _aes.Clear(); } catch (Exception) { }
        }
    }

    // --------------------------------------------------------------- transport

    internal static class Transport
    {
        public static Stream ConnectRelay(string host, int port, byte role, string room,
                                          Action<string> progress, int timeoutMs)
        {
            TcpClient client = new TcpClient();
            client.NoDelay = true;

            IAsyncResult ar = client.BeginConnect(host, port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                client.Close();
                throw new IOException("kunne ikke naa relayet " + host + ":" + port);
            }
            client.EndConnect(ar);

            Stream s = client.GetStream();

            byte[] header = new byte[4 + 1 + 1 + 8];
            Buffer.BlockCopy(Wire.Magic, 0, header, 0, 4);
            header[4] = Wire.Version;
            header[5] = role;
            byte[] roomBytes = Encoding.ASCII.GetBytes(room.PadRight(8).Substring(0, 8));
            Buffer.BlockCopy(roomBytes, 0, header, 6, 8);
            s.Write(header, 0, header.Length);
            s.Flush();

            int status = s.ReadByte();
            if (status < 0) throw new IOException("relayet lukkede forbindelsen");
            if (status == Wire.RelayError) throw new IOException("relayet afviste rummet");
            if (status == Wire.RelayWaiting)
            {
                if (progress != null) progress("venter paa at vennen forbinder...");
                status = s.ReadByte();
                if (status < 0) throw new IOException("relayet lukkede forbindelsen");
                if (status == Wire.RelayError) throw new IOException("relayet afviste forbindelsen");
            }
            if (status != Wire.RelayPaired) throw new IOException("uventet svar fra relayet");

            return s;
        }

        public static TcpListener ListenDirect(int port)
        {
            TcpListener listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            return listener;
        }
    }
}
