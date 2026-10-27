using System.Text;
using System.Text.RegularExpressions;
using LLama;
using LLama.Batched;
using LLama.Common;
using LLama.Native; // DecodeResult
using LLama.Sampling;
using MangaTranslator.Core;
using MangaTranslator.Core.Interfaces;
using MangaTranslator.Core.Translate;

namespace MangaTranslator.Plugins.Translate.QwenGguf;

/// <summary>
/// Qwen3 Instruct GGUF 백엔드 — LLamaSharp in-process (v3).
/// jpn→kor 문맥·캐릭터체 번역이 NLLB급 seq2seq보다 월등해 1순위. Apache-2.0.
///
/// v3: llama.cpp 네이티브 DLL(llama/ggml/ggml-cuda)을 같은 프로세스에 직접 로드 —
/// llama-server subprocess·포트(:8123)·health-check 제거.
///   1) 모델 최초 1회 lazy load 후 상주, Dispose에서 네이티브 해제
///   2) 배치: 대사 전체를 번호 매겨 1프롬프트 → 'n. 번역' 파싱. 불일치 시 개별 폴백
///   3) thinking off: 템플릿 kwargs 전달 불가 → 비생성 프리픽스 수동 조합 + 잔재 정규식 이중 안전망
/// API: LLamaSharp 0.30.0 실소스 기준 — Conversation.GetSampleIndex + pipeline.Sample(NativeHandle, idx).
/// </summary>
public sealed class QwenGgufTranslateEngine : ITranslationEngine, ILoadableEngine, IUnloadable, IDisposable
{
    public string Name => "qwen_gguf";
    public int MaxConcurrentSentences => 32;
    public bool RequiresGpu => false;          // CPU 백엔드로도 동작(느리게), Cuda13 백엔드면 GPU
    public string? GgufPath { get; set; }
    /// <summary>-1 = auto: GPU 우선 — Windows·Linux(Cuda13 백엔드)=99, macOS(Metal 백엔드)=99.
    /// GPU 없으면 ggml이 soft-fail 후 자동 CPU. env LLAMA_NGL=0 강제 CPU.</summary>
    public int NGpuLayers { get; set; } = -1;
    public int CtxSize { get; set; } = 8192;

    private const string SystemPrompt =
        "당신은 일본어 만화 전문 한국어 번역가다. 만화 대사를 자연스러운 구어체 한국어로 번역한다. " +
        "입력과 같은 수의 줄, 각 줄 '번호. 한국어'. 설명·인사말·출처 금지. " +
        "일본어 혼용 출력 금지 — 반드시 한국어로. 이미 한국어면 다듬어 출력. " +
        "캐릭터 말투(존댓말/반말)와 뉘앙스·의성어를 살린다. 말풍선 길이 제약이 있으므로 간결하게. " +
        "대사 뒤에 (여) 또는 (남) 마커가 붙으면 그 화자의 성별·성격에 맞는 한국어 말투를 쓴다 — " +
        "(여): 여성스러운 어미(～년/～다/～잖아·부드러운 종결), (남): 남성적인 어미(～다/～는데/～냐·건장한 종결). " +
        "마커가 없거나 (자)면 문맥(私/僕/あた시 등)으로 화자를 추정한다. 마커는 절대 출력하지 않는다.";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _loadLock = new();
    private LLamaWeights? _weights;
    private BatchedExecutor? _executor;
    private DefaultSamplingPipeline? _pipeline;

    private int ResolveNgl()
    {
        if (NGpuLayers >= 0) return NGpuLayers;
        if (int.TryParse(Environment.GetEnvironmentVariable("LLAMA_NGL"), out var n)) return n;
        // macOS→Metal, Windows·Linux→CUDA (llama.cpp가 런타임에 백엔드 자동 선택·부재 시 CPU 폴백)
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() ? 99 : 0;
    }

    /// <summary>모델+배치컨텍스트 최초 1회 로드(멱등·스레드 안전). 14B q4 ≈ 9GB VRAM.</summary>
    private void EnsureLoaded()
    {
        lock (_loadLock)
        {
            if (_executor != null) return;
            if (string.IsNullOrEmpty(GgufPath) || !File.Exists(GgufPath))
                throw new FileNotFoundException($"GGUF 없음: {GgufPath}");

            var mp = new ModelParams(GgufPath)
            {
                ContextSize = (uint)CtxSize,
                GpuLayerCount = ResolveNgl(),
            };
            mp.FlashAttention = mp.GpuLayerCount > 0; // CPU 백엔드 FA 미지원 경로 회피
            _weights = LLamaWeights.LoadFromFile(mp);
            _executor = new BatchedExecutor(_weights, mp);
            _pipeline = new DefaultSamplingPipeline { Temperature = 0.3f };
        }
    }

    /// <summary>스플래시 프리로드 훅.</summary>
    public Task EnsureLoadedAsync(CancellationToken ct = default) => Task.Run(EnsureLoaded, ct);

    /// <summary>VRAM 순차 상재(2026-10-25): 번역 스테이지 종료 후 18.4GiB weights를 내려
    /// OCR VLM(dots 3B~Qwen3-VL 17GB) 재로드 자리를 만든다. 다음 번역 시 lazy 재로드.</summary>
    public bool IsLoaded { get { lock (_loadLock) return _executor != null; } }

