using System.Diagnostics;
using MangaFlow.App.Services;
using MangaFlow.Core.Imaging;
using MangaFlow.Core.Pipeline;

namespace MangaFlow.App.Smoke;

/// <summary>
/// dev CLI: --pipeline &lt;in.png&gt; [out.png] — ①→⑤ 종단 자동 실행(스텝 편집 없음).
/// 산출: out_default = &lt;in&gt;_ko.png. 중간 산물 &lt;in&gt;.clean.png(제거 전용).
/// </summary>
public static class PipelineCli
{
    public static int Run(string imagePath, string? outPath)
    {
        var ctx = EngineContext.CreateDefault();
        var page = imagePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? PngCodec.Decode(imagePath) : ImageIO.Decode(imagePath);

        // 고정 엔진 스택(선택 스왑 제거) — GUI와 동일 경로
        Console.WriteLine($"engines: ocr={EngineContext.OcrKey} inpaint={EngineContext.InpaintKey} " +
                          $"translate={EngineContext.TranslateKey} render={EngineContext.RenderKey}");

        var pipeline = ctx.BuildDefaultPipeline();
        pipeline.Progress += p => Console.WriteLine($"[{p.Stage} {p.Percent:P0}] {p.Message}");

        PipelineResult res;
        var sw = Stopwatch.StartNew();
        try { res = pipeline.RunAsync(page, "ja", "ko").GetAwaiter().GetResult(); }
        finally { ctx.Dispose(); } // v4: 엔진 캐시된 ctx가 자식(neive 세션) 정리 책임
        sw.Stop();

        PngCodec.Encode(res.CleanedPage, Path.ChangeExtension(imagePath, ".clean.png"));
        outPath ??= Path.ChangeExtension(imagePath, ".ko.png");
        PngCodec.Encode(res.RenderedPage, outPath);

        foreach (var r in res.Regions)
            Console.WriteLine($"  ({r.Box.Left},{r.Box.Top}) {(r.Vertical ? "세" : "횡")} '{r.Text}' → '{r.TranslatedText}'");
        Console.WriteLine($"done {sw.Elapsed.TotalSeconds:0.0}s -> {outPath}");
        return 0;
    }
}
