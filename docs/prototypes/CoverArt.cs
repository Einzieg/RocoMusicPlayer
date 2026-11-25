using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RocoPlayer.Prototypes;

// Four original sleeve illustrations, rendered at one size for both large covers and thumbnails.
internal static class CoverArt
{
    private static readonly Dictionary<int, BitmapSource> Cache = new();
    private static SolidColorBrush Ink(string color)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        b.Freeze();
        return b;
    }

    internal static BitmapSource Get(int id)
    {
        if (Cache.TryGetValue(id, out var existing)) return existing;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (id == 0) Forest(dc);
            else if (id == 1) Coast(dc);
            else if (id == 2) Night(dc);
            else Courtyard(dc);
        }
        const int size = 512;
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var bytes = new byte[size * size * 4];
        bitmap.CopyPixels(bytes, size * 4, 0);
        var rand = new Random(391 + id);
        for (int i = 0; i < bytes.Length; i += 4)
        {
            int grain = rand.Next(-6, 7);
            for (int k = 0; k < 3; k++) bytes[i + k] = (byte)Math.Clamp(bytes[i + k] + grain, 0, 255);
        }
        var result = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, bytes, size * 4);
        result.Freeze();
        return Cache[id] = result;
    }
    private static void Fill(DrawingContext d, string c) => d.DrawRectangle(Ink(c), null, new Rect(0, 0, 512, 512));
    private static void Shape(DrawingContext d, string c, string path) => d.DrawGeometry(Ink(c), null, Geometry.Parse(path));
    private static void Ring(DrawingContext d, string c, double x, double y, double radius)
        => d.DrawEllipse(Ink(c), null, new Point(x, y), radius, radius);

    private static void Forest(DrawingContext d)
    {
        Fill(d, "#E7DCC0");
        Ring(d, "#D78F5B", 343, 129, 58);
        Shape(d, "#C3C5A6", "M0,200 L43,208 98,184 131,187 174,151 206,168 246,142 266,153 290,177 321,164 345,185 379,164 412,174 460,141 512,158 512,512 0,512 Z");
        Shape(d, "#A6B4A2", "M0,239 Q67,201 110,219 T211,210 L267,177 331,201 365,188 411,202 475,170 512,179 512,512 0,512 Z");
        Shape(d, "#819B89", "M-10,180 Q28,186 65,209 L116,232 Q151,236 184,260 L235,273 Q302,278 354,320 L512,355 512,512 0,512 Z");
        Shape(d, "#587F71", "M0,289 Q78,280 136,298 T235,307 Q289,275 341,267 Q382,252 431,234 L512,209 512,512 0,512 Z");
        Shape(d, "#3A665C", "M-12,289 Q64,297 104,327 Q155,350 242,356 Q309,360 365,378 L512,392 512,512 0,512 Z");
        Shape(d, "#6E917E", "M144,368 Q205,344 277,345 Q330,342 373,321 Q418,299 512,281 L512,328 Q431,345 392,363 Q344,384 262,381 Q208,378 184,391 Z");
        Shape(d, "#A5B7A0", "M228,355 Q259,342 291,331 Q317,320 337,318 Q300,331 281,341 Q261,351 275,355 Q299,361 289,368 Q264,381 224,390 Q186,401 198,409 L154,415 Q151,399 199,384 Q240,371 247,365 Q251,362 228,355 Z");
        Shape(d, "#254F47", "M0,372 Q73,345 129,364 Q175,380 219,405 Q281,432 358,441 L512,471 512,512 0,512 Z");
        Shape(d, "#173F39", "M0,474 Q64,436 138,440 T288,447 Q347,430 410,395 Q460,367 512,358 L512,512 0,512 Z");
        Trees(d, new Random(32), "#244F47", 4, 144, x => 365 + 0.0016 * (x - 63) * (x - 63), 13, 41);
        Trees(d, new Random(28), "#173F39", 294, 521, x => 460 - .46 * (x - 295), 12, 44);
        Trees(d, new Random(18), "#103833", -8, 80, x => 483 - .43 * x, 24, 63);
        Lines(d, "#3091A491", 11, 324, 229, 13);
        Birds(d, "#596958", 145, 104);
    }
    private static void Trees(DrawingContext d, Random r, string color, double start, double end, Func<double, double> ground, int min, int max)
    {
        var brush = Ink(color);
        for (double x = start; x < end; x += r.Next(4, 10))
        {
            var h = r.Next(min, max);
            var w = h * (.11 + r.NextDouble() * .07);
            var y = ground(x);
            var geo = new StreamGeometry();
            using (var p = geo.Open())
            {
                p.BeginFigure(new Point(x, y - h), true, true);
                foreach (var (dx, dy) in new (double, double)[] { (-.54, .42), (-.2, .36), (-.77, .63), (-.41, .57), (-1, .83), (-.12, .75), (-.09, 1), (.09, 1), (.12, .75), (1, .83), (.41, .57), (.77, .63), (.2, .36), (.54, .42) })
                    p.LineTo(new Point(x + dx * w, y - h + dy * h), true, false);
            }
            d.DrawGeometry(brush, null, geo);
        }
    }
    private static void Birds(DrawingContext d, string color, int x, int y)
    {
        var pen = new Pen(Ink(color), 1.6);
        foreach (var (dx, dy, s) in new (int, int, double)[] { (0, 0, 1), (26, -8, .7), (42, 5, .57) })
        {
            d.PushTransform(new TranslateTransform(x + dx, y + dy));
            d.PushTransform(new ScaleTransform(s, s));
            d.DrawGeometry(null, pen, Geometry.Parse("M0,0 Q4,-2 8,2 Q11,-1 16,-1"));
            d.Pop();
            d.Pop();
        }
    }
    private static void Lines(DrawingContext d, string c, double x, double y, double span, int rows)
    {
        var p = new Pen(Ink(c), .7);
        for (int i = 0; i < rows; i++)
        {
            double yy = y + i * 2.1;
            d.DrawLine(p, new Point(x + i * 3, yy), new Point(x + span - i * 4, yy + i * .3));
        }
    }
    private static void Coast(DrawingContext d)
    {
        Fill(d, "#D5DDDB");
        Ring(d, "#E4AA78", 134, 119, 48);
        Shape(d, "#A3B9B6", "M0,229 Q79,198 143,214 T261,210 Q329,204 371,220 L512,221 512,512 0,512 Z");
        Shape(d, "#4F7F83", "M0,222 Q103,225 192,219 Q328,217 512,223 L512,512 0,512 Z");
        Shape(d, "#759898", "M0,249 Q153,238 300,259 Q392,280 512,261 L512,295 Q391,313 269,284 Q115,254 0,277 Z");
        Shape(d, "#D8C4A6", "M512,208 Q453,213 430,233 Q396,249 332,254 Q279,261 274,278 Q270,291 356,300 Q403,305 391,323 Q367,342 233,355 Q153,364 72,391 L0,409 0,512 512,512 Z");
        Shape(d, "#EDF0DA", "M512,219 Q450,222 421,242 Q394,257 326,262 Q287,266 288,277 Q291,283 354,289 Q426,298 412,321 Q395,349 252,363 Q116,378 0,430 L0,413 Q104,367 248,354 Q392,338 398,318 Q402,304 350,298 Q274,291 271,279 Q266,258 327,252 Q389,248 415,236 Q455,213 512,210 Z");
        Shape(d, "#A85D45", "M512,271 Q481,290 481,318 Q475,355 412,381 Q362,402 257,414 Q159,425 92,474 L37,512 512,512 Z");
        Shape(d, "#C07753", "M512,311 L512,512 81,512 Q117,464 240,438 Q388,415 446,382 Q474,368 482,349 Z");
        Birds(d, "#566967", 290, 116);
        Lines(d, "#55D6E6DB", 8, 242, 178, 9);
    }
    private static void Night(DrawingContext d)
    {
        Fill(d, "#3F4F79");
        Ring(d, "#F5DEA6", 338, 137, 54);
        var rand = new Random(88);
        for (int i = 0; i < 67; i++)
        {
            double x = rand.Next(8, 504), y = rand.Next(8, 267);
            if (Math.Pow(x - 338, 2) + Math.Pow(y - 137, 2) > 3600) Ring(d, "#CFD5D6", x, y, rand.NextDouble() * 1.2 + .55);
        }
        Shape(d, "#7B87A3", "M0,286 L54,263 83,270 134,227 185,253 232,210 261,230 319,190 347,202 392,247 445,221 512,257 512,512 0,512 Z");
        Shape(d, "#5C7393", "M0,276 L80,305 126,292 187,312 245,287 293,298 347,275 412,288 463,259 512,267 512,512 0,512 Z");
        Shape(d, "#334D70", "M0,297 Q77,314 151,345 Q254,383 319,371 Q407,346 512,319 L512,512 0,512 Z");
        Shape(d, "#203A54", "M0,420 Q93,377 162,389 T308,408 Q383,408 512,370 L512,512 0,512 Z");
        Trees(d, new Random(36), "#203A54", 2, 164, x => 415 - .21 * x, 16, 55);
        Trees(d, new Random(122), "#1C334B", 353, 528, x => 422 - .26 * (x - 355), 22, 68);
    }
    private static void Courtyard(DrawingContext d)
    {
        Fill(d, "#EBD6BF");
        Ring(d, "#C76343", 379, 126, 67);
        Shape(d, "#E4B092", "M0,170 L143,107 214,148 214,512 0,512 Z");
        Shape(d, "#A25D4F", "M143,107 L215,148 215,512 143,512 Z");
        Shape(d, "#F0CBA6", "M217,205 L512,146 512,512 217,512 Z");
        Shape(d, "#B57561", "M254,421 L254,304 C254,188 416,159 416,276 L416,421 Z");
        Shape(d, "#704B4A", "M280,421 L280,309 Q284,227 349,223 Q407,215 416,275 L416,421 Z");
        Shape(d, "#D7A283", "M0,372 L269,318 512,370 512,512 0,512 Z");
        Shape(d, "#E8C4A1", "M-20,512 L-20,466 53,452 53,427 131,410 131,387 208,371 208,347 296,328 349,342 349,512 Z");
        Shape(d, "#A56856", "M-20,466 L98,485 98,512 -20,512 Z M53,427 L173,448 173,471 53,452 Z M131,387 L250,411 250,432 131,410 Z M208,347 L330,369 330,392 208,371 Z");
        Birds(d, "#956E5C", 63, 94);
    }
}
