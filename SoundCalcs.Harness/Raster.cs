using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using SoundCalcs.Visualization;

namespace SoundCalcs.Harness
{
    /// <summary>
    /// Minimal dependency-free RGBA canvas with PNG output and a built-in 5×7 pixel font.
    /// Good enough to draw heatmaps, walls, speakers and legends for inspection.
    /// </summary>
    public class Raster
    {
        public int Width { get; }
        public int Height { get; }
        private readonly byte[] _px; // RGBA, straight alpha, opaque canvas

        public Raster(int width, int height, Rgba background)
        {
            Width = width;
            Height = height;
            _px = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                _px[i * 4] = background.R;
                _px[i * 4 + 1] = background.G;
                _px[i * 4 + 2] = background.B;
                _px[i * 4 + 3] = 255;
            }
        }

        public Rgba Get(int x, int y)
        {
            int i = (y * Width + x) * 4;
            return new Rgba(_px[i], _px[i + 1], _px[i + 2], _px[i + 3]);
        }

        /// <summary>Source-over blend of a straight-alpha colour onto the opaque canvas.</summary>
        public void Blend(int x, int y, Rgba c)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height || c.A == 0) return;
            int i = (y * Width + x) * 4;
            if (c.A == 255)
            {
                _px[i] = c.R; _px[i + 1] = c.G; _px[i + 2] = c.B;
                return;
            }
            double a = c.A / 255.0;
            _px[i]     = (byte)Math.Round(c.R * a + _px[i]     * (1 - a));
            _px[i + 1] = (byte)Math.Round(c.G * a + _px[i + 1] * (1 - a));
            _px[i + 2] = (byte)Math.Round(c.B * a + _px[i + 2] * (1 - a));
        }

        public void FillRect(double x0, double y0, double x1, double y1, Rgba c)
        {
            int ix0 = (int)Math.Floor(Math.Min(x0, x1)), ix1 = (int)Math.Ceiling(Math.Max(x0, x1));
            int iy0 = (int)Math.Floor(Math.Min(y0, y1)), iy1 = (int)Math.Ceiling(Math.Max(y0, y1));
            for (int y = Math.Max(0, iy0); y < Math.Min(Height, iy1); y++)
                for (int x = Math.Max(0, ix0); x < Math.Min(Width, ix1); x++)
                    Blend(x, y, c);
        }

        public void StrokeRect(double x0, double y0, double x1, double y1, Rgba c, double width = 1)
        {
            Line(x0, y0, x1, y0, c, width);
            Line(x1, y0, x1, y1, c, width);
            Line(x1, y1, x0, y1, c, width);
            Line(x0, y1, x0, y0, c, width);
        }

        /// <summary>Thick line drawn as a capsule (distance-to-segment test).</summary>
        public void Line(double x0, double y0, double x1, double y1, Rgba c, double width = 1)
        {
            double r = Math.Max(0.5, width / 2);
            int minX = (int)Math.Floor(Math.Min(x0, x1) - r), maxX = (int)Math.Ceiling(Math.Max(x0, x1) + r);
            int minY = (int)Math.Floor(Math.Min(y0, y1) - r), maxY = (int)Math.Ceiling(Math.Max(y0, y1) + r);
            double dx = x1 - x0, dy = y1 - y0;
            double len2 = dx * dx + dy * dy;
            for (int y = Math.Max(0, minY); y <= Math.Min(Height - 1, maxY); y++)
            {
                for (int x = Math.Max(0, minX); x <= Math.Min(Width - 1, maxX); x++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    double t = len2 > 0 ? ((px - x0) * dx + (py - y0) * dy) / len2 : 0;
                    t = Math.Max(0, Math.Min(1, t));
                    double qx = x0 + t * dx - px, qy = y0 + t * dy - py;
                    if (qx * qx + qy * qy <= r * r) Blend(x, y, c);
                }
            }
        }

        public void FillCircle(double cx, double cy, double radius, Rgba c)
        {
            for (int y = (int)(cy - radius - 1); y <= (int)(cy + radius + 1); y++)
                for (int x = (int)(cx - radius - 1); x <= (int)(cx + radius + 1); x++)
                {
                    double ddx = x + 0.5 - cx, ddy = y + 0.5 - cy;
                    if (ddx * ddx + ddy * ddy <= radius * radius) Blend(x, y, c);
                }
        }

        public void StrokeCircle(double cx, double cy, double radius, Rgba c, double width = 1)
        {
            double rin = radius - width / 2, rout = radius + width / 2;
            for (int y = (int)(cy - rout - 1); y <= (int)(cy + rout + 1); y++)
                for (int x = (int)(cx - rout - 1); x <= (int)(cx + rout + 1); x++)
                {
                    double ddx = x + 0.5 - cx, ddy = y + 0.5 - cy;
                    double d2 = ddx * ddx + ddy * ddy;
                    if (d2 <= rout * rout && d2 >= rin * rin) Blend(x, y, c);
                }
        }

        // -----------------------------------------------------------------
        // Text (5×7 bitmap font, uppercase ASCII subset)
        // -----------------------------------------------------------------

        public const int GlyphW = 5, GlyphH = 7;

        public static int TextWidth(string text, int scale = 1) => text.Length * (GlyphW + 1) * scale;

        public void Text(double x, double y, string text, Rgba c, int scale = 1)
        {
            int cx = (int)x;
            foreach (char ch0 in text.ToUpperInvariant())
            {
                char ch = ch0 == '–' || ch0 == '—' ? '-' : ch0;
                if (Font.TryGetValue(ch, out string[] rows))
                {
                    for (int ry = 0; ry < GlyphH; ry++)
                        for (int rx = 0; rx < GlyphW; rx++)
                            if (rows[ry][rx] == '#')
                                FillRect(cx + rx * scale, y + ry * scale,
                                         cx + (rx + 1) * scale, y + (ry + 1) * scale, c);
                }
                cx += (GlyphW + 1) * scale;
            }
        }

        private static readonly Dictionary<char, string[]> Font = BuildFont();

        private static Dictionary<char, string[]> BuildFont()
        {
            // Each glyph: 7 rows of 5 columns, '#' = ink.
            string[] defs =
            {
                "0 .###. #...# #..## #.#.# ##..# #...# .###.",
                "1 ..#.. .##.. ..#.. ..#.. ..#.. ..#.. .###.",
                "2 .###. #...# ....# ...#. ..#.. .#... #####",
                "3 ####. ....# ....# .###. ....# ....# ####.",
                "4 ...#. ..##. .#.#. #..#. ##### ...#. ...#.",
                "5 ##### #.... ####. ....# ....# #...# .###.",
                "6 .###. #.... #.... ####. #...# #...# .###.",
                "7 ##### ....# ...#. ..#.. .#... .#... .#...",
                "8 .###. #...# #...# .###. #...# #...# .###.",
                "9 .###. #...# #...# .#### ....# ....# .###.",
                "A .###. #...# #...# ##### #...# #...# #...#",
                "B ####. #...# #...# ####. #...# #...# ####.",
                "C .###. #...# #.... #.... #.... #...# .###.",
                "D ####. #...# #...# #...# #...# #...# ####.",
                "E ##### #.... #.... ####. #.... #.... #####",
                "F ##### #.... #.... ####. #.... #.... #....",
                "G .###. #...# #.... #.### #...# #...# .####",
                "H #...# #...# #...# ##### #...# #...# #...#",
                "I .###. ..#.. ..#.. ..#.. ..#.. ..#.. .###.",
                "J ..### ...#. ...#. ...#. ...#. #..#. .##..",
                "K #...# #..#. #.#.. ##... #.#.. #..#. #...#",
                "L #.... #.... #.... #.... #.... #.... #####",
                "M #...# ##.## #.#.# #.#.# #...# #...# #...#",
                "N #...# #...# ##..# #.#.# #..## #...# #...#",
                "O .###. #...# #...# #...# #...# #...# .###.",
                "P ####. #...# #...# ####. #.... #.... #....",
                "Q .###. #...# #...# #...# #.#.# #..#. .##.#",
                "R ####. #...# #...# ####. #.#.. #..#. #...#",
                "S .#### #.... #.... .###. ....# ....# ####.",
                "T ##### ..#.. ..#.. ..#.. ..#.. ..#.. ..#..",
                "U #...# #...# #...# #...# #...# #...# .###.",
                "V #...# #...# #...# #...# #...# .#.#. ..#..",
                "W #...# #...# #...# #.#.# #.#.# #.#.# .#.#.",
                "X #...# #...# .#.#. ..#.. .#.#. #...# #...#",
                "Y #...# #...# .#.#. ..#.. ..#.. ..#.. ..#..",
                "Z ##### ....# ...#. ..#.. .#... #.... #####",
                ". ..... ..... ..... ..... ..... .##.. .##..",
                ", ..... ..... ..... ..... .##.. ..#.. .#...",
                "- ..... ..... ..... ##### ..... ..... .....",
                "+ ..... ..#.. ..#.. ##### ..#.. ..#.. .....",
                ": ..... .##.. .##.. ..... .##.. .##.. .....",
                "( ...#. ..#.. .#... .#... .#... ..#.. ...#.",
                ") .#... ..#.. ...#. ...#. ...#. ..#.. .#...",
                "/ ....# ...#. ...#. ..#.. .#... .#... #....",
                "% ##..# ##.#. ...#. ..#.. .#... .#.## #..##",
                "= ..... ..... ##### ..... ##### ..... .....",
                "_ ..... ..... ..... ..... ..... ..... #####",
                "< ...#. ..#.. .#... #.... .#... ..#.. ...#.",
                "> .#... ..#.. ...#. ....# ...#. ..#.. .#...",
                "@ .###. #...# #.### #.#.# #.### #.... .####",
                "# .#.#. .#.#. ##### .#.#. ##### .#.#. .#.#.",
                "' ..#.. ..#.. .#... ..... ..... ..... .....",
                "~ ..... ..... .#... #.#.# ...#. ..... .....",
                "[ .###. .#... .#... .#... .#... .#... .###.",
                "] .###. ...#. ...#. ...#. ...#. ...#. .###.",
                "* ..... #.#.# .###. ##### .###. #.#.# .....",
                "± ..#.. ..#.. ##### ..#.. ..#.. ..... #####",
                "² .##.. #..#. ..#.. .#... ####. ..... .....",
            };

            var font = new Dictionary<char, string[]>();
            foreach (string def in defs)
            {
                string[] parts = def.Split(' ');
                font[parts[0][0]] = new[] { parts[1], parts[2], parts[3], parts[4], parts[5], parts[6], parts[7] };
            }
            font[' '] = new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." };
            return font;
        }

        // -----------------------------------------------------------------
        // PNG encoding (RGB, 8-bit, zlib via System.IO.Compression)
        // -----------------------------------------------------------------

        public void SavePng(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            using (var fs = File.Create(path))
            {
                fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

                var ihdr = new byte[13];
                WriteBE(ihdr, 0, (uint)Width);
                WriteBE(ihdr, 4, (uint)Height);
                ihdr[8] = 8;  // bit depth
                ihdr[9] = 2;  // colour type RGB
                WriteChunk(fs, "IHDR", ihdr);

                byte[] raw = new byte[Height * (Width * 3 + 1)];
                int o = 0;
                for (int y = 0; y < Height; y++)
                {
                    raw[o++] = 0; // filter: none
                    for (int x = 0; x < Width; x++)
                    {
                        int i = (y * Width + x) * 4;
                        raw[o++] = _px[i];
                        raw[o++] = _px[i + 1];
                        raw[o++] = _px[i + 2];
                    }
                }

                byte[] compressed;
                using (var ms = new MemoryStream())
                {
                    using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                        z.Write(raw, 0, raw.Length);
                    compressed = ms.ToArray();
                }
                WriteChunk(fs, "IDAT", compressed);
                WriteChunk(fs, "IEND", Array.Empty<byte>());
            }
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            WriteBE(len, 0, (uint)data.Length);
            s.Write(len, 0, 4);
            byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = Crc32(typeBytes, 0xFFFFFFFFu);
            crc = Crc32(data, crc) ^ 0xFFFFFFFFu;
            var crcBytes = new byte[4];
            WriteBE(crcBytes, 0, crc);
            s.Write(crcBytes, 0, 4);
        }

        private static void WriteBE(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        private static uint[] _crcTable;

        private static uint Crc32(byte[] data, uint crc)
        {
            if (_crcTable == null)
            {
                var t = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++)
                        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    t[n] = c;
                }
                _crcTable = t;
            }
            foreach (byte b in data)
                crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
