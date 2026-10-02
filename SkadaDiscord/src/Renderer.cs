// Рисует отчёт в стиле окон Skada: полосы цвета класса, иконки специализаций, шрифт Expressway.
// Несколько окон раскладываются по колонкам.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;

namespace SkadaDiscord
{
    public class Entry
    {
        public string Name, Class, Color, Text;
        public int Spec;
        public double Value, PerSec;
    }

    public class BlockView
    {
        public string Title, Total;
        public double Value;
        public bool Rate;
        public List<Entry> Entries = new List<Entry>();
    }

    public class RenderJob
    {
        public string Title, Subtitle, Info, Footer;
        public bool Success = true;
        public List<BlockView> Blocks = new List<BlockView>();
        public string SummaryTitle = "Итоги боя";
        public List<string[]> Summary = new List<string[]>();
    }

    public static class Renderer
    {
        // координаты иконок в Skada\Media\Textures\icons.blp (сетка 64x64), из Skada\Core\Init.lua
        static readonly Dictionary<string, int[]> ClassCells = new Dictionary<string, int[]> {
            {"WARRIOR", new[]{0,0}}, {"MAGE", new[]{1,0}}, {"ROGUE", new[]{2,0}}, {"DRUID", new[]{3,0}},
            {"BOSS", new[]{5,0}}, {"MONSTER", new[]{6,0}}, {"ENEMY", new[]{7,0}},
            {"HUNTER", new[]{0,1}}, {"SHAMAN", new[]{1,1}}, {"PRIEST", new[]{2,1}}, {"WARLOCK", new[]{3,1}},
            {"PET", new[]{5,1}}, {"UNKNOWN", new[]{6,1}}, {"PLAYER", new[]{7,1}},
            {"PALADIN", new[]{0,2}}, {"DEATHKNIGHT", new[]{1,2}},
        };
        static readonly Dictionary<int, int[]> SpecCells = new Dictionary<int, int[]> {
            {71, new[]{0,3}}, {72, new[]{1,3}}, {73, new[]{2,3}}, {62, new[]{3,3}}, {63, new[]{4,3}}, {64, new[]{5,3}}, {259, new[]{6,3}}, {260, new[]{7,3}},
            {261, new[]{0,4}}, {102, new[]{1,4}}, {103, new[]{2,4}}, {104, new[]{3,4}}, {105, new[]{4,4}}, {253, new[]{5,4}}, {254, new[]{6,4}}, {255, new[]{7,4}},
            {262, new[]{0,5}}, {263, new[]{1,5}}, {264, new[]{2,5}}, {256, new[]{3,5}}, {257, new[]{4,5}}, {258, new[]{5,5}}, {265, new[]{6,5}}, {266, new[]{7,5}},
            {267, new[]{0,6}}, {65, new[]{1,6}}, {66, new[]{2,6}}, {70, new[]{3,6}}, {250, new[]{4,6}}, {251, new[]{5,6}}, {252, new[]{6,6}},
        };

        // ---------------------------------------------------------------------
        // BLP2 / DXT1 (только то, что нужно для icons.blp)

        static Bitmap iconsCache;
        static string iconsCachePath;

