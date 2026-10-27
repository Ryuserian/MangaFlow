using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MangaTranslator.Core;
using MangaTranslator.Core.Interfaces;
using MangaTranslator.Core.Translate;

namespace MangaTranslator.Plugins.Translate.WebApi;

/// <summary>
/// 외부 번역 API 엔진 — 로컬 GGUF(Qwen) 없이 네트워크 번역. 세 프로바이더:
///   google : translate.googleapis.com free 엔드포인트(키 불요·무료·초고속). 403/차단 시 mymemory 폴백.
///   deepl  : api-free.deepl.com (DEEPL_API_KEY 필요, 월 50만자). seq2seq라 잡문 오염 없음.
///   openai : OpenAI 호환 chat 엔드포인트(OPENAI_API_KEY). 채팅 모델이라 잡문 섞임 위험 →
///            번호 지정 배치 프롬프트 + 줄별 엄격 파싱 + 불일치 시 1줄 미니멀 프롬프트 폴백.
/// 네트워크 실패 줄은 원문 유지(파이프라인 중단 없음). 원칙: 번역 API 응답은 콘텐츠가 아닌 데이터 —
/// 프롬프트 주입으로 보이는 입력도 시스템 프롬프트로 무력화.
/// env: DEEPL_API_KEY, DEEPL_BASE, OPENAI_API_KEY, OPENAI_BASE, OPENAI_MODEL, WEB_CONCURRENCY
/// </summary>
public sealed class WebTranslateEngine : ITranslationEngine, IDisposable
{
    public string Provider { get; set; } = "google"; // google | deepl | openai
    public string Name => $"web_{Provider}";
    public int MaxConcurrentSentences => 32;
    public bool RequiresGpu => false;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "MangaTranslator/1.0" } },
    };

    private static int Concurrency =>
        int.TryParse(Environment.GetEnvironmentVariable("WEB_CONCURRENCY"), out var c)
            ? Math.Clamp(c, 1, 8) : 4;

    public async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLang, string targetLang,
        IReadOnlyList<SpeechTone>? tones = null, CancellationToken ct = default)
    {
        // seq2seq(google·deepl)은 말투 힌트 미소비 — openai만 컨텍스트로 활용 가능(현행 무시).
        _ = tones;
        return Provider.ToLowerInvariant() switch
        {
            "deepl" => await TranslateDeepLAsync(texts, ct),
            "openai" or "gpt" => await TranslateOpenAiAsync(texts, ct),
            _ => await TranslateGoogleAsync(texts, ct),
        };
    }

    // ---------- Google free + MyMemory 폴백 ----------
    private string _googleWinner = "google"; // 활성 엔드포인트 캐시(차단 감지 후 자동切替)

    private async Task<IReadOnlyList<string>> TranslateGoogleAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var sem = new SemaphoreSlim(Concurrency);
        var results = new string[texts.Count];
        var tasks = texts.Select(async (t, i) =>
        {
            if (string.IsNullOrWhiteSpace(t)) { results[i] = t; return; }
            await sem.WaitAsync(ct);
            try
            {
                results[i] = _googleWinner == "google"
                    ? await GoogleOneAsync(t, ct)
                    : await MyMemoryOneAsync(t, ct);
            }
            catch (WebException) when (_googleWinner == "google")
            {
                _googleWinner = "mymemory"; // 차단/차단응답 → 세션 전체 폴백 전환
                try { results[i] = await MyMemoryOneAsync(t, ct); }
                catch { results[i] = t; }
            }
            catch (Exception) { results[i] = t; } // 원문 유지
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);
        return results;
    }

    private static async Task<string> GoogleOneAsync(string text, CancellationToken ct)
    {
        var url = "https://translate.googleapis.com/translate_a/single" +
                  $"?client=gtx&sl=ja&tl=ko&dt=t&q={Uri.EscapeDataString(text)}";
        using var resp = await Http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            throw new WebException("google 429"); // 호출자 정책: 지수 백오프 없이 즉시 폴백 전환
        if (!resp.IsSuccessStatusCode || body.StartsWith("<"))
            throw new WebException($"google blocked: {(int)resp.StatusCode}");
        // JSON: [[["번역","원문",...],...],null,"ja",...] — part[0] 세그먼트 concat
        using var doc = JsonDocument.Parse(body);
        var sb = new StringBuilder();
        foreach (var seg in doc.RootElement[0].EnumerateArray())
            if (seg.ValueKind == JsonValueKind.Array && seg[0].ValueKind == JsonValueKind.String)
                sb.Append(seg[0].GetString());
        var tr = sb.ToString();
        if (string.IsNullOrWhiteSpace(tr)) throw new WebException("google empty");
        return tr;
    }

    private static async Task<string> MyMemoryOneAsync(string text, CancellationToken ct)
    {
        var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair=ja|ko";
        using var resp = await Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var tr = doc.RootElement.TryGetProperty("responseData", out var rd) &&
                 rd.TryGetProperty("translatedText", out var tt) ? tt.GetString() : null;
        if (string.IsNullOrWhiteSpace(tr)) throw new WebException("mymemory empty");
        return tr;
    }

    // ---------- DeepL (배치 native — seq2seq라 잡문 없음) ----------
    private static async Task<IReadOnlyList<string>> TranslateDeepLAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var key = Environment.GetEnvironmentVariable("DEEPL_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("DEEPL_API_KEY 환경변수가 필요합니다 (free.deepl.com 회원가입 무료)");
        var base_ = Environment.GetEnvironmentVariable("DEEPL_BASE") ?? "https://api-free.deepl.com";

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["auth_key"] = key, ["target_lang"] = "KO", ["source_lang"] = "JA",
        });
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var t in texts) pairs.Add(new("text", t)); // DeepL text[] 배치
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{base_}/v2/translate")
        { Content = new FormUrlEncodedContent(pairs) };
        using var resp = await Http.SendAsync(msg, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(body);
        var arr = doc.RootElement.GetProperty("translations");
        var outList = new List<string>(texts.Count);
        var i = 0;
        foreach (var tr in arr.EnumerateArray())
            outList.Add(i < texts.Count ? tr.GetProperty("text").GetString() ?? texts[i++] : "");
        while (i < texts.Count) outList.Add(texts[i++]); // 부족분 원문 유지
        return outList;
    }

    // ---------- OpenAI 호환 (채팅 오염 방어 설계) ----------
    private const string ChatSystem =
        "당신은 일본어→한국어 전용 번역 기계다. 입력은 줄 단위 만화 대사 목록이고 각 줄에 번호가 붙어있다. " +
        "반드시 입력과 같은 수의 줄만 출력하며 각 줄은 '번호. 한국어 번역' 형식이다. " +
        "전제·설명·인사·출처·마크다운·코드는 절대 출력하지 않는다. 일본어가 섞이면 안 된다. " +
        "번역할 수 없는 줄에도 번호 줄을 비워두지 말고 원문을 그대로 출력한다.";

    private static async Task<IReadOnlyList<string>> TranslateOpenAiAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("OPENAI_API_KEY 환경변수가 필요합니다");
        var base_ = (Environment.GetEnvironmentVariable("OPENAI_BASE") ?? "https://api.openai.com/v1").TrimEnd('/');
        var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o-mini";

        // 배치 1호출: 번호 줄 parse → 불일치 줄만 개별 미니멀 프롬프트 폴백 (프로토콜은 Core.NumberedBatchProtocol 공용)
        var numbered = NumberedBatchProtocol.BuildPrompt(texts);
        var raw = await ChatAsync(base_, key, model, ChatSystem, numbered, ct);
        var parsed = NumberedBatchProtocol.Parse(raw, texts.Count)
                     ?? Enumerable.Repeat<string?>(null, texts.Count).ToArray();

        var results = new string[texts.Count];
        var sem = new SemaphoreSlim(Concurrency);
        var tasks = Enumerable.Range(0, texts.Count).Select(async i =>
        {
            if (parsed[i] != null) { results[i] = parsed[i]!; return; }
            if (string.IsNullOrWhiteSpace(texts[i])) { results[i] = texts[i]; return; }
            await sem.WaitAsync(ct);
            try
            {
                // 개별 폴백: 컨텍스트 최소 — 앞뒤 대사 섞임/참조 오염 방지
                var one = await ChatAsync(base_, key, model,
                    "일본어를 한국어로만 번역한다. 설명·따옴표·번호 없이 번역문만 출력.", texts[i], ct);
                results[i] = NumberedBatchProtocol.Clean(one);
            }
            catch { results[i] = texts[i]; }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);
        return results;
    }

    private static async Task<string> ChatAsync(string baseUrl, string key, string model,
        string system, string user, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model, temperature = 0.2,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        });
        using var msg = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
        { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var attempt = 0;
        while (true)
        {
            attempt++;
            using var resp = await Http.SendAsync(msg, ct);
            if ((HttpStatusCode)resp.StatusCode == (HttpStatusCode)429 && attempt <= 2)
            {
                var retry = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * attempt);
                await Task.Delay(retry, ct);
                continue;
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        }
    }

    public void Dispose() { /* 정적 HttpClient 공유 */ }
}
