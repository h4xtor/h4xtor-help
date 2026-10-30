// Voice.cs — samtale via WinMM waveIn/waveOut og G.711 u-law.
// 8 kHz mono 16-bit, 40 ms stk (320 samples -> 320 u-law bytes = 8 KB/s).
// Ingen ekstern lib: kun winmm.dll.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Fjern
{
    internal static class VoiceCodec
    {
        // u-law (G.711) tabelfri omformer
        public static byte[] Encode(byte[] pcm, int offset, int count)
        {
            byte[] outBytes = new byte[count / 2];
            for (int i = 0, j = 0; j < outBytes.Length; j++)
            {
                short sample = (short)(pcm[offset + i] | (pcm[offset + i + 1] << 8));
                outBytes[j] = LinearToUlaw(sample);
                i += 2;
            }
            return outBytes;
        }

        public static byte[] Decode(byte[] ulaw, int offset, int count)
        {
            byte[] pcm = new byte[count * 2];
            for (int i = 0, j = 0; j < count; j++)
            {
                short sample = UlawToLinear(ulaw[offset + j]);
                pcm[i++] = (byte)(sample & 0xFF);
                pcm[i++] = (byte)((sample >> 8) & 0xFF);
            }
            return pcm;
        }

        private static byte LinearToUlaw(short sample)
        {
            int sign = (sample >> 8) & 0x80;
            if (sign != 0) sample = (short)(-sample);
            if (sample > 32635) sample = 32635;
            int v = sample + 132;
            int exponent = 7;
            for (int i = 0; i < 7; i++)
            {
                if ((v & 0x4000) != 0) break;
                v <<= 1;
                exponent--;
            }
            int mantissa = (v >> (exponent + 3)) & 0x0F;
            return (byte)~(sign | (exponent << 4) | mantissa);
        }

        private static short UlawToLinear(byte ulaw)
        {
            ulaw = (byte)(~ulaw);
            int sign = ulaw & 0x80;
            int exponent = (ulaw >> 4) & 0x07;
            int mantissa = ulaw & 0x0F;
            int sample = ((mantissa << 3) + 132) << exponent;
            sample -= 132;
            return (short)(sign != 0 ? -sample : sample);
        }
    }

    // fælles P/Invoke-shape
    [StructLayout(LayoutKind.Sequential)]
    internal struct WaveFormatEx
    {
        public short FormatTag; public short Channels; public uint SamplesPerSec;
        public uint AvgBytesPerSec; public short BlockAlign; public short BitsPerSample;
        public short Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WaveHdr
    {
        public IntPtr Data; public uint Length; public uint Recorded; public uint User;
        public uint Flags; public uint Loops; public IntPtr Next; public IntPtr Reserved;
    }

    internal static class WinMM
    {
        public const int WAVE_MAPPER = -1;
        public const int MMSYSERR_NOERROR = 0;
        public const int CALLBACK_FUNCTION = 0x30000;
        public const int WIM_DATA = 0x3C0;

        [DllImport("winmm.dll")] public static extern int waveOutOpen(out IntPtr h, int device,
            ref WaveFormatEx fmt, int callback, int instance, int flags);
        [DllImport("winmm.dll")] public static extern int waveOutPrepareHeader(IntPtr h, ref WaveHdr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveOutWrite(IntPtr h, ref WaveHdr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveOutUnprepareHeader(IntPtr h, ref WaveHdr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] public static extern int waveOutClose(IntPtr h);

        [DllImport("winmm.dll")] public static extern int waveInOpen(out IntPtr h, int device,
            ref WaveFormatEx fmt, WindowCallback cb, int instance, int flags);
        [DllImport("winmm.dll")] public static extern int waveInPrepareHeader(IntPtr h, ref WaveHdr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveInAddBuffer(IntPtr h, ref WaveHdr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveInUnprepareHeader(IntPtr h, ref WaveHdr hdr, int size);
        [DllImport("winmm.dll")] public static extern int waveInReset(IntPtr h);
        [DllImport("winmm.dll")] public static extern int waveInClose(IntPtr h);
    }

    internal delegate void WindowCallback(IntPtr dev, uint msg, int instance, int param1, int param2);

    // Afspiller u-law-pakker (320 bytes / 40 ms).
    internal sealed class VoicePlayer : IDisposable
    {
        private const int SampleRate = 8000;
        private const int SamplesPerChunk = 320;
        private const int Buffers = 16;

        private IntPtr _dev;
        private WaveHdr[] _headers;
        private byte[][] _buffers;
        private readonly List<GCHandle> _pins = new List<GCHandle>();
        private readonly Queue<byte[]> _queue = new Queue<byte[]>();
        private readonly object _lock = new object();
        private Thread _pump;
        private volatile bool _running;

        public bool Failed { get; private set; }

        public bool Start()
        {
            WaveFormatEx fmt = new WaveFormatEx();
            fmt.FormatTag = 1; fmt.Channels = 1;
            fmt.SamplesPerSec = SampleRate; fmt.BitsPerSample = 16;
            fmt.BlockAlign = 2; fmt.AvgBytesPerSec = (uint)(SampleRate * 2);

            IntPtr dev;
            if (WinMM.waveOutOpen(out dev, WinMM.WAVE_MAPPER, ref fmt, 0, 0, 0) != WinMM.MMSYSERR_NOERROR)
                return false;
            _dev = dev;

            int bytes = SamplesPerChunk * 2;
            _headers = new WaveHdr[Buffers];
            _buffers = new byte[Buffers][];
            for (int i = 0; i < Buffers; i++)
            {
                _headers[i] = new WaveHdr();
                _headers[i].Length = (uint)bytes;
                _buffers[i] = new byte[bytes];
                _pins.Add(GCHandle.Alloc(_buffers[i], GCHandleType.Pinned));
                _headers[i].Data = _pins[i].AddrOfPinnedObject();
                WinMM.waveOutPrepareHeader(_dev, ref _headers[i], Marshal.SizeOf(typeof(WaveHdr)));
            }

            _running = true;
            _pump = new Thread(PumpLoop);
            _pump.IsBackground = true;
            _pump.Name = "fjern-voice-player";
            _pump.Start();
            return true;
        }

        public void PlayUlaw(byte[] ulaw)
        {
            if (!_running || ulaw == null || ulaw.Length == 0) return;
            byte[] pcm = VoiceCodec.Decode(ulaw, 0, ulaw.Length);
            lock (_lock)
            {
                if (_queue.Count > 16) _queue.Dequeue();      // kastrér baglog
                _queue.Enqueue(pcm);
            }
        }

        private void PumpLoop()
        {
            int cursor = 0;
            while (_running)
            {
                byte[] data;
                lock (_lock) { data = _queue.Count > 0 ? _queue.Dequeue() : null; }
                if (data == null) { Thread.Sleep(15); continue; }

                if ((_headers[cursor].Flags & 0x1) == 0)      // WHDR_DONE-bjælle
                {
                    Thread.Sleep(10);
                    if ((_headers[cursor].Flags & 0x1) == 0) continue;
                }

                int n = Math.Min(data.Length, _buffers[cursor].Length);
                Buffer.BlockCopy(data, 0, _buffers[cursor], 0, n);
                WaveHdr hdr = _headers[cursor];
                hdr.Length = (uint)n;
                hdr.Flags = 0;
                WinMM.waveOutWrite(_dev, ref hdr, Marshal.SizeOf(typeof(WaveHdr)));
                _headers[cursor] = hdr;
                cursor = (cursor + 1) % Buffers;
            }
        }

        public void Dispose()
        {
            _running = false;
            if (_dev != IntPtr.Zero)
            {
                try
                {
                    WinMM.waveOutReset(_dev);
                    for (int i = 0; i < _pins.Count; i++) _pins[i].Free();
                    _pins.Clear();
                    WinMM.waveOutClose(_dev);
                }
                catch (Exception) { }
                _dev = IntPtr.Zero;
            }
        }
    }

    // Mikrofon: 8 x 40 ms-buffers; tømmer med G.711 u-law og sender OnChunk.
    internal sealed class VoiceRecorder
    {
        private const int SampleRate = 8000;
        private const int SamplesPerChunk = 320;
        private const int Buffers = 8;

        private IntPtr _dev;
        private WaveHdr[] _headers;
        private byte[][] _buffers;
        private readonly List<GCHandle> _pins = new List<GCHandle>();
        private volatile bool _running;

        // callback fra winmm lander hos den nyeste instans — en ad gangen
        private static VoiceRecorder _current;

        public event Action<byte[]> ChunkReady;

        public bool Failed { get; private set; }

        public bool Start()
        {
            WaveFormatEx fmt = new WaveFormatEx();
            fmt.FormatTag = 1; fmt.Channels = 1;
            fmt.SamplesPerSec = SampleRate; fmt.BitsPerSample = 16;
            fmt.BlockAlign = 2; fmt.AvgBytesPerSec = (uint)(SampleRate * 2);

            WindowCallback cb = Callback;   // håndtag til winmm callback
            _current = this;

            try
            {
                if (WinMM.waveInOpen(out _dev, WinMM.WAVE_MAPPER, ref fmt, cb, 0,
                    WinMM.CALLBACK_FUNCTION) != WinMM.MMSYSERR_NOERROR)
                {
                    Failed = true;
                    return false;
                }

                int bytes = SamplesPerChunk * 2;
                _headers = new WaveHdr[Buffers];
                _buffers = new byte[Buffers][];
                for (int i = 0; i < Buffers; i++)
                {
                    _headers[i] = new WaveHdr();
                    _headers[i].Length = (uint)bytes;
                    _buffers[i] = new byte[bytes];
                    _pins.Add(GCHandle.Alloc(_buffers[i], GCHandleType.Pinned));
                    _headers[i].Data = _pins[i].AddrOfPinnedObject();
                    WinMM.waveInPrepareHeader(_dev, ref _headers[i], Marshal.SizeOf(typeof(WaveHdr)));
                    WinMM.waveInAddBuffer(_dev, ref _headers[i], Marshal.SizeOf(typeof(WaveHdr)));
                }
                return true;
            }
            catch (Exception)
            {
                Dispose();
                return false;
            }
        }

        private void Callback(IntPtr dev, uint msg, int inst, int p1, int p2)
        {
            if (msg != WinMM.WIM_DATA) return;
            VoiceRecorder rec = _current;
            if (rec == null) return;
            rec.OnData();
        }

        private void OnData()
        {
            if (!_running) return;

            for (int i = 0; i < Buffers; i++)
            {
                if (_headers[i].Recorded <= 0) continue;

                int recorded = (int)Math.Min(_headers[i].Recorded, (uint)_buffers[i].Length);
                if (recorded > 0)
                {
                    byte[] pcm = new byte[recorded];
                    Buffer.BlockCopy(_buffers[i], 0, pcm, 0, recorded);
                    byte[] ulaw = VoiceCodec.Encode(pcm, 0, recorded);
                    Action<byte[]> handler = ChunkReady;
                    if (handler != null) handler(ulaw);
                }
                _headers[i].Recorded = 0;
                _headers[i].Flags = 0;
                WinMM.waveInAddBuffer(_dev, ref _headers[i], Marshal.SizeOf(typeof(WaveHdr)));
                return;   // én buffer i en pulje
            }
        }

        public void Dispose()
        {
            _running = false;
            if (_dev != IntPtr.Zero)
            {
                IntPtr dev = _dev;
                _dev = IntPtr.Zero;
                try { WinMM.waveInReset(dev); } catch (Exception) { }
                for (int i = 0; i < _pins.Count; i++) _pins[i].Free();
                _pins.Clear();
                try { WinMM.waveInClose(dev); } catch (Exception) { }
            }
            if (_current == this) _current = null;
        }
    }
}