        public static Bitmap LoadBlp(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b[0] != 'B' || b[1] != 'L' || b[2] != 'P' || b[3] != '2' || b[8] != 2 || b[9] > 1)
                throw new Exception("unsupported BLP: " + path);
            int w = BitConverter.ToInt32(b, 12), h = BitConverter.ToInt32(b, 16);
            int off = BitConverter.ToInt32(b, 20);
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var px = new int[w * h];
            var pal = new int[4];
            for (int by = 0; by < h / 4; by++)
            for (int bx = 0; bx < w / 4; bx++)
            {
                int p = off + (by * (w / 4) + bx) * 8;
                int c0 = b[p] | (b[p + 1] << 8), c1 = b[p + 2] | (b[p + 3] << 8);
                uint idx = BitConverter.ToUInt32(b, p + 4);
                int r0 = (c0 >> 11 & 31) * 255 / 31, g0 = (c0 >> 5 & 63) * 255 / 63, b0 = (c0 & 31) * 255 / 31;
                int r1 = (c1 >> 11 & 31) * 255 / 31, g1 = (c1 >> 5 & 63) * 255 / 63, b1 = (c1 & 31) * 255 / 31;
                pal[0] = Argb(255, r0, g0, b0);
                pal[1] = Argb(255, r1, g1, b1);
                if (c0 > c1)
                {
                    pal[2] = Argb(255, (2 * r0 + r1) / 3, (2 * g0 + g1) / 3, (2 * b0 + b1) / 3);
                    pal[3] = Argb(255, (r0 + 2 * r1) / 3, (g0 + 2 * g1) / 3, (b0 + 2 * b1) / 3);
                }
                else
                {
                    pal[2] = Argb(255, (r0 + r1) / 2, (g0 + g1) / 2, (b0 + b1) / 2);
                    pal[3] = 0;
                }
                for (int i = 0; i < 16; i++)
                    px[(by * 4 + i / 4) * w + bx * 4 + i % 4] = pal[(idx >> (2 * i)) & 3];
            }
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            System.Runtime.InteropServices.Marshal.Copy(px, 0, data.Scan0, px.Length);
            bmp.UnlockBits(data);
            return bmp;
        }

        static int Argb(int a, int r, int g, int b) { return (a << 24) | (r << 16) | (g << 8) | b; }

        // ---------------------------------------------------------------------
        // отрисовка

        const int ColW = 430, Pad = 12, Gap = 12;
        const int HeaderH = 64, TitleH = 24, BarH = 22, BarGap = 1;

        static Color Hex(string s, int alpha)
        {
            int v;
            try { v = Convert.ToInt32(string.IsNullOrEmpty(s) ? "ffffff" : s, 16); } catch { v = 0xffffff; }
            return Color.FromArgb(alpha, (v >> 16) & 255, (v >> 8) & 255, v & 255);
        }

        class Ctx
        {
            public Graphics G;
            public FontFamily Family;
            public Bitmap Icons;
        }

        static void Text(Ctx c, string s, float size, float x, float y, float w, Color color, StringAlignment align)
        {
            if (w <= 0) return;
            using (var path = new GraphicsPath())
            using (var fmt = new StringFormat(StringFormat.GenericTypographic))
            {
                fmt.Alignment = align;
                fmt.LineAlignment = StringAlignment.Center;
                fmt.Trimming = StringTrimming.EllipsisCharacter;
                fmt.FormatFlags |= StringFormatFlags.NoWrap;
                path.AddString(s ?? "", c.Family, (int)FontStyle.Regular, size, new RectangleF(x, y, w, size * 1.6f), fmt);
                // контур как у шрифта WoW с OUTLINE
                using (var pen = new Pen(Color.FromArgb(230, 0, 0, 0), 2.6f) { LineJoin = LineJoin.Round })
                    c.G.DrawPath(pen, path);
                using (var br = new SolidBrush(color))
                    c.G.FillPath(br, path);
            }
        }

        static float TextAt(float y, float h, float size) { return y + (h - size * 1.6f) / 2f; }

