using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Fjern
{
    // Skærmaftastning med flis-delta.
    //
    // Hele skærmen fanges i en Bitmap, skaleres ned til maks bredde, og deles i
    // fliser paa TileSize x TileSize. Hver flise hashes (samplet, hver 8. byte)
    // og kun ændrede fliser JPEG-kodes. Skifter mere end halvdelen af fliserne,
    // sendes hele billedet som én flise — det er billigere end 40 smaa JPEG-kald.
    //
    // Samplingen kan overse enkelte pixels; derfor tvinges et fuldt genopfrisk
    // hver KeyframeInterval billeder.
    internal sealed class Grabber : IDisposable
    {
        private readonly Rectangle _monitor;
        private readonly int _tile;
        private readonly int _keyframeInterval;

        private Bitmap _src;
        private Bitmap _dst;
        private Graphics _srcG;
        private Graphics _dstG;

        private byte[] _pixels;
        private long[] _hashes;
        private bool _hashesReady;

        private int _gridX;
        private int _gridY;
        private int _frameSeq;

        private readonly ImageCodecInfo _jpeg;
        private readonly object _sync = new object();

        public readonly int OutW;
        public readonly int OutH;

        public Grabber(int maxWidth, int tileSize, int keyframeInterval, int monitorIndex)
        {
            Screen target = monitorIndex >= 0 && monitorIndex < Screen.AllScreens.Length
                ? Screen.AllScreens[monitorIndex]
                : Screen.PrimaryScreen;
            _monitor = target.Bounds;
            _tile = Math.Max(64, tileSize);
            _keyframeInterval = Math.Max(1, keyframeInterval);

            if (_monitor.Width > maxWidth)
            {
                OutW = maxWidth;
                OutH = (int)Math.Round(_monitor.Height * (double)maxWidth / _monitor.Width);
            }
            else
            {
                OutW = _monitor.Width;
                OutH = _monitor.Height;
            }

            _dst = new Bitmap(OutW, OutH, PixelFormat.Format32bppArgb);
            _dstG = Graphics.FromImage(_dst);

            if (OutW != _monitor.Width || OutH != _monitor.Height)
            {
                _src = new Bitmap(_monitor.Width, _monitor.Height, PixelFormat.Format32bppArgb);
                _srcG = Graphics.FromImage(_src);
                _srcG.CopyFromScreen(_monitor.X, _monitor.Y, 0, 0, _monitor.Size,
                                     CopyPixelOperation.SourceCopy);
                _dstG.InterpolationMode = InterpolationMode.Bilinear;
                _dstG.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                _dstG.DrawImage(_src, new Rectangle(0, 0, OutW, OutH),
                                0, 0, _monitor.Width, _monitor.Height, GraphicsUnit.Pixel);
            }

            _gridX = (OutW + _tile - 1) / _tile;
            _gridY = (OutH + _tile - 1) / _tile;
            _hashes = new long[_gridX * _gridY];

            _jpeg = FindEncoder(ImageFormat.Jpeg);
            if (_jpeg == null) throw new InvalidOperationException("ingen JPEG-encoder fundet");
        }

        public Rectangle MonitorBounds { get { return _monitor; } }

        // Enkel FPS-m\u00e5ling til statusvinduet: antal billeder genereret pr. sekund.
        private long _fpsWindowStart = DateTime.UtcNow.Ticks;
        private int _fpsWindowFrames;
        private int _activeFps;

        public int ActiveFps { get { return _activeFps; } }

        public static ImageCodecInfo FindEncoder(ImageFormat format)
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
            for (int i = 0; i < codecs.Length; i++)
            {
                if (codecs[i].FormatID == format.Guid) return codecs[i];
            }
            return null;
        }

        public void Invalidate()
        {
            _hashesReady = false;
        }

        // Fanger skærmen og returnerer en Video-payload, eller null hvis intet ændrede sig.
        public byte[] Grab(int quality)
        {
            lock (_sync)
            {
                Capture();

                int stride;
                int width = OutW;
                int height = OutH;

                BitmapData data = _dst.LockBits(new Rectangle(0, 0, width, height),
                                                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    stride = data.Stride;
                    int need = stride * height;
                    if (_pixels == null || _pixels.Length < need) _pixels = new byte[need];
                    Marshal.Copy(data.Scan0, _pixels, 0, need);
                }
                finally
                {
                    _dst.UnlockBits(data);
                }

                _frameSeq++;

                _fpsWindowFrames++;
                long now = DateTime.UtcNow.Ticks;
                if (now - _fpsWindowStart >= TimeSpan.TicksPerSecond)
                {
                    double seconds = (now - _fpsWindowStart) / (double)TimeSpan.TicksPerSecond;
                    _activeFps = (int)Math.Round(_fpsWindowFrames / seconds);
                    _fpsWindowStart = now;
                    _fpsWindowFrames = 0;
                }

                List<int> changed = new List<int>(16);
                long[] fresh = new long[_hashes.Length];

                for (int ty = 0; ty < _gridY; ty++)
                {
                    for (int tx = 0; tx < _gridX; tx++)
                    {
                        int idx = ty * _gridX + tx;
                        long h = HashTile(tx, ty, stride, width, height);
                        fresh[idx] = h;
                        if (!_hashesReady || _hashes[idx] != h) changed.Add(idx);
                    }
                }

                bool keyframe = !_hashesReady || (_frameSeq % _keyframeInterval) == 0;

                if (changed.Count == 0 && !keyframe) return null;
                if (changed.Count == 0) return null;

                bool wholeFrame = keyframe || changed.Count * 2 >= _hashes.Length;

                List<byte[]> blobs = new List<byte[]>(changed.Count);
                List<int> xs = new List<int>(changed.Count);
                List<int> ys = new List<int>(changed.Count);
                List<int> ws = new List<int>(changed.Count);
                List<int> hs = new List<int>(changed.Count);

                if (wholeFrame)
                {
                    xs.Add(0); ys.Add(0); ws.Add(width); hs.Add(height);
                    blobs.Add(EncodeRect(new Rectangle(0, 0, width, height), quality));
                }
                else
                {
                    for (int i = 0; i < changed.Count; i++)
                    {
                        int idx = changed[i];
                        int tx = idx % _gridX;
                        int ty = idx / _gridX;
                        int x = tx * _tile;
                        int y = ty * _tile;
                        int w = Math.Min(_tile, width - x);
                        int h = Math.Min(_tile, height - y);
                        if (w <= 0 || h <= 0) continue;

                        xs.Add(x); ys.Add(y); ws.Add(w); hs.Add(h);
                        blobs.Add(EncodeRect(new Rectangle(x, y, w, h), quality));
                    }
                }

                if (blobs.Count == 0) return null;

                Buffer.BlockCopy(fresh, 0, _hashes, 0, fresh.Length * sizeof(long));
                _hashesReady = true;

                using (MemoryStream ms = new MemoryStream(64 * 1024))
                {
                    using (BinaryWriter w = new BinaryWriter(ms))
                    {
                        w.Write((ushort)width);
                        w.Write((ushort)height);
                        w.Write((ushort)_tile);
                        w.Write((uint)_frameSeq);
                        w.Write((ushort)blobs.Count);

                        for (int i = 0; i < blobs.Count; i++)
                        {
                            w.Write((ushort)xs[i]);
                            w.Write((ushort)ys[i]);
                            w.Write((ushort)ws[i]);
                            w.Write((ushort)hs[i]);
                            w.Write((uint)blobs[i].Length);
                            w.Write(blobs[i]);
                        }
                    }
                    return ms.ToArray();
                }
            }
        }

        private void Capture()
        {
            if (_src == null)
            {
                _dstG.CopyFromScreen(_monitor.X, _monitor.Y, 0, 0, _monitor.Size,
                                     CopyPixelOperation.SourceCopy);
                return;
            }

            _srcG.CopyFromScreen(_monitor.X, _monitor.Y, 0, 0, _monitor.Size,
                                 CopyPixelOperation.SourceCopy);
            _dstG.DrawImage(_src, new Rectangle(0, 0, OutW, OutH),
                            0, 0, _monitor.Width, _monitor.Height, GraphicsUnit.Pixel);
        }

        // Alternativ til Grab(): bruger en leveret bitmap i stedet for sk\u00e6rmen.
        // Bruges af selftest, s\u00e5 delatesten bliver deterministisk (en syntetisk
        // sk\u00e6rm der ikke animerer i baggrunden).
        public byte[] GrabFrom(Bitmap source, int quality)
        {
            lock (_sync)
            {
                _dstG.InterpolationMode = InterpolationMode.Bilinear;
                _dstG.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                _dstG.DrawImage(source, new Rectangle(0, 0, OutW, OutH));

                int stride;
                int width = OutW;
                int height = OutH;

                BitmapData data = _dst.LockBits(new Rectangle(0, 0, width, height),
                                                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    stride = data.Stride;
                    int need = stride * height;
                    if (_pixels == null || _pixels.Length < need) _pixels = new byte[need];
                    Marshal.Copy(data.Scan0, _pixels, 0, need);
                }
                finally
                {
                    _dst.UnlockBits(data);
                }

                _frameSeq++;

                List<int> changed = new List<int>(16);
                long[] fresh = new long[_hashes.Length];

                for (int ty = 0; ty < _gridY; ty++)
                {
                    for (int tx = 0; tx < _gridX; tx++)
                    {
                        int idx = ty * _gridX + tx;
                        long h = HashTile(tx, ty, stride, width, height);
                        fresh[idx] = h;
                        if (!_hashesReady || _hashes[idx] != h) changed.Add(idx);
                    }
                }

                bool keyframe = !_hashesReady || (_frameSeq % _keyframeInterval) == 0;

                if (changed.Count == 0 && !keyframe) return null;
                if (changed.Count == 0) return null;

                bool wholeFrame = keyframe || changed.Count * 2 >= _hashes.Length;

                List<byte[]> blobs = new List<byte[]>(changed.Count);
                List<int> xs = new List<int>(changed.Count);
                List<int> ys = new List<int>(changed.Count);
                List<int> ws = new List<int>(changed.Count);
                List<int> hs = new List<int>(changed.Count);

                if (wholeFrame)
                {
                    xs.Add(0); ys.Add(0); ws.Add(width); hs.Add(height);
                    blobs.Add(EncodeRect(new Rectangle(0, 0, width, height), quality));
                }
                else
                {
                    for (int i = 0; i < changed.Count; i++)
                    {
                        int idx = changed[i];
                        int tx = idx % _gridX;
                        int ty = idx / _gridX;
                        int x = tx * _tile;
                        int y = ty * _tile;
                        int w = Math.Min(_tile, width - x);
                        int h = Math.Min(_tile, height - y);
                        if (w <= 0 || h <= 0) continue;

                        xs.Add(x); ys.Add(y); ws.Add(w); hs.Add(h);
                        blobs.Add(EncodeRect(new Rectangle(x, y, w, h), quality));
                    }
                }

                if (blobs.Count == 0) return null;

                Buffer.BlockCopy(fresh, 0, _hashes, 0, fresh.Length * sizeof(long));
                _hashesReady = true;

                using (MemoryStream ms = new MemoryStream(64 * 1024))
                {
                    using (BinaryWriter w = new BinaryWriter(ms))
                    {
                        w.Write((ushort)width);
                        w.Write((ushort)height);
                        w.Write((ushort)_tile);
                        w.Write((uint)_frameSeq);
                        w.Write((ushort)blobs.Count);

                        for (int i = 0; i < blobs.Count; i++)
                        {
                            w.Write((ushort)xs[i]);
                            w.Write((ushort)ys[i]);
                            w.Write((ushort)ws[i]);
                            w.Write((ushort)hs[i]);
                            w.Write((uint)blobs[i].Length);
                            w.Write(blobs[i]);
                        }
                    }
                    return ms.ToArray();
                }
            }
        }

        private long HashTile(int tx, int ty, int stride, int width, int height)
        {
            int x0 = tx * _tile;
            int y0 = ty * _tile;
            int x1 = Math.Min(x0 + _tile, width);
            int y1 = Math.Min(y0 + _tile, height);
            int rowBytes = (x1 - x0) * 4;

            unchecked
            {
                long h = -3750763034362895579L; // FNV-1a 64 offset basis
                for (int y = y0; y < y1; y++)
                {
                    int rowBase = y * stride + x0 * 4;
                    for (int i = 0; i < rowBytes; i += 8)
                    {
                        h ^= _pixels[rowBase + i];
                        h *= 1099511628211L;
                    }
                }
                return h;
            }
        }

        private byte[] EncodeRect(Rectangle rect, int quality)
        {
            using (Bitmap crop = _dst.Clone(rect, PixelFormat.Format24bppRgb))
            using (MemoryStream ms = new MemoryStream(4096))
            using (EncoderParameters ps = new EncoderParameters(1))
            {
                ps.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
                crop.Save(ms, _jpeg, ps);
                return ms.ToArray();
            }
        }

        // Bruges af forhaandsvisningen i vinduet. Blokerer aldrig UI-traaden:
        // er fangsttraaden midt i en ramme, springer vi bare tegningen over.
        public bool DrawFrame(Graphics g, Rectangle target)
        {
            if (!Monitor.TryEnter(_sync)) return false;
            try
            {
                if (_dst == null) return false;
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(_dst, target);
                return true;
            }
            finally
            {
                Monitor.Exit(_sync);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_dstG != null) _dstG.Dispose();
                if (_dst != null) _dst.Dispose();
                if (_srcG != null) _srcG.Dispose();
                if (_src != null) _src.Dispose();
                _dst = null;
                _src = null;
            }
        }
    }
}
