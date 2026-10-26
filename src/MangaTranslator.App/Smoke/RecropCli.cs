using MangaTranslator.App.Services;
using MangaTranslator.Core;
using MangaTranslator.Core.Imaging;
using MangaTranslator.Plugins.Ocr.PpOcr;

namespace MangaTranslator.App.Smoke;

/// <summary>dev CLI: --recrop &lt;in.png&gt; x y w h [cw|ccw|auto] — 박스 하나 rec 품질 검사(확대 저장 포함).</summary>
public static class RecropCli
{
    public static int Run(string imagePath, int x, int y, int w, int h, string mode)
    {
        if (!EngineContext.HasVlmRec) { Console.Error.WriteLine($"rec VLM GGUF 없음: {EngineContext.VlmDir}"); return 2; }
        // GUI 🔄 재인식과 동일 경로 통일(VlmOcrEngine.RecognizeBoxAsync) — VLM 네이티브 방향, mode 무시
        using var engine = new MangaTranslator.Plugins.Ocr.VlmOcr.VlmOcrEngine(
            EngineContext.PpOcrDetPath, EngineContext.PpOcrRecPath, EngineContext.PpOcrDictPath)
        { VlmRec = EngineContext.CreateVlmRec(), OwnsVlm = true };

        var page = imagePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? PngCodec.Decode(imagePath) : ImageIO.Decode(imagePath);
        var box = new BoundingBox(x, y, x + w, y + h).Clamp(page.Width, page.Height);
        var crop = page.Crop(box);

        // 4× nearest upscale 저장 — 사람이 눈으로 확인용
        var big = new ImageData(crop.Width * 4, crop.Height * 4);
        for (int yy = 0; yy < crop.Height; yy++)
        for (int xx = 0; xx < crop.Width; xx++)
        {
            var s = crop.Row(yy);
            for (int dy = 0; dy < 4; dy++)
            {
                var dr = big.Row(yy * 4 + dy);
                for (int dx = 0; dx < 4; dx++)
                    for (int c = 0; c < 4; c++)
                        dr[xx * 16 + dx * 4 + c] = s[xx * 4 + c];
            }
        }
        var cropPath = Path.ChangeExtension(imagePath, ".crop.png");
        PngCodec.Encode(big, cropPath);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (text, conf, vertical) = engine.RecognizeBoxAsync(page, box);
        sw.Stop();
        Console.WriteLine($"box=({x},{y})~({x + w},{y + h}) conf={conf:0.00} vert={vertical} {sw.ElapsedMilliseconds}ms text='{text}'  crop4x->{cropPath}");
        return 0;
    }
}
