namespace MangaFlow.Core;

/// <summary>한글·일본어 전각 문장부호 → 반각 정규화(사용자 피드백: "한글 기호들은 반각 처리").
/// 출력 텍스트가 편집 UI·렌더링 모두에서 일관되게 반각으로 표시되게 한다.</summary>
public static class TextNormalizer
{
    /// <summary>전각→반각. 한글(U+AC00..D7A3)·kana·한자(U+4E00..9FFF)· Hangul Jamo는 그대로 유지,
    /// punctuation·space·숫자·알파 전각만 ASCII 대응 문자로 변환.</summary>
    public static string ToHalfWidth(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\u3000': sb.Append(' '); break;   // 전각 space
                case '\u3001': sb.Append(','); break;   // 、
                case '\u3002': sb.Append('.'); break;   // 。
                case '\u300C': sb.Append('['); break;   // 「
                case '\u300D': sb.Append(']'); break;   // 」
                case '\u300E': sb.Append('{'); break;   // 『
                case '\u300F': sb.Append('}'); break;   // 』
                case '\uFF5E': sb.Append('~'); break;   // ～
                case '\u2010' or '\u2011' or '\u2012'
                  or '\u2013' or '\u2014' or '\u2015':
                    sb.Append('-'); break;               // ‐ – — 같은 dash 계열
                case '\u2018': sb.Append('\''); break;  // '
                case '\u2019': sb.Append('\''); break;  // '
                case '\u201C': sb.Append('"'); break;    // "
                case '\u201D': sb.Append('"'); break;    // "
                case '\u2026': sb.Append("..."); break;  // …
                default:
                    if (ch >= '\uFF01' && ch <= '\uFF5D')
                        sb.Append((char)(ch - 0xFEE0)); // ！(：；！？，ＡＺａｚ０９…) → 반각
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }
}