        static float Measure(Ctx c, string s, float size)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            using (var path = new GraphicsPath())
            using (var fmt = new StringFormat(StringFormat.GenericTypographic))
            {
                path.AddString(s, c.Family, (int)FontStyle.Regular, size, new PointF(0, 0), fmt);
                return path.GetBounds().Width;
            }
        }

        static void Icon(Ctx c, Entry e, float x, float y, float size)
        {
            if (c.Icons == null) return;
            int[] cell;
            if (!SpecCells.TryGetValue(e.Spec, out cell) && !ClassCells.TryGetValue(e.Class ?? "UNKNOWN", out cell))
                cell = ClassCells["UNKNOWN"];
            var src = new RectangleF(cell[0] * 64 + 1, cell[1] * 64 + 1, 62, 62);
            c.G.DrawImage(c.Icons, new RectangleF(x, y, size, size), src, GraphicsUnit.Pixel);
        }

        static void TitleBar(Ctx c, float x, float y, float w, string title, string total)
        {
            using (var tb = new LinearGradientBrush(new RectangleF(x, y, w, TitleH), Color.FromArgb(255, 48, 52, 64), Color.FromArgb(255, 26, 28, 34), 90f))
                c.G.FillRectangle(tb, x, y, w, TitleH);
            float tw = string.IsNullOrEmpty(total) ? 0 : Measure(c, total, 14f) + 16;
            Text(c, title, 15f, x + 6, TextAt(y, TitleH, 15f), w - tw - 10, Color.White, StringAlignment.Near);
            if (tw > 0)
                Text(c, total, 14f, x + w - tw, TextAt(y, TitleH, 14f), tw - 6, Color.FromArgb(255, 220, 220, 220), StringAlignment.Far);
        }

        static Color Lighten(Color c, float k) { return Color.FromArgb(c.A, (int)(c.R + (255 - c.R) * k), (int)(c.G + (255 - c.G) * k), (int)(c.B + (255 - c.B) * k)); }
        static Color Darken(Color c, float k) { return Color.FromArgb(c.A, (int)(c.R * (1 - k)), (int)(c.G * (1 - k)), (int)(c.B * (1 - k))); }

        static int WindowHeight(BlockView b) { return TitleH + Math.Max(1, b.Entries.Count) * (BarH + BarGap); }
        static int SummaryHeight(RenderJob r) { return TitleH + r.Summary.Count * (BarH + BarGap) + 4; }

        static void Window(Ctx c, float x, float y, float w, BlockView b)
        {
            var g = c.G;
            using (var bg = new SolidBrush(Color.FromArgb(200, 10, 10, 12)))
                g.FillRectangle(bg, x, y, w, WindowHeight(b));
            TitleBar(c, x, y, w, b.Title, b.Total);

            double maxv = 0;
            foreach (var e in b.Entries) maxv = Math.Max(maxv, e.Value);

            float by = y + TitleH;
            for (int i = 0; i < b.Entries.Count; i++)
            {
                var e = b.Entries[i];
                float bx = x + BarH, bw = w - BarH;
                using (var back = new SolidBrush(Color.FromArgb(150, 45, 45, 50)))
                    g.FillRectangle(back, bx, by, bw, BarH);
                float fill = maxv > 0 ? (float)(bw * e.Value / maxv) : 0;
                if (fill >= 1)
                {
                    var col = Hex(e.Color, 255);
                    using (var bar = new LinearGradientBrush(new RectangleF(bx, by, fill, BarH), col, col, 90f))
                    {
                        var blend = new ColorBlend(3);
                        blend.Colors = new[] { Lighten(col, 0.18f), col, Darken(col, 0.22f) };
                        blend.Positions = new[] { 0f, 0.45f, 1f };
                        bar.InterpolationColors = blend;
                        g.FillRectangle(bar, bx, by, fill, BarH);
                    }
                }
                Icon(c, e, x, by, BarH);
                Text(c, e.Text, 14f, bx + bw * 0.42f, TextAt(by, BarH, 14f), bw * 0.58f - 5, Color.White, StringAlignment.Far);
                Text(c, (i + 1) + ". " + e.Name, 14f, bx + 4, TextAt(by, BarH, 14f), bw * 0.58f, Color.White, StringAlignment.Near);
                by += BarH + BarGap;
            }
            if (b.Entries.Count == 0)
                Text(c, "нет данных", 13f, x + 6, TextAt(by, BarH, 13f), w - 12, Color.Gray, StringAlignment.Near);
        }

        static void Summary(Ctx c, float x, float y, float w, RenderJob r)
        {
            using (var bg = new SolidBrush(Color.FromArgb(200, 10, 10, 12)))
                c.G.FillRectangle(bg, x, y, w, SummaryHeight(r));
            TitleBar(c, x, y, w, r.SummaryTitle, null);
            float ry = y + TitleH + 2;
            foreach (var row in r.Summary)
            {
                Text(c, row[0], 14f, x + 8, TextAt(ry, BarH, 14f), w * 0.5f, Color.FromArgb(255, 170, 172, 180), StringAlignment.Near);
                Text(c, row[1], 14f, x + w * 0.35f, TextAt(ry, BarH, 14f), w * 0.65f - 8, Color.White, StringAlignment.Far);
                ry += BarH + BarGap;
            }
        }

        static readonly object Sync = new object();

        // GDI+ не любит один Bitmap из разных потоков (отправка + предпросмотр), поэтому по очереди
        public static byte[] Render(RenderJob r, string fontPath, string iconsPath, float scale)
        {
            lock (Sync) return RenderLocked(r, fontPath, iconsPath, scale);
        }

        static byte[] RenderLocked(RenderJob r, string fontPath, string iconsPath, float scale)
        {
            var fonts = new PrivateFontCollection();
            FontFamily family;
            if (!string.IsNullOrEmpty(fontPath) && File.Exists(fontPath))
            {
                fonts.AddFontFile(fontPath);
                family = fonts.Families[0];
            }
            else family = new FontFamily("Arial");

            Bitmap icons = null;
            try
            {
                if (iconsCachePath != iconsPath && File.Exists(iconsPath)) { iconsCache = LoadBlp(iconsPath); iconsCachePath = iconsPath; }
                icons = iconsCache;
            }
            catch { icons = null; }

            // раскладка: окна + "Итоги" по колонкам, каждое в самую короткую колонку
            var panels = new List<object>(r.Blocks.Cast<object>());
            if (r.Summary.Count > 0) panels.Add(r);
            int cols = Math.Max(2, Math.Min(3, r.Blocks.Count));
            if (r.Blocks.Count == 1 && r.Summary.Count == 0) cols = 1;
            var colH = new int[cols];
            var placed = new List<Tuple<object, int, int>>(); // панель, колонка, y
            foreach (var p in panels)
            {
                int best = 0;
                for (int i = 1; i < cols; i++) if (colH[i] < colH[best]) best = i;
                placed.Add(Tuple.Create(p, best, colH[best]));
                int h = p is BlockView ? WindowHeight((BlockView)p) : SummaryHeight(r);
                colH[best] += h + Gap;
            }
            int width = Pad * 2 + cols * ColW + (cols - 1) * Gap;
            int height = HeaderH + Pad + Math.Max(TitleH + BarH, colH.Max() - Gap) + Pad + 18;

            var bmp = new Bitmap((int)(width * scale), (int)(height * scale), PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.ScaleTransform(scale, scale);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var c = new Ctx { G = g, Family = family, Icons = icons };

                using (var bg = new LinearGradientBrush(new Rectangle(0, 0, width, height), Color.FromArgb(255, 30, 32, 38), Color.FromArgb(255, 16, 17, 20), 90f))
                    g.FillRectangle(bg, 0, 0, width, height);

                // шапка
                var resultColor = r.Success ? Color.FromArgb(255, 46, 204, 113) : Color.FromArgb(255, 231, 76, 60);
                using (var accent = new SolidBrush(resultColor))
                    g.FillRectangle(accent, 0, 0, 5, HeaderH);
                float infoW = Math.Min(420, width / 2f);
                Text(c, r.Title, 24f, Pad + 4, TextAt(4, 32, 24f), width - Pad * 2, Color.FromArgb(255, 255, 209, 0), StringAlignment.Near);
                Text(c, r.Subtitle, 14f, Pad + 4, TextAt(36, 22, 14f), width - Pad * 2 - infoW - 10, Color.FromArgb(255, 210, 210, 215), StringAlignment.Near);
                Text(c, r.Info, 14f, width - Pad - infoW, TextAt(36, 22, 14f), infoW, resultColor, StringAlignment.Far);

                float top = HeaderH + Pad;
                foreach (var p in placed)
                {
                    float x = Pad + p.Item2 * (ColW + Gap);
                    if (p.Item1 is BlockView) Window(c, x, top + p.Item3, ColW, (BlockView)p.Item1);
                    else Summary(c, x, top + p.Item3, ColW, r);
                }

                Text(c, r.Footer, 12f, Pad, TextAt(height - 22, 18, 12f), width - Pad * 2, Color.FromArgb(255, 120, 122, 130), StringAlignment.Far);
            }

            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                bmp.Dispose();
                fonts.Dispose();
                return ms.ToArray();
            }
        }
    }
}
