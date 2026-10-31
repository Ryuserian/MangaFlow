using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LLama;
using LLama.Batched;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using MangaFlow.Core.Imaging;
using MangaFlow.Core.Interfaces;

namespace MangaFlow.Plugins.Ocr.VlmQwen;

/// <summary>
/// rec VLM 백엔드 — 기본 모델 **Qwen3-VL-30B-A3B**(Apache-2.0) / 구버전 dots.ocr(GGUF, 폐기 2026-10-26).
/// 얇은 손글씨·다중 열 세로말풍선에서 manga-ocr이 빈/저신뢰 출력을 내는 박스를 판독한다.
/// mtmd 범용 로더라 GgufPath/MmprojPath만 바꾸면 어떤 mtmd 호환 GGUF든 동작.
///
/// 구현 v3: LLamaSharp 0.30 in-process MTMD — llama.cpp 네이티브 DLL(llama/mtmd)을
/// 같은 프로세스에 직접 로드한다. llama-server subprocess·포트(:8124)·health-check 제거.
///   1) 모델은 최초 1회 로드 후 재사용(App 기동 시 eager load; 실패 시 rec 시점에 1회 재시도)
///   2) 판독: LoadMedia(png bytes) → Conversation.Prompt(marker+text, embeds) → sample loop
///   3) temp0 + repeat-penalty 1.1 + upscale — '今' 무한반복 붕괴 실측 대응(§4) 유지
/// 샘플링은 GreedySamplingPipeline이 아닌 DefaultSamplingPipeline(temp=0) — greedy 코드 분기 회피.
/// </summary>
public sealed class QwenVlRecEngine : IVlmRecEngine, ILoadableEngine, IUnloadable, IDisposable
{
    public string Name => "vlm_qwen3vl";
    public string? GgufPath { get; set; }
    public string? MmprojPath { get; set; }
    /// <summary>-1 = auto: GPU 우선 — win·linux(CUDA)=99, macOS(Metal)=99, GPU 부재 시 ggml 자동 CPU. env VLM_NGL.</summary>
    public int NGpuLayers { get; set; } = -1;
    public int CtxSize { get; set; } = 4096;
    /// <summary>crop 최장변 최소 픽셀. 미만이면 int 배율 bilinear upscale. env VLM_UPSCALE=minside (0=off).</summary>
    public int UpscaleMinSide { get; set; } = 280;

    // r02 crop 실측 통과 프롬프트 — 세로 열 우→좌 순서 지시 + 출력 억제(幻觉 방지)
    private const string Prompt =
        "Recognize all Japanese text in this manga image. Read vertical text columns right-to-left, top-to-bottom. Output only the recognized text.";
    private const string RetryPrompt = "OCR the Japanese text.";
    private const int MaxNewTokens = 256;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _loadLock = new();
    private Task? _loadTask;

    private LLamaWeights? _weights;
    private MtmdWeights? _mtmd;
    private BatchedExecutor? _executor;
    private DefaultSamplingPipeline? _pipeline;
    private string _marker = "<__media__>";

    public QwenVlRecEngine()
    {
        var ngl = Environment.GetEnvironmentVariable("VLM_NGL");
        if (int.TryParse(ngl, out var n)) NGpuLayers = n;
        var up = Environment.GetEnvironmentVariable("VLM_UPSCALE");
        if (int.TryParse(up, out var uu)) UpscaleMinSide = Math.Clamp(uu, 0, 2048); // 0=off
    }

    private int ResolveNgl() => NGpuLayers >= 0
        ? NGpuLayers
        : (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() ? 99 : 0);

