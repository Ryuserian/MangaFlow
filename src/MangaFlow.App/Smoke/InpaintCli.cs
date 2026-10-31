using System.Diagnostics;
using MangaFlow.App.Services;
using MangaFlow.Core.Imaging;
using MangaFlow.ModelManager;
using MangaFlow.Plugins.Inpaint.Lama;
using MangaFlow.Plugins.Ocr.PpOcr;

namespace MangaFlow.App.Smoke;

/// <summary>
/// dev CLI: --inpainttest &lt;in.png&gt; [maxRegions] — OCR 박스 → LaMa content-fill 검증.
/// 산출: in.inpaint.png(제거 결과), in.mask.png(마스크 시각화, white=fill 영역).
/// </summary>
public static class InpaintCli
{
    public static int Run(string imagePath, int maxRegions)
    {
        var lamaPath = Path.Combine(ModelCatalogManager.DefaultRootDir,
            "inpaint", "Carve_LaMa-ONNX", "lama_fp32.onnx");
        if (!File.Exists(lamaPath))
        { Console.Error.WriteLine($"missing {lamaPath}"); return 1; }

        // jpg(진보적 포함)도 디코딩되는 ImageIO 경로 — SingleModeViewModel.LoadFromAsync과 동일 철학
        var page = ImageIO.Decode(imagePath);
        Console.WriteLine($"page {page.Width}x{page.Height}");

        using var ocr = new PpOcrEngine(
            EngineContext.PpOcrDetPath, EngineContext.PpOcrRecPath, EngineContext.PpOcrDictPath);
        var regions = ocr.RecognizeAsync(page).GetAwaiter().GetResult();
        if (maxRegions > 0 && regions.Count > maxRegions)
            regions = regions.Take(maxRegions).ToList();
        Console.WriteLine($"regions={regions.Count}");

        // mask 시각화 (검정 배경 + white=fill)
        var maskImg = new ImageData(page.Width, page.Height);
        maskImg.FillRect(new BoundingBox(0, 0, page.Width, page.Height), 0, 0, 0);
        foreach (var r in regions)
            maskImg.FillRect(r.InpaintBox(6), 255, 255, 255);
        PngCodec.Encode(maskImg, Path.ChangeExtension(imagePath, ".mask.png"));

        var boxes = regions.Select(r => r.InpaintBox(6)).ToList();
        using var lama = new LamaInpaintEngine { ModelPath = lamaPath };
        var sw = Stopwatch.StartNew();
        var filled = lama.FillAsync(page, boxes).GetAwaiter().GetResult();
        sw.Stop();
        var outPath = Path.ChangeExtension(imagePath, ".inpaint.png");
        PngCodec.Encode(filled, outPath);
        Console.WriteLine($"lama done in {sw.Elapsed.TotalSeconds:0.0}s -> {outPath}");
        return 0;
    }
}
