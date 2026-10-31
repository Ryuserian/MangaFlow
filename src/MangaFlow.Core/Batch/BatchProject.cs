using System.Text.Json;
using MangaFlow.Core.Imaging;

namespace MangaFlow.Core.Batch;

/// <summary>
/// 하나의 말풍선 설정(직렬화 가능 DTO) — 파일 개별 설정이 아니라 통합 설정의 항목(사용자 2026-10-28).
/// OCR 인식 결과가 여기로 모이고, 번역·재조판 실행은 이 설정만 읽어서 수행한다.
/// 사용자 편집(박스 이동/☑/방향/말투/크기 knob)이 캡처되면 이 항목이 갱신된다.
/// </summary>
public sealed class RegionConfig
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public string Text { get; set; } = "";
    public string TranslatedText { get; set; } = "";
    /// <summary>ReadingDir enum 값(int 직렬화 — enum 이름 변경에 무관).</summary>
    public int Dir { get; set; }
    /// <summary>SpeechTone enum 값(int).</summary>
    public int Tone { get; set; }
    /// <summary>처리로 포함(☑). false면 재조판 시 원본 보존.</summary>
    public bool Include { get; set; } = true;
    public double Confidence { get; set; } = 1.0;
    public double SrcCharPx { get; set; }
    public bool Bold { get; set; }
    public double FontScale { get; set; } = 1.0;

    public static RegionConfig FromRegion(TextRegion r) => new()
    {
        Left = r.Box.Left, Top = r.Box.Top, Right = r.Box.Right, Bottom = r.Box.Bottom,
        Text = r.Text, TranslatedText = r.TranslatedText,
        Dir = (int)r.ReadingDir, Tone = (int)r.Tone, Include = true,
        Confidence = r.Confidence, SrcCharPx = r.Style.SrcCharPx,
        Bold = r.Style.Bold, FontScale = r.Style.FontScale,
    };

    /// <summary>파이프라인이 소비할 TextRegion으로 복원. 크기 산식은 VM.ToRegion과 동일 계수.</summary>
    public TextRegion ToRegion() => new()
    {
        Box = new BoundingBox(Left, Top, Right, Bottom),
        Text = Text,
        TranslatedText = TranslatedText,
        ReadingDir = (ReadingDir)Dir,
        Tone = (SpeechTone)Tone,
        Include = Include,
        Confidence = Confidence,
        Style = new TextStyle
        {
            FontSizePx = (float)Math.Clamp(Math.Max(SrcCharPx, (Bottom - Top) * 0.7), 12.0, 200.0),
            SrcCharPx = (float)SrcCharPx,
            Bold = Bold,
            FontScale = (float)FontScale,
        },
    };
}

/// <summary>통합 설정의 페이지 항목 = 파일 경로 + 그 장의 모든 말풍선 설정.</summary>
public sealed class PageConfig
{
    public string Path { get; set; } = "";
    public List<RegionConfig> Regions { get; set; } = new();
}

/// <summary>
/// 배치 통합 설정(2026-10-28 사용자 결정): 파일별 개별 설정 파일 대신
/// **대기열 전체의 설정을 메모리에 하나**로 들고 다니며, ①인식(OCR)에서 채우고
/// ②실행(번역·재조판)에서 이걸 읽어 전체를 처리한다.
/// 단일 JSON(batch-project.json)로 백업 저장 — 다시 열면 이어서 재조판 가능.
/// </summary>
public sealed class BatchProject
{
    public string SourceLang { get; set; } = "ja";
    public string TargetLang { get; set; } = "ko";
    /// <summary>설정 수집 시점의 번역 엔진 키 — 실행 시 콤보로 바뀌면 실행 키가 우선.</summary>
    public string TranslateEngine { get; set; } = "qwen_gguf";
    public DateTime SavedAtUtc { get; set; }
    public List<PageConfig> Pages { get; set; } = new();

    public int Count => Pages.Count;
    public PageConfig? For(string path) => Pages.FirstOrDefault(p => p.Path == path);
    public bool Has(string path) => For(path) != null;

    /// <summary>같은 경로의 페이지 설정을 교체(없으면 추가). 인식/편집 직후 호출로 설정을 신선하게 유지.</summary>
    public void Upsert(string path, IEnumerable<RegionConfig> regions)
    {
        var pc = For(path);
        if (pc == null) Pages.Add(new PageConfig { Path = path, Regions = regions.ToList() });
        else pc.Regions = regions.ToList();
    }

    public void SaveJson(string path)
    {
        SavedAtUtc = DateTime.UtcNow;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    public static BatchProject? TryLoadJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<BatchProject>(File.ReadAllText(path));
        }
        catch { return null; }
    }
}
