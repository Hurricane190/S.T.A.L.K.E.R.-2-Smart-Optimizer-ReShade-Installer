using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZoneTune.Core.Models;

namespace ZoneTune.UI;

/// <summary>
/// Renders an explicitly synthetic "Zone" scene and applies a preset's stylistic
/// profile to it. This is a colour-preview aid only — never presented as a game
/// screenshot. It also avoids copying author screenshots or game assets.
/// </summary>
public static class PresetPreviewRenderer
{
    private const int W = 560;
    private const int H = 300;
    private static readonly Dictionary<string, BitmapSource> Cache = new();

    public static BitmapSource Render(PresetEntry preset, bool original = false)
    {
        var key = (original ? "orig:" : "p:") + preset.Id + ":" + preset.Preview.Contrast + ":" + preset.Preview.Saturation +
                  ":" + preset.Preview.Warmth + ":" + preset.Preview.Sharpen + ":" + preset.Preview.Vignette + ":" + preset.Preview.Exposure + ":" + preset.Preview.Glow;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        if (Cache.Count > 40) Cache.Clear();
        var bitmap = RenderInternal(original ? new PresetProfile() : preset.Preview);
        Cache[key] = bitmap;
        return bitmap;
    }

    /// <summary>Left half original, right half graded. Makes subtle presets comparable in a thumbnail.</summary>
    public static BitmapSource RenderComparison(PresetEntry preset)
    {
        var key = "cmp:" + preset.Id + ":" + preset.Preview.Contrast + ":" + preset.Preview.Saturation +
                  ":" + preset.Preview.Warmth + ":" + preset.Preview.Sharpen + ":" + preset.Preview.Vignette + ":" + preset.Preview.Exposure + ":" + preset.Preview.Glow;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        if (Cache.Count > 40) Cache.Clear();

        var before = Render(preset, original: true);
        var after = Render(preset);
        var stride = W * 4;
        var a = new byte[stride * H];
        var b = new byte[stride * H];
        before.CopyPixels(a, stride, 0);
        after.CopyPixels(b, stride, 0);
        var split = (int)(W * 0.5);
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var i = y * stride + x * 4;
                var src = x < split ? a : b;
                b[i] = src[i];
                b[i + 1] = src[i + 1];
                b[i + 2] = src[i + 2];
                b[i + 3] = 255;
            }
        }
        // Divider
        for (var y = 0; y < H; y++)
        {
            var i = y * stride + split * 4;
            b[i] = 60; b[i + 1] = 163; b[i + 2] = 227; b[i + 3] = 255; // BGR amber
        }
        var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgr32, null, b, stride);
        bmp.Freeze();
        Cache[key] = bmp;
        return bmp;
    }

    private static BitmapSource RenderInternal(PresetProfile p)
    {
        var pixels = new byte[W * H * 4];
        var baseR = new double[W * H];
        var baseG = new double[W * H];
        var baseB = new double[W * H];
        var horizon = (int)(H * 0.58);
        var sunX = W * 0.72;
        var sunY = H * 0.44;

        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                double r, g, b;
                if (y < horizon)
                {
                    var t = y / (double)horizon;
                    // Dusty overcast sky with a weak sun break.
                    r = Lerp(0.16, 0.55, Math.Pow(t, 1.7));
                    g = Lerp(0.22, 0.48, Math.Pow(t, 1.5));
                    b = Lerp(0.28, 0.40, Math.Pow(t, 1.2));
                    var dx = (x - sunX) / (W * 0.20);
                    var dy = (y - sunY) / (H * 0.24);
                    var glow = Math.Exp(-(dx * dx + dy * dy) * 2.4) * 0.55;
                    r += glow * 0.45; g += glow * 0.33; b += glow * 0.16;
                    var dx2 = (x - sunX) / 11.0; var dy2 = (y - sunY) / 11.0;
                    if (dx2 * dx2 + dy2 * dy2 < 1) { r = 0.92; g = 0.82; b = 0.62; }
                }
                else
                {
                    var t = (y - horizon) / (double)(H - horizon);
                    r = Lerp(0.15, 0.075, t);
                    g = Lerp(0.15, 0.085, t);
                    b = Lerp(0.11, 0.070, t);
                    // faint road toward horizon
                    var road = Math.Abs(x - W * 0.55) / (W * (0.07 + t * 0.20));
                    if (road < 1) { r += 0.055 * (1 - road); g += 0.050 * (1 - road); b += 0.038 * (1 - road); }
                }

                // distant industrial silhouettes
                foreach (var (bx, bw, bh, shade) in Buildings)
                {
                    if (x < bx || x > bx + bw) continue;
                    var top = horizon - bh;
                    if (y < top || y >= horizon) continue;
                    var window = ((x / 6) + (y / 5)) % 11 == 0 && y < horizon - 4;
                    r = shade + (window ? 0.20 : 0);
                    g = shade + (window ? 0.15 : 0);
                    b = shade + (window ? 0.07 : 0);
                }

                // antenna lines on the right
                if (y < horizon - 20 && y > 32)
                {
                    var ax = W * 0.86;
                    if (Math.Abs(x - ax) < 0.8) { r = g = b = 0.09; }
                    if (Math.Abs((x - ax) - (horizon - 20 - y) * 0.28) < 0.7 && y > 55) { r = g = b = 0.10; }
                }

                var noise = (Hash(x, y) - 0.5) * 0.020;
                baseR[y * W + x] = r + noise;
                baseG[y * W + x] = g + noise;
                baseB[y * W + x] = b + noise;
            }
        }

        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var i = y * W + x;
                var r = baseR[i];
                var g = baseG[i];
                var b = baseB[i];
                var sharpen = p.Sharpen * 1.2;
                var contrast = 1 + (p.Contrast - 1) * 1.7;
                var saturation = 1 + (p.Saturation - 1) * 1.7;
                var warmth = p.Warmth * 1.5;
                var vignette = p.Vignette * 1.4;

                if (sharpen > 0.001)
                {
                    var xm = Math.Max(0, x - 1);
                    var xp = Math.Min(W - 1, x + 1);
                    var ym = Math.Max(0, y - 1);
                    var yp = Math.Min(H - 1, y + 1);
                    var blurR = (baseR[y * W + xm] + baseR[y * W + xp] + baseR[ym * W + x] + baseR[yp * W + x]) * 0.25;
                    var blurG = (baseG[y * W + xm] + baseG[y * W + xp] + baseG[ym * W + x] + baseG[yp * W + x]) * 0.25;
                    var blurB = (baseB[y * W + xm] + baseB[y * W + xp] + baseB[ym * W + x] + baseB[yp * W + x]) * 0.25;
                    r += (r - blurR) * sharpen * 1.6;
                    g += (g - blurG) * sharpen * 1.6;
                    b += (b - blurB) * sharpen * 1.6;
                }

                r = (r - 0.5) * contrast + 0.5 + p.Exposure;
                g = (g - 0.5) * contrast + 0.5 + p.Exposure;
                b = (b - 0.5) * contrast + 0.5 + p.Exposure;

                var luma = r * 0.2126 + g * 0.7152 + b * 0.0722;
                r = luma + (r - luma) * saturation;
                g = luma + (g - luma) * saturation;
                b = luma + (b - luma) * saturation;

                if (p.Glow > 0.001)
                {
                    var highlight = Math.Max(0, luma - 0.55);
                    r += p.Glow * highlight * 0.55;
                    g += p.Glow * highlight * 0.50;
                    b += p.Glow * highlight * 0.42;
                }

                r += warmth * 0.10;
                g += warmth * 0.025;
                b -= warmth * 0.10;

                if (vignette > 0.001)
                {
                    var nx = (x / (double)W - 0.5) * 2;
                    var ny = (y / (double)H - 0.5) * 2;
                    var v = 1 - vignette * Math.Min(1, (nx * nx + ny * ny) * 0.42);
                    r *= v; g *= v; b *= v;
                }

                var idx = i * 4;
                pixels[idx + 0] = Clamp(b);
                pixels[idx + 1] = Clamp(g);
                pixels[idx + 2] = Clamp(r);
                pixels[idx + 3] = 255;
            }
        }

        var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgr32, null, pixels, W * 4);
        bmp.Freeze();
        return bmp;
    }

    private static readonly (double x, double w, double h, double shade)[] Buildings =
    {
        (W * 0.03, W * 0.10, H * 0.10, 0.10),
        (W * 0.14, W * 0.06, H * 0.16, 0.09),
        (W * 0.21, W * 0.12, H * 0.08, 0.11),
        (W * 0.35, W * 0.05, H * 0.14, 0.09),
        (W * 0.41, W * 0.08, H * 0.07, 0.10),
        (W * 0.52, W * 0.04, H * 0.20, 0.08),
        (W * 0.59, W * 0.09, H * 0.09, 0.10),
        (W * 0.70, W * 0.05, H * 0.12, 0.09),
        (W * 0.78, W * 0.13, H * 0.07, 0.11)
    };

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);

    private static double Hash(int x, int y)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0xFFFF) / 65535.0;
        }
    }
}
