using MangaFlow.App.Engines;
using MangaFlow.Core;
using MangaFlow.Core.Interfaces;
using MangaFlow.Core.Pipeline;
using MangaFlow.ModelManager;
using MangaFlow.Plugins.Inpaint.Lama;
using MangaFlow.Plugins.Ocr.VlmOcr;
using MangaFlow.Plugins.Render.Skia;
using MangaFlow.Plugins.Translate.QwenGguf;
using MangaFlow.Plugins.Translate.WebApi;

namespace MangaFlow.App.Services;

/// <summary>
/// 엔진 교체 배선의 중심. 여기서 "stub"을 "ppocr/lama/qwen" 등으로 키만 바꿔
/// 후보를 붙인다(후보 교체 = Catalog 키 + 팩토리 추가).
/// </summary>
public sealed class EngineContext : IDisposable
{
    // ---- 엔진 스택. 번역만 env TRANSLATE_ENGINE로 교체 가능(qwen_gguf 기본 | web_google | web_deepl | web_openai).
    // GUI 콤보박스도 같은 키를 사용 — 단일 진실원 유지.
    public const string OcrKey = "vlm_ocr";             // PP-OCR det + VLM rec(Qwen3-VL) — 단일 rec, 폴백 없음
    public const string InpaintKey = "lama";
    public const string RenderKey = "skia";
    public static string TranslateKey { get; set; } =
        Environment.GetEnvironmentVariable("TRANSLATE_ENGINE") is { Length: > 0 } t ? t : "qwen_gguf";

    public EngineRegistry Registry { get; } = new();
    public ModelCatalogManager Models { get; }
    public string OutputDir { get; }

    // ---- 엔진 캐시: ONNX 세션·GGUF·폰트는 생성이 비싸다 → 키별 1회 생성 후 재사용.
    // v4 파이프라인은 얇은 배선(엔진 소유권 없음)이라 커맨드마다 만들어도 싸다. 수명=앱. ----
    private readonly Dictionary<string, IOcrEngine> _ocrCache = new();
    private readonly Dictionary<string, IInpaintEngine> _inpaintCache = new();
    private readonly Dictionary<string, ITranslationEngine> _translateCache = new();
    private readonly Dictionary<string, ITextRenderer> _renderCache = new();
    private readonly object _cacheLock = new();

    // ---- PP-OCR = det 전용(det-only) — rec는 Qwen3-VL(Priority)+manga-ocr(폴백)이 담당 ----
    public static string PpOcrDir => Path.Combine(
        ModelCatalogManager.DefaultRootDir, "ocr", "ch_PP-OCRv4");
    public static string PpOcrDetPath => Path.Combine(PpOcrDir, "ch_PP-OCRv4_det_infer.onnx");
    // env PP_REC_MODEL=japan → 일본어 rec로 스왑(만화 품질 확인용; 엔진 교체 아키텍처의 실제 위력)
    public static string PpOcrRecPath
    {
        get
        {
            var jp = Path.Combine(PpOcrDir, "japan_PP-OCRv4_rec_infer.onnx");
            return Environment.GetEnvironmentVariable("PP_REC_MODEL") == "japan" && File.Exists(jp)
                ? jp : Path.Combine(PpOcrDir, "ch_PP-OCRv4_rec_infer.onnx");
        }
    }
    // rec↔dict 세트 스왑(dev CLI 전용 — 파이프라인은 det만 사용, 파일 없으면 det-only로 정상 동작).
    public static string PpOcrDictPath => Path.GetFileName(PpOcrRecPath).StartsWith("japan")
        ? Path.Combine(PpOcrDir, "japan_dict.txt")
        : File.Exists(Path.Combine(PpOcrDir, "ppocr_keys_v1.txt"))
            ? Path.Combine(PpOcrDir, "ppocr_keys_v1.txt")
            : Path.Combine(PpOcrDir, "ppocrv4_rec_dict.txt");
    /// <summary>필수 게이트: det만 있으면 OCR 엔진 구성 가능(rec는 VLM/manga).</summary>
    public static bool HasPpOcr => File.Exists(PpOcrDetPath);

    // ---- rec VLM = Qwen3-VL-30B-A3B (Apache-2.0) — sample/before 실측 coverage 100%·7/8 정독.
    // env OCR_VLM_GGUF/OCR_VLM_MMPROJ 로 다른 mtmd GGUF 스왑 가능(실험용).
    public static string VlmDir => Path.Combine(ModelCatalogManager.DefaultRootDir,
        "ocr", "Qwen3-VL-30B-A3B-Instruct-GGUF");
    public static string VlmGgufPath => Environment.GetEnvironmentVariable("OCR_VLM_GGUF")
        ?? Path.Combine(VlmDir, "Qwen3VL-30B-A3B-Instruct-Q4_K_M.gguf");
    public static string VlmMmprojPath => Environment.GetEnvironmentVariable("OCR_VLM_MMPROJ")
        ?? Path.Combine(VlmDir, "mmproj-Qwen3VL-30B-A3B-Instruct-Q8_0.gguf");
    public static bool HasVlmRec => File.Exists(VlmGgufPath) && File.Exists(VlmMmprojPath);
    /// <summary>선택된 rec VLM 이름(로그·UI 메시지).</summary>
    public const string SelectedVlmName = "qwen3-vl-30b-a3b";

