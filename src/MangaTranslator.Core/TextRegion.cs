using MangaTranslator.Core.Imaging;

namespace MangaTranslator.Core;

/// <summary>
/// OCR이 찾아낸 하나의 텍스트 영역: 위치(Box) + 내용(Text) + 원래 스타일 추정치.
/// 스타일 스키마를 엔진에 종속시키지 않는 것이 "모델 교체 가능"의 핵심.
/// </summary>
public sealed class TextRegion
{
    public BoundingBox Box { get; set; }
    public string Text { get; set; } = "";          // 인식된 원문 (일본어)
    public string TranslatedText { get; set; } = "";// 번역문 (한국어), Pipeline이 기입
    /// <summary>읽기 방향(사용자 ComboBox 선택) — 레이아웃 단일 진실원.
    /// Vertical/ColumnRightToLeft은 여기서 파생(하위호환 계산식).</summary>
    public ReadingDir ReadingDir { get; set; } = ReadingDir.HorizontalLeftToRight;
    /// <summary>세로쓰기 여부 = ReadingDir의 계산값(get/set 호환).</summary>
    public bool Vertical
    {
        get => ReadingDir is ReadingDir.VerticalTopToBottomLeftToRight or ReadingDir.VerticalTopToBottomRightToLeft;
        set => ReadingDir = value ? ReadingDir.VerticalTopToBottomRightToLeft : ReadingDir.HorizontalLeftToRight;
    }
    /// <summary>세로 열 진행 순서: true=우→좌(일본 만화 원본 관행).</summary>
    public bool ColumnRightToLeft => ReadingDir is ReadingDir.VerticalTopToBottomRightToLeft
        or ReadingDir.HorizontalRightToLeft;
    public double Confidence { get; set; } = 1.0;

    public TextStyle Style { get; set; } = new();

    /// <summary>inpaint용: 박스 확장(여백 포함). pad=8 — LaMa 클린 영역을 렌더 inflate와 동일하게.</summary>
    public BoundingBox InpaintBox(int pad = 8) => new(
        Box.Left - pad, Box.Top - pad, Box.Right + pad, Box.Bottom + pad);
}

/// <summary>원본 텍스트의 스타일 추정. v0는 휴리스틱, v1+는 폰트 분류 모델로 교체.</summary>
public sealed class TextStyle
{
    public float FontSizePx { get; set; } = 24;
    /// <summary>원본 레이아웃에서 글자 1자 크기(px). 세로=열너비/열수, 가로=행높이/행수. 렌더字号MATCH의 기준.</summary>
    public float SrcCharPx { get; set; }
    /// <summary>재조판 글자 크기 배율(사용자Knob). 1=자동(원본_match), >1 크게, <1 작게. grow-search 출발값과 마지노선에 곱해진다.</summary>
    public float FontScale { get; set; } = 1f;
    /// <summary>원본 세로열(또는 가로행) 개수 — GroupColumns 병합 시 열수 기록.</summary>
    public int SrcCols { get; set; } = 1;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public string? FontFamilyHint { get; set; }      // 원본 폰트 추정 (v1)
    public uint FillBgra { get; set; } = 0xFFFFFFFF; // 텍스트 색상 (BGRA)
    public uint OutlineBgra { get; set; } = 0xFF000000;
    public float OutlineWidth { get; set; } = 2f;
    public BalloonKind Balloon { get; set; } = BalloonKind.Unknown;
}

public enum BalloonKind { Unknown, SpeechBubble, NarrationBox, Shout, Sfx, PlainText }

/// <summary>말풍선 읽기·재조판 방향(사용자 선택, 2026-10-26 ComboBox).
/// 시작 모서리 + 축: 읽기 첫 문자 위치와 진행 축. 세로일 때 열 순서, 가로일 때 행 정렬 기준을 결정한다.</summary>
public enum ReadingDir
{
    /// <summary←→ 가로쓰기, 왼쪽부터 채움(한국어 표준 — 번역재조판 기본).</summary>
    HorizontalLeftToRight = 0,
    /// <summary>↑↓ 세로쓰기, 열은 왼쪽→오른쪽(한국식 세로).</summary>
    VerticalTopToBottomLeftToRight = 1,
    /// <summary>←→ 가로쓰기, 오른쪽부터(원문 우좌말풍선: 昔의 만화 가로 쓰김).</summary>
    HorizontalRightToLeft = 2,
    /// <summary>↓↑ 세로쓰기, 열은 오른쪽→왼쪽(일본 만화 관행 — OCR 세로 감지 시 자동).</summary>
    VerticalTopToBottomRightToLeft = 3,
}
