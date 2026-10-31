using MangaFlow.Core.Engines;
using MangaFlow.Core.Imaging;
using MangaFlow.Core.Pipeline;

namespace MangaFlow.App.Smoke;

/// <summary>
/// 모델 없이 4단계 파이프라인이 실제로 도는지 검증:
/// demo OCR → flat fill → passthrough 번역 → block 렌더 → PNG 저장.
/// GUI를 못 띄우는 환경(WSL CI 등)의 회귀망.
/// </summary>
public static class SmokeTest
{
    public static int Run(string outPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

            var page = new ImageData(800, 1200);
            page.FillRect(new BoundingBox(0, 0, 800, 1200), 210, 210, 210);

            var pipeline = new TranslationPipeline(
                new DemoTextOcrEngine(), new FlatFillInpaintEngine(),
                new PassthroughTranslationEngine(), new BlockTextRenderer());

            pipeline.Progress += p => Console.WriteLine($"  [{p.Stage,-8}] {p.Percent,5:P0} {p.Message}");

            var result = pipeline.RunAsync(page).GetAwaiter().GetResult();
            PngCodec.Encode(result.RenderedPage, outPath);

            Console.WriteLine($"SMOKE OK: {result.Regions.Count} regions -> {outPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SMOKE FAILED: {ex}");
            return 1;
        }
    }
}