    /// <summary>앱 기동 시 1회 호출 권장(최초 rec까지 ~분 단위 대기 제거). 스레드 안전·멱등.</summary>
    public Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        lock (_loadLock)
        {
            if (_loadTask is { IsCompleted: true } && _executor != null) return Task.CompletedTask;
            if (_loadTask is { IsCompleted: false }) return _loadTask;
            return _loadTask = Task.Run(() => LoadAsync(CancellationToken.None), CancellationToken.None);
        }
    }

    /// <summary>VRAM 순차 상재(2026-10-25): OCR 스테이지 종료 후 번역 GGUF를올리기 전에
    /// 3B(Q8≈3GB) 또는 Qwen3-VL(Q4≈17GB) weights를 내려준다. 다음 rec 시 lazy 재로드.</summary>
    public bool IsLoaded { get { lock (_loadLock) return _executor != null; } }

    public void Unload()
    {
        lock (_loadLock)
        {
            if (_executor == null && _mtmd == null && _weights == null) return;
            try { _executor?.Dispose(); } catch { }
            try { _mtmd?.Dispose(); } catch { }
            try { _weights?.Dispose(); } catch { }
            _executor = null; _mtmd = null; _weights = null; _loadTask = null;
            System.GC.Collect();   // 네이티브 메모리 즉시 회수 유도(llama_free)
            System.GC.WaitForPendingFinalizers();
        }
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(GgufPath) || !File.Exists(GgufPath))
            throw new FileNotFoundException($"rec VLM GGUF 없음: {GgufPath}");
        if (string.IsNullOrEmpty(MmprojPath) || !File.Exists(MmprojPath))
            throw new FileNotFoundException($"mmproj 없음: {MmprojPath}");

        var ngl = ResolveNgl();
        // ModelParams가 IContextParams를 겸함 — executor와 동일 객체 재사용(0.30 API)
        var mp = new ModelParams(GgufPath)
        {
            ContextSize = (uint)CtxSize,
            GpuLayerCount = ngl, // Int32 (uint 아님)
            FlashAttention = ngl > 0, // Blackwell FA — CPU 빌드(FA 미지원 백엔드)에서는 off
            // DefragThreshold: 0.30 deprecated(기본 -1=off) — 설정 생략
        };
        _weights = await LLamaWeights.LoadFromFileAsync(mp, ct);

        var mtmdParams = new MtmdContextParams
        {
            UseGpu = ngl > 0,        // CPU 빌드면 vision tower도 CPU — 이미지 없는 CPU 전용 환경에서 .so 부하 회피
            PrintTimings = false,
        };
        _mtmd = await MtmdWeights.LoadFromFileAsync(MmprojPath, _weights, mtmdParams, ct);

        _executor = new BatchedExecutor(_weights, mp, _mtmd);

        _pipeline = new DefaultSamplingPipeline { Temperature = 0f, RepeatPenalty = 1.1f };
        _marker = NativeApi.MtmdDefaultMarker() ?? "<__media__>"; // ← mtmd default marker('<__media__>') 실사용 문자열
    }

    // ---------- rec ----------
    public async Task<string> RecognizeCropAsync(ImageData crop, CancellationToken ct = default)
    {
        if (crop.Width < 8 || crop.Height < 8) return "";
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var input = UpscaleIfNeeded(crop);
            var b64 = PngCodec.EncodeBytes(input);

            var text = await RecognizeOnceAsync(b64, Prompt, ct);
            if (!HasCjk(text)) // VLM 빈 생성(00015 r01 실측) → 최소 지시문 1회 재시도
                text = await RecognizeOnceAsync(b64, RetryPrompt, ct);
            return text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"[vlm_qwen3vl] rec 실패 → 빈 문자열(호출측 박스 유지): {ex.Message}");
            return "";
        }
        finally { _gate.Release(); }
    }

    /// <summary>1박스=1대화: media embed 루프外套 → temp0 sample → EOS/상한 중단.</summary>
    private async Task<string> RecognizeOnceAsync(byte[] png, string prompt, CancellationToken ct)
    {
        var executor = _executor ?? throw new InvalidOperationException("executor 미로드");
        var mtmd = _mtmd ?? throw new InvalidOperationException("mtmd 미로드");

        using var embed = mtmd.LoadMedia(png);
        using var conv = executor.Create();
        conv.Prompt($"{_marker}\n{prompt}", new[] { embed }, addBos: true);

        var decoder = new StreamingTokenDecoder(executor.Context); // 0.30: IDisposable 아님 — reset/gc任せ
        var sb = new StringBuilder();
        for (var n = 0; n < MaxNewTokens; n++)
        {
            var res = await executor.Infer(ct);
            if (res != DecodeResult.Ok) break;
            if (!conv.RequiresSampling) continue; // 이미지 chunk eval 구간
            // 0.30.0: Conversation.Sample은 logits(Span<float>) 반환 → pipeline이 index에서 샘플
            var token = _pipeline!.Sample(executor.Context.NativeHandle, conv.GetSampleIndex(0));
            if (token.IsEndOfGeneration(executor.Context.Vocab)) break;
            _pipeline.Accept(token);
            conv.Prompt(token); // 샘플 토큰을 다음 Infer에 급전 — 없으면 같은 토큰 무한반복
            decoder.Add(token);
            sb.Append(decoder.Read());
            if (sb.Length > 768) break; // 폭주 안전망
        }
        return Clean(sb.ToString());
    }

    /// <summary>최장변 &lt; UpscaleMinSide이면 int 배율(2~4) bilinear 확대. VLM vision은 ~30px+ Glyph에서 안정.</summary>
    private ImageData UpscaleIfNeeded(ImageData src)
    {
        int maxSide = Math.Max(src.Width, src.Height);
        if (UpscaleMinSide <= 0 || maxSide >= UpscaleMinSide) return src;
        int sc = Math.Clamp((int)Math.Ceiling((double)UpscaleMinSide / maxSide), 2, 4);
        int nw = src.Width * sc, nh = src.Height * sc;
        var o = new ImageData(nw, nh);
        double inv = 1.0 / sc;
        for (int y = 0; y < nh; y++)
        {
            double sy = Math.Min(src.Height - 1, (y + 0.5) * inv - 0.5); if (sy < 0) sy = 0;
            int y0 = (int)sy, y1 = Math.Min(src.Height - 1, y0 + 1);
            double fy = sy - y0, wy = 1 - fy;
            var r0 = src.Row(y0); var r1 = src.Row(y1);
            var dr = o.Row(y);
            for (int x = 0; x < nw; x++)
            {
                double sx = Math.Min(src.Width - 1, (x + 0.5) * inv - 0.5); if (sx < 0) sx = 0;
                int x0 = (int)sx, x1 = Math.Min(src.Width - 1, x0 + 1);
                double fx = sx - x0, wx = 1 - fx;
                for (int c = 0; c < 4; c++)
                {
                    int o0 = x0 * 4 + c, o1 = x1 * 4 + c;
                    dr[x * 4 + c] = (byte)((r0[o0] * wx + r0[o1] * fx) * wy + (r1[o0] * wx + r1[o1] * fx) * fy);
                }
            }
        }
        return o;
    }

    private static bool HasCjk(string s)
    {
        foreach (var c in s)
            if ((c >= '぀' && c <= 'ヿ') || (c >= '一' && c <= '鿿')) return true;
        return false;
    }

    /// <summary>마크다운 코드펜스·template 잔재·따옴표 제거. 열 구분 newline은 유지.</summary>
    private static string Clean(string s)
    {
        s = s.Trim();
        int cut = s.IndexOf("<|", StringComparison.Ordinal); // template token 누수
        if (cut >= 0) s = s[..cut];
        s = Regex.Replace(s, @"^```[a-z]*\n?", "");
        s = Regex.Replace(s, "\n?```$", "");
        s = Regex.Replace(s, @"(?:^|\n)[ \t]*#+[ \t]*", "\n"); // markdown 헤딩 '#' 누수('セセンセ…' 앞에 붙는 실측)
        s = s.Trim('"', '「', '」', '„', '”', '\'', ' ', '#', '\n');
        return s.Trim();
    }

    public void Dispose()
    {
        try
        {
            _executor?.Dispose();
            _mtmd?.Dispose();
            _weights?.Dispose();
            _pipeline?.Dispose();
        }
        catch { }
        _gate.Dispose();
    }
}
