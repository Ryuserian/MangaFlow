using MangaTranslator.App.Services;
using MangaTranslator.Core;
using MangaTranslator.Core.Imaging;
using MangaTranslator.Plugins.Ocr.PpOcr;

namespace MangaTranslator.App.Smoke;

/// <summary>
/// dev CLI: dotnet run --project src/MangaTranslator.App -- --ocrtest &lt;image&gt; [annot_out]
/// PP-OCR 실모델로 인식 실행 → 박스 주석 PNG + 텍스트 리포트 저장. (native OpenCV 무依赖)
/// </summary>
public static class OcrCli
{
    public static int Run(string imagePath, string? annotatedOut)
    {
        try
        {
            using var engine = new PpOcrEngine(
                EngineContext.PpOcrDetPath,
                EngineContext.PpOcrRecPath,
                EngineContext.PpOcrDictPath);
            // OCR_ENGINE=ppocr → det+PP rec(dark route) — 기본은 det+Qwen3-VL rec
            bool pp = Environment.GetEnvironmentVariable("OCR_ENGINE") == "ppocr";
            using var vlmEngine = new MangaTranslator.Plugins.Ocr.VlmOcr.VlmOcrEngine(
                EngineContext.PpOcrDetPath, EngineContext.PpOcrRecPath, EngineContext.PpOcrDictPath)
            { VlmRec = EngineContext.CreateVlmRec(), OwnsVlm = true };
            MangaTranslator.Core.Interfaces.IOcrEngine active = pp ? engine : vlmEngine;
            Console.WriteLine($"ocr engine={active.Name}");

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // CLI는 png=자체 디코더, jpg 등=ImageSharp(진보적 JPEG 지원)
            var page = imagePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? PngCodec.Decode(imagePath) : ImageIO.Decode(imagePath);

            var regions = active.RecognizeAsync(page).GetAwaiter().GetResult();
            sw.Stop();

            // dev: prob맵도 저장 — 탐지 recall 진단용. 3rd 인자 "prob", 4th="center"면 중앙 타일
            if (annotatedOut == "prob")
            {
                int tx = 0, ty = 0;
                var pt = Environment.GetEnvironmentVariable("PROB_TILE"); // "x,y" | "center"
                if (pt == "center")
                { tx = Math.Min(768, page.Width - 768); ty = Math.Min(768, page.Height - 768); }
                else if (pt != null)
                {
                    var parts = pt.Split(',');
                    if (parts.Length == 2 && int.TryParse(parts[0], out tx) && int.TryParse(parts[1], out ty)) { }
                    else { tx = ty = 0; }
                }
                var tile = page.Crop(new BoundingBox(tx, ty, Math.Min(tx + 768, page.Width), Math.Min(ty + 768, page.Height)));
                Console.WriteLine($"prob tile=({tx},{ty})");
                var prob = engine.DebugProbMap(tile); // det(prob)는 ppocr 공용
                PngCodec.Encode(prob, Path.ChangeExtension(imagePath, ".prob.png"));
                Console.WriteLine($"prob -> {Path.ChangeExtension(imagePath, ".prob.png")}");
                return 0;
            }

            Console.WriteLine($"regions={regions.Count} in {sw.Elapsed.TotalSeconds:0.0}s");
            var report = new System.Text.StringBuilder();
            int i = 0;
            foreach (var r in regions)
            {
                var line = $"[{i++}] conf={r.Confidence:0.00} {(r.Vertical ? '세' : '횡')} " +
                          $"box=({r.Box.Left},{r.Box.Top})~({r.Box.Right},{r.Box.Bottom}) {r.Text}";
                Console.WriteLine(line);
                report.AppendLine(line);

                DrawRect(page, r.Box, g: 255); // 초록 outlines
            }

            annotatedOut ??= Path.ChangeExtension(imagePath, ".ocr.png");
            PngCodec.Encode(page, annotatedOut);
            File.WriteAllText(Path.ChangeExtension(imagePath, ".ocr.txt"), report.ToString());
            Console.WriteLine($"annotated -> {annotatedOut}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"OCRTEST FAILED: {ex}");
            return 1;
        }
    }

    private static void DrawRect(ImageData img, BoundingBox b, byte b2 = 0, byte g = 255, byte r = 0, int t = 2)
    {
        img.FillRect(new BoundingBox(b.Left, b.Top, b.Right, b.Top + t), b2, g, r);
        img.FillRect(new BoundingBox(b.Left, b.Bottom - t, b.Right, b.Bottom), b2, g, r);
        img.FillRect(new BoundingBox(b.Left, b.Top, b.Left + t, b.Bottom), b2, g, r);
        img.FillRect(new BoundingBox(b.Right - t, b.Top, b.Right, b.Bottom), b2, g, r);
    }
}
