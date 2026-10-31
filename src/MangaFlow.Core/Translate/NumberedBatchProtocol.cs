using System.Text.RegularExpressions;

namespace MangaFlow.Core.Translate;

/// <summary>
/// '번호. 텍스트' 배치 번역 프로토콜 — LLM 계열(Qwen GGUF, GPT/OpenAI 호환) 공통 단일 진실원.
/// 프롬프트 구성/응답 파싱/클리닝이 엔진마다 중복·편차되는 것을 중앙화한다.
/// 규칙: 'n.'/'n)'/'n:' 매칭, n∈[1,기대치], 전 줄 충족 시에만 성공(null→호출부 개별 폴백).
/// </summary>
public static class NumberedBatchProtocol
{
    private static readonly Regex LineNum = new(@"^(\d+)[.):]?\s+(.+)$", RegexOptions.Compiled);

    public static string BuildPrompt(IReadOnlyList<string> texts)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < texts.Count; i++)
            sb.Append(i + 1).Append(". ").AppendLine(texts[i]);
        return sb.ToString();
    }

    /// <summary>'n. 번역' 줄만 채집(각 줄 Clean). 하나라도 비면 null — 호출부는 개별 라인 폴백으로.</summary>
    public static IReadOnlyList<string>? Parse(string raw, int expect)
    {
        var map = new string?[expect];
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = LineNum.Match(line);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n >= 1 && n <= expect)
                map[n - 1] = Clean(m.Groups[2].Value);
        }
        return map.All(s => !string.IsNullOrEmpty(s)) ? map.Select(s => s!).ToList() : null;
    }

    /// <summary>모델이 붙이는 서두 장식 제거: '번역:'·따옴표·이중 번호·구두점 — 안정화까지 반복.
    /// 말투 마커 누수((여)/(남)/(자)) 안전망: 번역이 마커를 재출력하면 항상 제거한다.</summary>
    public static string Clean(string s)
    {
        s = s.Trim();
        string prev;
        do
        {
            prev = s;
            s = Regex.Replace(s, @"\s*[(（](여|남|자)[)）]\s*$", ""); // 말투 마커 echo 제거
            s = s.TrimStart('「', '『', '“', '"', '\'', '·', '-', '•');
            if (s.StartsWith("번역", StringComparison.Ordinal)) s = s[2..];
            s = s.TrimStart(':', '：', '.', ' ', '"', '\'', '”', '』', '」');
            s = Regex.Replace(s, @"^\d+[.)]\s*", "");
        } while (s != prev);
        return s.Trim();
    }
}
