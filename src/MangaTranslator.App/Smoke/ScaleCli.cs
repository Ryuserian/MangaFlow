using MangaTranslator.Core.Imaging;

namespace MangaTranslator.App.Smoke;

/// <summary>dev CLI: --scale &lt;in.png&gt; &lt;out.png&gt; [maxW] — 순수 C# bilinear 리사이즈(축소·upscale 양방향).
/// VLM rec 입력 pre-scale 용도: maxW&gt;width면 배율 확대(작은 crop 획 확대 → dots.ocr 오독 감소).</summary>
public static class ScaleCli
{
    public static int Run(string src, string dst, int maxW = 900)
    {
        var img = src.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? PngCodec.Decode(src) : ImageIO.Decode(src);
        if (img.Width == maxW) { PngCodec.Encode(img, dst); Console.WriteLine($"{img.Width}x{img.Height} no scale"); return 0; }
        double s = (double)maxW / img.Width;
        int nw = maxW, nh = Math.Max(1, (int)(img.Height * s + 0.5));
        var o = new ImageData(nw, nh);
        for (int y = 0; y < nh; y++)
        {
            double sy = Math.Min(img.Height - 1, y / s);
            int y0 = (int)sy, y1 = Math.Min(img.Height - 1, y0 + 1);
            double fy = sy - y0;
            var r0 = img.Row(y0); var r1 = img.Row(y1);
            var dr = o.Row(y);
            for (int x = 0; x < nw; x++)
            {
                double sx = Math.Min(img.Width - 1, x / s);
                int x0 = (int)sx, x1 = Math.Min(img.Width - 1, x0 + 1);
                double fx = sx - x0, wx = 1 - fx, wy = 1 - fy;
                for (int c = 0; c < 4; c++)
                {
                    int o0 = x0 * 4 + c, o1 = x1 * 4 + c;
                    dr[x * 4 + c] = (byte)((r0[o0] * wx + r0[o1] * fx) * wy + (r1[o0] * wx + r1[o1] * fx) * fy);
                }
            }
        }
        PngCodec.Encode(o, dst);
        Console.WriteLine($"{img.Width}x{img.Height} -> {nw}x{nh} {dst}");
        return 0;
    }
}
