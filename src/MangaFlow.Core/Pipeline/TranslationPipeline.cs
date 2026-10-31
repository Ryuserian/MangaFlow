using MangaFlow.Core.Imaging;
using MangaFlow.Core.Interfaces;

namespace MangaFlow.Core.Pipeline;

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

    /// <summary>설정 기반 연속 실행(2026-10-28 사용자 결정): true면 스테이지 경계 unload을 건너뛴다 —
    /// 배치 전체 재조판 루프에서 번역 GGUF(18GB)를 매 장 cold 재적재하지 않도록.
    /// 대기열 종료 후 ReleaseHeldEngines()가 한 번에 내린다.</summary>
    public bool HoldAcrossPages { get; set; }
    /// <summary>GUI Review 단계: true면 detect 후 OCR unload을 건너뛴다 — 행별 🔄 재인식이
    /// 매번 cold(17GB GGUF 재적재 ≈80초) 되는 것을 막는다(사용자 2026-10-26 "인식 잘 안됨").
    /// Apply(번역 직전)에서 false로 되돌려 순차 상재를 복귀시킨다.</summary>
    public bool HoldOcrLoaded { get; set; }

    /// <summary>스탯 직 후 Unloadable 해제(2026-10-25 사용자 요청: VRAM은 순차 상재). 3B OCR·17GB VL
    /// ·32B 번역을 동시에 올려두지 않는다. 다음 사용 시 엔진의 lazy 재로드로 복귀.
    /// HoldAcrossPages=true(배치 연속 실행)면 suppressing — 설정 기반 재조판 루프 보호.</summary>
    private void MaybeUnload(object engine, string stage)
    {
        if (KeepEnginesLoaded || HoldAcrossPages) return;
        if (engine is IUnloadable u && u.IsLoaded)
        {
            u.Unload();
            System.Diagnostics.Debug.WriteLine($"[pipeline] {stage}: {engine.GetType().Name} VRAM unload");
        }
    }

    /// <summary>HoldAcrossPages와 무관하게 항상 내리는 unload(멱등) — OCR은 detect 전용이라
    /// apply 단계에서는 설정 기반으로 불필요. 번역 GGUF와 동시 상재(17+18GB)를 막는 순차 상재 규칙.</summary>
    private void UnloadNow(object engine, string stage)
    {
        if (engine is IUnloadable u && u.IsLoaded)
        {
            u.Unload();
            System.Diagnostics.Debug.WriteLine($"[pipeline] {stage}: {engine.GetType().Name} VRAM unload (forced)");
        }
    }

    /// <summary>배치 설정 실행 종료 후 호출 — hold를 풀고 이 파이프라인이 점유한 큰 엔진을 내린다.</summary>
    public void ReleaseHeldEngines()
    {
        HoldAcrossPages = false;
        HoldOcrLoaded = false;
        MaybeUnload(_ocr, "batch-end");
        MaybeUnload(_inpaint, "batch-end");
        MaybeUnload(_translate, "batch-end");
    }

    /// <summary>싱글 스텝1: 일본어 위치 detect → TextRegion(instance) 목록.</summary>
    public async Task<IReadOnlyList<TextRegion>> DetectAsync(ImageData page, CancellationToken ct = default)
    {
        Report(PipelineStage.Detect, 0.1, $"{_ocr.Name}: detecting text regions…");
        var regions = await _ocr.RecognizeAsync(page, ct);
        if (!HoldOcrLoaded) MaybeUnload(_ocr, "detect"); // GUI Review 중엔 hangat—🔄 재인식 warm 유지
        return regions;
    }

    /// <summary>싱글 스텝2: 편집된 instance 기준으로 fill+translate+render 한 번에.
    /// 통합 설정 기반 실행(2026-10-28): regions에 Include=false(보존)가 섞여 있어도 된다 —
    /// 내부 필터로 ☑ 박스만 제거·번역·재조판하고, 나머지는 원본 그대로 둔다.</summary>
    public async Task<PipelineResult> ApplyAsync(ImageData page, IReadOnlyList<TextRegion> regions,
        string sourceLang = "ja", string targetLang = "ko", CancellationToken ct = default)
    {
        var work = regions.Where(r => r.Include).ToList();
        if (work.Count == 0)
        {
            // ☑ 0개 = '전면 보존' 의도 — 원본 클론을 결과로(엔진 전부 hot 패스, VRAM 소모 없음)
            Report(PipelineStage.Done, 1.0, "nothing selected — original preserved");
            var keep = page.Clone();
            return new PipelineResult(keep, keep.Clone(), regions);
        }

        Report(PipelineStage.Inpaint, 0.3, $"{_inpaint.Name}: content-filling {work.Count} regions…");
        var clean = await _inpaint.FillAsync(page, work.Select(r => r.InpaintBox()).ToList(), ct);
        Report(PipelineStage.Inpaint, 0.5, "content filled");
        MaybeUnload(_inpaint, "inpaint"); // LaMa ONNX도 내려 VRAM 여유 확보

        Report(PipelineStage.Translate, 0.6, $"{_translate.Name}: translating {work.Count} lines…");
        UnloadNow(_ocr, "apply-before-translate"); // Review 종료=OCR GGUF 해방 → 번역 GGUF 순차 상재(hold와 무관)
        HoldOcrLoaded = false;
        var texts = work.Select(r => r.Text).ToList();
        var tones = work.Select(r => r.Tone).ToList();
        var translated = await _translate.TranslateAsync(texts, sourceLang, targetLang, tones, ct);
        // 전각→반각(사용자 피드백): 편집 UI 텍스트·렌더가 일관되게 반각
        for (int i = 0; i < work.Count; i++) work[i].TranslatedText = TextNormalizer.ToHalfWidth(translated[i]);
        MaybeUnload(_translate, "translate"); // 18GB 번역 GGUF 해제 — 렌더는 CPU(Skia)

        Report(PipelineStage.Render, 0.8, $"{_renderer.Name}: typesetting into same boxes…");
        var output = clean.Clone();
        _renderer.Render(output, work);

        Report(PipelineStage.Done, 1.0, "done");
        return new PipelineResult(clean, output, work);
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
