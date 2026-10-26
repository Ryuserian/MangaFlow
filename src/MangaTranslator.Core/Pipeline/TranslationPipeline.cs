using MangaTranslator.Core.Imaging;
using MangaTranslator.Core.Interfaces;

namespace MangaTranslator.Core.Pipeline;

public enum PipelineStage { Load, Detect, Review, Inpaint, Translate, Render, Done }

public sealed record PipelineProgress(PipelineStage Stage, double Percent, string Message);

public sealed record PipelineResult(
    ImageData CleanedPage,   // ③ 텍스트 제거된 이미지
    ImageData RenderedPage,  // ⑤ 한국어 재조판된 이미지
    IReadOnlyList<TextRegion> Regions);

/// <summary>
/// 4단계 파이프라인. Detect/Apply가 분리되어 있어
///  - 싱글 모드: Detect → 사용자 인스턴스 편집 → Apply(fill+translate+render를 1회로)
///  - 배치 모드: RunAsync( Detect→Apply 연속 )를 파일마다 반복
/// 모두 이 클래스 하나로 충당한다. 엔진은 인터페이스 주입, 설정으로 교체.
/// v4: 엔진 소유권 없음 — EngineContext가 프로세스 수명 동안 캐시·폐기.
/// 파이프라인은 얇은 배선이라 자주 만들어도 싸다(ONNX 세션 재생성 없음).
/// </summary>
public sealed class TranslationPipeline
{
    private readonly IOcrEngine _ocr;
    private readonly IInpaintEngine _inpaint;
    private readonly ITranslationEngine _translate;
    private readonly ITextRenderer _renderer;

    public event Action<PipelineProgress>? Progress;

    public TranslationPipeline(IOcrEngine ocr, IInpaintEngine inpaint,
        ITranslationEngine translate, ITextRenderer renderer)
    {
        _ocr = ocr; _inpaint = inpaint; _translate = translate; _renderer = renderer;
    }

    /// <summary>진행 중 Unload 억누름: MEM_KEEP=1면 keep. 기본 off = unload함(32GB GPU 안전).</summary>
    private static bool KeepEnginesLoaded => Environment.GetEnvironmentVariable("MEM_KEEP") == "1";
    /// <summary>GUI Review 단계: true면 detect 후 OCR unload을 건너뛴다 — 행별 🔄 재인식이
    /// 매번 cold(17GB GGUF 재적재 ≈80초) 되는 것을 막는다(사용자 2026-10-26 "인식 잘 안됨").
    /// Apply(번역 직전)에서 false로 되돌려 순차 상재를 복귀시킨다.</summary>
    public bool HoldOcrLoaded { get; set; }

    /// <summary>스탯 직 후 Unloadable 해제(2026-10-25 사용자 요청: VRAM은 순차 상재). 3B OCR·17GB VL
    /// ·32B 번역을 동시에 올려두지 않는다. 다음 사용 시 엔진의 lazy 재로드로 복귀.</summary>
    private static void MaybeUnload(object engine, string stage)
    {
        if (KeepEnginesLoaded) return;
        if (engine is IUnloadable u && u.IsLoaded)
        {
            u.Unload();
            System.Diagnostics.Debug.WriteLine($"[pipeline] {stage}: {engine.GetType().Name} VRAM unload");
        }
    }

    /// <summary>싱글 스텝1: 일본어 위치 detect → TextRegion(instance) 목록.</summary>
    public async Task<IReadOnlyList<TextRegion>> DetectAsync(ImageData page, CancellationToken ct = default)
    {
        Report(PipelineStage.Detect, 0.1, $"{_ocr.Name}: detecting text regions…");
        var regions = await _ocr.RecognizeAsync(page, ct);
        if (!HoldOcrLoaded) MaybeUnload(_ocr, "detect"); // GUI Review 중엔 hangat—🔄 재인식 warm 유지
        return regions;
    }

    /// <summary>싱글 스텝2: 편집된 instance 기준으로 fill+translate+render 한 번에.</summary>
    public async Task<PipelineResult> ApplyAsync(ImageData page, IReadOnlyList<TextRegion> regions,
        string sourceLang = "ja", string targetLang = "ko", CancellationToken ct = default)
    {
        Report(PipelineStage.Inpaint, 0.3, $"{_inpaint.Name}: content-filling {regions.Count} regions…");
        var clean = await _inpaint.FillAsync(page, regions.Select(r => r.InpaintBox()).ToList(), ct);
        Report(PipelineStage.Inpaint, 0.5, "content filled");
        MaybeUnload(_inpaint, "inpaint"); // LaMa ONNX도 내려 VRAM 여유 확보

        Report(PipelineStage.Translate, 0.6, $"{_translate.Name}: translating {regions.Count} lines…");
        MaybeUnload(_ocr, "apply-before-translate"); // Review 종료=OCR GGUF 해방 → 번역 GGUF 순차 상재
        HoldOcrLoaded = false;
        var texts = regions.Select(r => r.Text).ToList();
        var translated = await _translate.TranslateAsync(texts, sourceLang, targetLang, ct);
        // 전각→반각(사용자 피드백): 편집 UI 텍스트·렌더가 일관되게 반각
        for (int i = 0; i < regions.Count; i++) regions[i].TranslatedText = TextNormalizer.ToHalfWidth(translated[i]);
        MaybeUnload(_translate, "translate"); // 18GB 번역 GGUF 해제 — 렌더는 CPU(Skia)

        Report(PipelineStage.Render, 0.8, $"{_renderer.Name}: typesetting into same boxes…");
        var output = clean.Clone();
        _renderer.Render(output, regions);

        Report(PipelineStage.Done, 1.0, "done");
        return new PipelineResult(clean, output, regions);
    }

    /// <summary>배치/원터치: 전체 자동 연속 실행.</summary>
    public async Task<PipelineResult> RunAsync(ImageData page,
        string sourceLang = "ja", string targetLang = "ko", CancellationToken ct = default)
    {
        Report(PipelineStage.Load, 0.0, $"{page.Width}x{page.Height}");
        var regions = await DetectAsync(page, ct);
        Report(PipelineStage.Detect, 0.2, $"{regions.Count} regions");
        return await ApplyAsync(page, regions, sourceLang, targetLang, ct);
    }

    private void Report(PipelineStage s, double p, string m) => Progress?.Invoke(new(s, p, m));
}