    /// <summary>rec VLM 생성(Qwen3-VL mtmd). 미보유 시 null → VlmOcrEngine 이 명시 에러.</summary>
    public static Core.Interfaces.IVlmRecEngine? CreateVlmRec() => HasVlmRec
        ? new MangaFlow.Plugins.Ocr.VlmQwen.QwenVlRecEngine
        {
            GgufPath = VlmGgufPath,
            MmprojPath = VlmMmprojPath,
        }
        : null;

    // ---- 번역/폰트 경로 ----
    public static string ModelsDir => ModelCatalogManager.DefaultRootDir;
    /// <summary>번역 = Qwen3 GGUF. 32B q4_k_m 우선(보이면), 없으면 14B 폴백(기본 다운로드 — 30GB GPU 안전).
    /// 카탈로그 Group "qwen3" 중 하나만 받으면 충족. mac 128GB 등은 --getmodel translate.qwen3_32b_gguf.</summary>
    public static string QwenGgufPath
    {
        get
        {
            var big = Path.Combine(ModelsDir, "translate", "Qwen_Qwen3-32B-GGUF", "Qwen3-32B-Q4_K_M.gguf");
            if (File.Exists(big)) return big;
            return Path.Combine(ModelsDir, "translate", "Qwen_Qwen3-14B-GGUF", "Qwen3-14B-Q4_K_M.gguf");
        }
    }
    public static string NotoRegularPath => Path.Combine(ModelsDir,
        "font", "googlefonts_noto-cjk", "Sans", "SubsetOTF", "KR", "NotoSansKR-Regular.otf");
    public static string NotoBoldPath => Path.Combine(ModelsDir,
        "font", "googlefonts_noto-cjk", "Sans", "SubsetOTF", "KR", "NotoSansKR-Bold.otf");

    private EngineContext(ModelCatalogManager models)
    {
        Models = models;
        OutputDir = Path.Combine(Environment.CurrentDirectory, "out");
        Directory.CreateDirectory(OutputDir);

        // ② OCR — 단일 엔진: PP-OCR det + Qwen3-VL rec. rec 폴백 없음(manga-ocr 삭제 2026-10-26,
        //    실측 garbage). VLM GGUF 미보유 시 Recognition 시점에 명확한 에러.
        Registry.RegisterOcr(OcrKey, () => HasPpOcr
            ? new VlmOcrEngine(PpOcrDetPath, PpOcrRecPath, PpOcrDictPath)
            {
                VlmRec = CreateVlmRec(),
                OwnsVlm = true,
            }
            : (IOcrEngine)new PpOcrMissingEngine());
        // ③ Inpaint
        Registry.RegisterInpaint(InpaintKey, () => new LamaInpaintEngine
        {
            ModelPath = TryResolve("inpaint.lama_big"),
        });
        // ④ 번역 — 로컬 Qwen GGUF 기본 + 외부 API 대안(속도/품질 트레이드오프, TRANSLATE_ENGINE로 선택)
        Registry.RegisterTranslation("qwen_gguf", () => new QwenGgufTranslateEngine
        {
            GgufPath = QwenGgufPath,
        });
        Registry.RegisterTranslation("web_google", () => new WebTranslateEngine { Provider = "google" });
        Registry.RegisterTranslation("web_deepl", () => new WebTranslateEngine { Provider = "deepl" });
        Registry.RegisterTranslation("web_openai", () => new WebTranslateEngine { Provider = "openai" });
        // ⑤ 재조판
        Registry.RegisterRenderer(RenderKey, () => new SkiaFontRenderer
        {
            RegularFontPath = NotoRegularPath,
            BoldFontPath = NotoBoldPath,
        });
    }

    public static EngineContext CreateDefault() => new(new ModelCatalogManager());

    /// <summary>엔진 키는 고정 상수(선택지 제거) — 스플래시 프리로드/CLI의 단일 진실원.</summary>
    public (string Ocr, string Inpaint, string Translate, string Render) DefaultKeys() =>
        (OcrKey, InpaintKey, TranslateKey, RenderKey);

    public string? TryResolve(string catalogKey)
    {
        var spec = Models.Find(catalogKey);
        if (spec == null) return null;
        var p = Models.PathFor(spec);
        return File.Exists(p) ? p : null;
    }

