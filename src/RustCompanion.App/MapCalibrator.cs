using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RustCompanion.App;

/// <summary>
/// Автоматическое совмещение картинки RustMaps с картой Rust+. Обе — один и тот же мир, но поле океана по краям у них разное, из-за чего данные Rust+ «уезжают»
/// при приближении. Сравниваются маски суши (вода — синяя) на сетке, и подбирается доля поля океана у картинки RustMaps, при которой маски совпадают лучше всего.
/// </summary>
internal static class MapCalibrator
{
    private const int N = 96;

    /// <summary>Возвращает долю поля океана (0…0.25) и качество совпадения 0…1 (пересечение/объединение суши).</summary>
    public static (double Ratio, double Score) FindMarginRatio(BitmapSource plus, int plusMargin, BitmapSource other)
    {
        var (pa, pw, ph) = Pixels(plus); var (oa, ow, oh) = Pixels(other);
        var pm = plusMargin * (double)pw / Math.Max(1, plus.PixelWidth);
        var a = new bool[N * N]; var land = 0;
        for (var j = 0; j < N; j++)
            for (var i = 0; i < N; i++)
            {
                var x = pm + (i + 0.5) / N * (pw - 2 * pm); var y = pm + (j + 0.5) / N * (ph - 2 * pm);
                if (!IsWater(pa, pw, ph, x, y)) { a[j * N + i] = true; land++; }
            }
        if (land < N * N * 0.03 || land > N * N * 0.97) return (0, 0);
        double bestR = 0, best = -1;
        for (var r = 0.0; r <= 0.25001; r += 0.0025)
        {
            int inter = 0, union = 0;
            for (var j = 0; j < N; j++)
                for (var i = 0; i < N; i++)
                {
                    var x = (r + (i + 0.5) / N * (1 - 2 * r)) * ow; var y = (r + (j + 0.5) / N * (1 - 2 * r)) * oh;
                    var b = !IsWater(oa, ow, oh, x, y); var k = a[j * N + i];
                    if (b && k) inter++; if (b || k) union++;
                }
            var score = union == 0 ? 0 : (double)inter / union;
            if (score > best) { best = score; bestR = r; }
        }
        return (bestR, Math.Max(0, best));
    }

    private static bool IsWater(byte[] d, int w, int h, double x, double y)
    {
        var xi = Math.Clamp((int)x, 0, w - 1); var yi = Math.Clamp((int)y, 0, h - 1); var o = (yi * w + xi) * 4;
        int b = d[o], g = d[o + 1], r = d[o + 2];
        return b - r > 18 && b >= g;
    }

    /// <summary>Уменьшенная копия картинки в формате BGRA (около 512 пикселей по ширине): для маски большего не нужно, а 4096×4096 копировать в память незачем.</summary>
    private static (byte[] Data, int W, int H) Pixels(BitmapSource src)
    {
        BitmapSource s = src;
        if (src.PixelWidth > 512) s = new TransformedBitmap(src, new ScaleTransform(512.0 / src.PixelWidth, 512.0 / src.PixelWidth));
        var c = new FormatConvertedBitmap(s, PixelFormats.Bgra32, null, 0);
        var w = c.PixelWidth; var h = c.PixelHeight; var data = new byte[w * h * 4];
        c.CopyPixels(data, w * 4, 0);
        return (data, w, h);
    }
}
