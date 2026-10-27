using MangaTranslator.Core.Imaging;
using MangaTranslator.ModelManager;
using MangaTranslator.Plugins.Ocr.PpOcr;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MangaTranslator.App.Smoke;

/// <summary>
/// dev probe: --lamatest <image> — LaMa ONNX 정규화 스케일 계약 판정.
/// [0,1] 입력 후 raw 출력 통계를 print → 출력 스케일이 [0,1]인지 [0,255]인지 [-1,1]인지 확정.
/// mask는 타일 중앙 사각형(테스트 이미지 텍스트 위치). 뒷배경 ground-truth와 hole 복원값을 비교한다.
/// </summary>
public static class LamaProbeCli
{
    public static int Run(string imagePath)
    {
        var lamaPath = Path.Combine(ModelCatalogManager.DefaultRootDir,
            "inpaint", "Carve_LaMa-ONNX", "lama_fp32.onnx");
        if (!File.Exists(lamaPath)) { Console.Error.WriteLine("missing " + lamaPath); return 1; }

        var page = ImageIO.Decode(imagePath);
        const int T = 512;
        int x0 = Math.Clamp(page.Width / 2 - T / 2, 0, Math.Max(0, page.Width - T));
        int y0 = Math.Clamp(page.Height / 3 - T / 2, 0, Math.Max(0, page.Height - T));

        var img = new float[3 * T * T];
        var msk = new float[T * T];
        var plane = T * T;
        // hole = 중앙 1/3 사각형
        int hx0 = T / 3, hy0 = T / 3, hx1 = 2 * T / 3, hy1 = 2 * T / 3;
        double bgSum = 0; int bgN = 0;
        for (var ty = 0; ty < T; ty++)
        {
            var py = y0 + ty;
            var sRow = py < page.Height ? page.Row(py) : Array.Empty<byte>();
            for (var tx = 0; tx < T; tx++)
            {
                var px = x0 + tx;
                var o = ty * T + tx;
                byte r = 255, g = 255, b = 255;
                if (px < page.Width && sRow.Length > px * 4)
                {
                    b = sRow[px * 4 + 0]; g = sRow[px * 4 + 1]; r = sRow[px * 4 + 2];
                }
                img[o] = r / 255f; img[plane + o] = g / 255f; img[2 * plane + o] = b / 255f;
                var hole = tx >= hx0 && tx < hx1 && ty >= hy0 && ty < hy1;
                msk[o] = hole ? 1f : 0f;
                if (!hole) { bgSum += (r + g + b) / 3.0 / 255.0; bgN++; }
            }
        }

        using var session = OnnxSessionFactory.CreateWithCpuFallback(lamaPath, cuda: false);
        Console.WriteLine("inputs:  " + string.Join(", ", session.InputNames));
        Console.WriteLine("outputs: " + string.Join(", ", session.OutputNames));

        using var res = session.Run(new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(session.InputNames.First(n => !n.Contains("mask", StringComparison.OrdinalIgnoreCase)),
                new DenseTensor<float>(img, new[] { 1, 3, T, T })),
            NamedOnnxValue.CreateFromTensor(session.InputNames.First(n => n.Contains("mask", StringComparison.OrdinalIgnoreCase)),
                new DenseTensor<float>(msk, new[] { 1, 1, T, T })),
        });
        var outT = ((DenseTensor<float>)res.First().AsTensor<float>());
        var buf = outT.Buffer.Span;

        double hMin = 9, hMax = -9, hSum = 0; int hn = 0;
        double oMin = 9, oMax = -9;
        foreach (var v in buf) { oMin = Math.Min(oMin, v); oMax = Math.Max(oMax, v); }
        for (var ty = hy0; ty < hy1; ty++)
            for (var tx = hx0; tx < hx1; tx++)
            {
                var o = ty * T + tx;
                var v = buf[o];
                hMin = Math.Min(hMin, v); hMax = Math.Max(hMax, v); hSum += v; hn++;
            }

        Console.WriteLine($"ground-truth bg mean     = {bgSum / bgN:F3}");
        Console.WriteLine($"raw out whole range      = [{oMin:F3} .. {oMax:F3}]");
        Console.WriteLine($"raw out HOLE stats       = mean {hSum / hn:F3}  min {hMin:F3}  max {hMax:F3}");
        var verdict = oMax > 1.5f ? "OUTPUT IS [0,255] — engine must NOT multiply by 255!"
                  : oMin < -0.05f ? "OUTPUT IS [-1,1] — engine must map (x+1)/2"
                  : hSum / hn > (bgSum / bgN) + 0.15f ? "OUTPUT [0,1] BUT holes washed WHITE — INPUT scaling wrong (needs [-1,1])"
                  : "OUTPUT matches bg — engine normalization OK";
        Console.WriteLine("VERDICT: " + verdict);

        // 시각 확인: raw*255 클립 저장
        var shot = new ImageData(T, T);
        for (var ty = 0; ty < T; ty++)
            for (var tx = 0; tx < T; tx++)
            {
                var o = ty * T + tx;
                var pxRow = shot.Row(ty);
                byte cl(float f) => (byte)Math.Clamp((int)Math.Round(f), 0, 255);
                pxRow[tx * 4 + 0] = cl(buf[2 * plane + o]);
                pxRow[tx * 4 + 1] = cl(buf[plane + o]);
                pxRow[tx * 4 + 2] = cl(buf[o]);
                pxRow[tx * 4 + 3] = 255;
            }
        PngCodec.Encode(shot, Path.ChangeExtension(imagePath, ".lama_probe.png"));
        Console.WriteLine("saved -> " + Path.ChangeExtension(imagePath, ".lama_probe.png"));
        return 0;
    }
}
