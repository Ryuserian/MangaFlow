using MangaTranslator.App.Services;
using MangaTranslator.Core.Imaging;

namespace MangaTranslator.App.Smoke;

/// <summary>
/// dev CLI: --inpaintdiff <orig.jpg|png> <clean.png>
/// LaMa가 마스크(검출 박스) 밖 픽셀을 건드렸는지 계측: 다른 픽셀의 수·비율·
/// 연결 요소 근사 박스 목록을 출력한다. 정상 동작이면 변경 픽셀은 검출 박스 주변에만 존재해야 한다.
/// </summary>
public static class InpaintDiffCli
{
    public static int Run(string origPath, string cleanPath)
    {
        var a = origPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? PngCodec.Decode(origPath) : ImageIO.Decode(origPath);
        var b = PngCodec.Decode(cleanPath);

        if (a.Width != b.Width || a.Height != b.Height)
        {
            Console.WriteLine($"SIZE MISMATCH {a.Width}x{a.Height} vs {b.Width}x{b.Height}");
            return 1;
        }

        var w = a.Width; var h = a.Height;
        const int Thresh = 4; // jpg 디코딩 결정적이므로 이론상 0; 여유 4
        var rowChanged = new long[h];
        var colChanged = new long[w];
        long total = 0;

        for (var y = 0; y < h; y++)
        {
            var ra = a.Row(y); var rb = b.Row(y);
            for (var x = 0; x < w; x++)
            {
                var o = x * 4;
                var d = Math.Max(Math.Abs(ra[o] - rb[o]),
                        Math.Max(Math.Abs(ra[o + 1] - rb[o + 1]), Math.Abs(ra[o + 2] - rb[o + 2])));
                if (d > Thresh) { total++; rowChanged[y]++; colChanged[x]++; }
            }
        }

        Console.WriteLine($"changed pixels: {total:N0} / {w * h:N0} = {total / (double)(w * h):P2} (thresh>{Thresh})");

        // 행 사영 → 밴드 분리, 밴드 내 열 사영 → 박스 추정
        var boxes = new List<(int X0, int Y0, int X1, int Y1)>();
        var rr = 0;
        while (rr < h)
        {
            if (rowChanged[rr] == 0) { rr++; continue; }
            var y0 = rr;
            while (rr < h && rowChanged[rr] > 0) rr++;
            var y1 = rr;
            // 밴드 내 열 프로필을 1회 계산 후 세그먼트 분리
            var bandCols = new long[w];
            for (var yy = y0; yy < y1; yy++)
            {
                var ra = a.Row(yy); var rb = b.Row(yy);
                for (var xx = 0; xx < w; xx++)
                {
                    var o = xx * 4;
                    var d = Math.Max(Math.Abs(ra[o] - rb[o]),
                            Math.Max(Math.Abs(ra[o + 1] - rb[o + 1]), Math.Abs(ra[o + 2] - rb[o + 2])));
                    if (d > Thresh) bandCols[xx]++;
                }
            }
            var x = 0;
            while (x < w)
            {
                while (x < w && bandCols[x] == 0) x++;
                if (x >= w) break;
                var x0 = x;
                while (x < w && bandCols[x] > 0) x++;
                boxes.Add((x0, y0, x, y1));
            }
        }

        Console.WriteLine($"changed clusters (projection): {boxes.Count}");
        foreach (var bx in boxes)
            Console.WriteLine($"  box ({bx.X0},{bx.Y0})-({bx.X1},{bx.Y1})  size {bx.X1 - bx.X0}x{bx.Y1 - bx.Y0}");
        return 0;
    }
}