    public void Unload()
    {
        lock (_loadLock)
        {
            if (_executor == null && _weights == null) return;
            try { _pipeline?.Dispose(); } catch { }
            try { _executor?.Dispose(); } catch { }
            try { _weights?.Dispose(); } catch { }
            _pipeline = null; _executor = null; _weights = null;
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
        }
    }

    // ---------- 번역 ----------
    public async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLang, string targetLang,
        IReadOnlyList<SpeechTone>? tones = null, CancellationToken ct = default)
    {
        if (texts.Count == 0) return Array.Empty<string>();
        await _gate.WaitAsync(ct);
        try
        {
            await Task.Run(EnsureLoaded, ct); // 14B 디스크→VRAM 로드: UI 스레드 차단 금지

            // 말투 마커(사용자 2026-10-27): (여)/(남)/(자)=auto — 프롬프트에만 주입, 출력은 원줄과 1:1
            var prompted = WithToneMarkers(texts, tones);
            var batch = await Task.Run(() => Generate(NumberedBatchProtocol.BuildPrompt(prompted),
                Math.Min(2048, 96 + EstimateChars(prompted))), ct);
            var parsed = NumberedBatchProtocol.Parse(batch, texts.Count);
            if (parsed != null) return parsed;

            // 폴백: 개별 라인(개별에도 마커 유지 — Parse 실패 라인만 재역)
            var outp = new List<string>(texts.Count);
            for (var i = 0; i < texts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var one = await Task.Run(() => Generate(prompted[i], 96 + texts[i].Length * 2), ct);
                outp.Add(NumberedBatchProtocol.Clean(one));
            }
            return outp;
        }
        finally { _gate.Release(); }
    }

    /// <summary>texts[i] 뒤에 말투 마커 부기. 전부 Auto면 원본 그대로(마크업 0).
    /// tones=null/mismatch → env TRANSLATE_TONE(female|male)이 페이지만지 전 페이지 기본말투(CLI smoke용).</summary>
    private static IReadOnlyList<string> WithToneMarkers(IReadOnlyList<string> texts, IReadOnlyList<SpeechTone>? tones)
    {
        if (tones == null || tones.Count != texts.Count)
        {
            var env = Environment.GetEnvironmentVariable("TRANSLATE_TONE")?.ToLowerInvariant();
            var dflt = env is "female" or "여" ? SpeechTone.Female : env is "male" or "남" ? SpeechTone.Male : SpeechTone.Auto;
            if (dflt == SpeechTone.Auto) return texts;
            tones = texts.Select(_ => dflt).ToList();
        }
        var any = tones.Any(t => t != SpeechTone.Auto);
        if (!any) return texts;
        var outp = new List<string>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
            outp.Add(tones[i] switch
            {
                SpeechTone.Female => texts[i] + " (여)",
                SpeechTone.Male => texts[i] + " (남)",
                _ => texts[i],
            });
        return outp;
    }

    private static int EstimateChars(IReadOnlyList<string> texts)
        => (int)Math.Min(4096, 64 + texts.Sum(t => (long)t.Length * 2));

    // ---------- 생성 (manual ChatML, thinking off) ----------
    private static string ImStart => "<|" + "im_start" + "|>";
    private static string ImEnd => "<|" + "im_end" + "|>";

    /// <summary>비thinking ChatML 프리픽스(Qwen3 템플릿 else-branch와 동일) + '/no_think' 2중 안전망.</summary>
    private string BuildChatML(string userContent)
        => ImStart + "system\n" + SystemPrompt + ImEnd + "\n" +
           ImStart + "user\n" + userContent + "\n/no_think" + ImEnd + "\n" +
           ImStart + "assistant\n";

    private string Generate(string userContent, int maxTokens)
    {
        var executor = _executor ?? throw new InvalidOperationException("executor 미로드");
        var pipeline = _pipeline ?? throw new InvalidOperationException("pipeline 미로드");

        using var conv = executor.Create();
        conv.Prompt(BuildChatML(userContent), addBos: false, special: true);

        var decoder = new StreamingTokenDecoder(executor.Context); // 0.30: IDisposable 아님
        var sb = new StringBuilder();
        for (var n = 0; n < maxTokens; n++)
        {
            var res = executor.Infer().GetAwaiter().GetResult();
            if (res != DecodeResult.Ok) break;
            if (!conv.RequiresSampling) continue;

            var token = pipeline.Sample(executor.Context.NativeHandle, conv.GetSampleIndex(0));
            if (token.IsEndOfGeneration(executor.Context.Vocab)) break;
            pipeline.Accept(token);
            decoder.Add(token);
            sb.Append(decoder.Read());
            conv.Prompt(token);
            if (sb.Length > 4096) break; // 폭주 안전망
        }
        return StripThinking(sb.ToString());
    }

    /// <summary>thinking 모드(on) 환경 잔재·template 누수 안전망: think 마커 이후/번호 없는 서두 제거.</summary>
    private static string StripThinking(string s)
    {
        s = Regex.Replace(s, "<\\|?think[^|>]*\\|?>", "", RegexOptions.IgnoreCase); // think 계열 제어토큰 전부
        var cut = s.IndexOf(ImEnd, StringComparison.Ordinal);                          // im_end 누수 시 이후 버림
        if (cut >= 0) s = s[..cut];
        return s.Trim();
    }

    public void Dispose()
    {
        lock (_loadLock)
        {
            try
            {
                _pipeline?.Dispose();
                _executor?.Dispose();
                _weights?.Dispose();
            }
            catch { }
            _pipeline = null;
            _executor = null;
            _weights = null;
        }
        _gate.Dispose();
    }
}