    // ---- 캐시 게터: 최초 호출 시 무거운 생성(ONNX 로드), 이후 재사용 ----
    public IOcrEngine GetOcr(string key)
    {
        lock (_cacheLock)
        {
            if (!_ocrCache.TryGetValue(key, out var e))
                _ocrCache[key] = e = Registry.CreateOcr(key);
            return e;
        }
    }
    public IInpaintEngine GetInpaint(string key)
    {
        lock (_cacheLock)
        {
            if (!_inpaintCache.TryGetValue(key, out var e))
                _inpaintCache[key] = e = Registry.CreateInpaint(key);
            return e;
        }
    }
    public ITranslationEngine GetTranslate(string key)
    {
        lock (_cacheLock)
        {
            if (!_translateCache.TryGetValue(key, out var e))
                _translateCache[key] = e = Registry.CreateTranslation(key);
            return e;
        }
    }
    public ITextRenderer GetRenderer(string key)
    {
        lock (_cacheLock)
        {
            if (!_renderCache.TryGetValue(key, out var e))
                _renderCache[key] = e = Registry.CreateRenderer(key);
            return e;
        }
    }

    public TranslationPipeline BuildPipeline(
        string ocrKey, string inpaintKey, string translateKey, string rendererKey) =>
        new(GetOcr(ocrKey), GetInpaint(inpaintKey),
            GetTranslate(translateKey), GetRenderer(rendererKey));

    /// <summary>고정 스택 파이프라인 — 앱의 유일한 실행 경로.</summary>
    public TranslationPipeline BuildDefaultPipeline() =>
        BuildPipeline(OcrKey, InpaintKey, TranslateKey, RenderKey);

    /// <summary>
    /// 스플래시 프리로드 v5 (2026-10-26 사용자 결정): **GGUF eager 로드 생략** — 부팅 빠르게.
    ///   · 여기서는 ONNX 경량 세션(det·manga ViT/BERT·LaMa)과 폰트만 로드(수 초).
    ///   · 큰 GGUF(rec Qwen3-VL 17GiB / 번역 Qwen3 18.4GiB)는 **첫 사용 시점 lazy 로드**
    ///     (첫 인식·번역에서 분 단위 대기 수용. 스테이지 경계 unload는 TranslationPipeline 유지).
    ///   · 파일 존재/무결성 감사는 이 메서드 이전의 다운로드 단계(SplashViewModel 감사)에서 이미 끝남.
    /// 엔진별 실패는 알리지 않고 계속(사용 시점에 명확한 에러) — 앱은 어떤 상태든 열리게.
    /// MEM_KEEP=1: 예전 동작(부팅 시 GGUF까지 확정 로드)로 복귀.
    /// </summary>
    public async Task PreloadAsync((string Ocr, string Inpaint, string Translate, string Render) keys,
        IProgress<(double Percent, string Message)> prog, CancellationToken ct = default)
    {
        var eager = Environment.GetEnvironmentVariable("MEM_KEEP") == "1";
        ct.ThrowIfCancellationRequested();
        prog.Report((0.05, "텍스트 디텍터·경량 인식 세션 로드 중…"));
        try
        {
            var ocr = await Task.Run(() => GetOcr(keys.Ocr), ct); // ONNX det+ViT: 수 초
            if (eager && ocr is ILoadableEngine le) await le.EnsureLoadedAsync(ct); // GGUF 워밍(옵트인)
        }
        catch (Exception ex) { prog.Report((0.30, $"⚠ OCR 로드 실패: {ex.Message}")); }
        prog.Report((0.35, $"{keys.Inpaint}: content-fill 세션 로드 중…"));
        try
        {
            var inp = await Task.Run(() => GetInpaint(keys.Inpaint), ct);
            if (inp is ILoadableEngine le) await le.EnsureLoadedAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { prog.Report((0.55, $"⚠ LaMa 로드 실패: {ex.Message}")); }
        ct.ThrowIfCancellationRequested();
        prog.Report((0.60, eager ? $"{keys.Translate}: 번역 모델 로드 중…" : "번역 엔진 준비…"));
        try
        {
            var tr = await Task.Run(() => GetTranslate(keys.Translate), ct); // 생성만 — GGUF 미적재
            if (eager && tr is ILoadableEngine le) await le.EnsureLoadedAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { prog.Report((0.85, $"⚠ 번역 엔진 준비 실패: {ex.Message}")); }
        ct.ThrowIfCancellationRequested();
        prog.Report((0.85, "한글 폰트 로드 중…"));
        try
        {
            var rd = await Task.Run(() => GetRenderer(keys.Render), ct);
            if (rd is ILoadableEngine le) await le.EnsureLoadedAsync(ct);
        }
        catch (Exception ex) { prog.Report((0.95, $"⚠ 폰트 로드 실패: {ex.Message}")); }
        prog.Report((1.0, eager ? "준비 완료" : "준비 완료 — 첫 인식·번역 시 GPU 모델을 올립니다(수 분 소요될 수 있음)"));
    }

    public void Dispose()
    {
        lock (_cacheLock)
        {
            foreach (var e in _ocrCache.Values) (e as IDisposable)?.Dispose();
            foreach (var e in _inpaintCache.Values) (e as IDisposable)?.Dispose();
            foreach (var e in _translateCache.Values) (e as IDisposable)?.Dispose();
            foreach (var e in _renderCache.Values) (e as IDisposable)?.Dispose();
            _ocrCache.Clear(); _inpaintCache.Clear(); _translateCache.Clear(); _renderCache.Clear();
        }
    }
}
