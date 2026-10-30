using MangaTranslator.Core;
using MangaTranslator.Core.Imaging;
using ReactiveUI;
using ReactiveUI.Primitives.Disposables; // WhenActivated 블록 파라미터 MultipleDisposable
using System.Reactive.Linq;

namespace MangaTranslator.App.ViewModels;

/// <summary>읽기 방향 ComboBox 항목: 값+라벨+아이콘(Path Data). MainWindow.axaml DataTemplate이 참조.</summary>
public sealed record ReadingDirOption(ReadingDir Dir, string Label, string IconData);

/// <summary>말투 ComboBox 항목(사용자 2026-10-27): 번역 어투 성별·성격 힌트.</summary>
public sealed record ToneOption(SpeechTone Tone, string Label);

/// <summary>
/// 사용자 편집 가능한 텍스트 인스턴스(사각형). Step1(감지) 이후 좌표·내용을
/// 추가/삭제/수정할 수 있고, Apply 시 TextRegion으로 변환되어 전달된다.
/// </summary>
public sealed class TextInstanceViewModel : ViewModelBase
{
    // Lifetime/Activator/ActivateLifetime — ViewModelBase 상속 (구독은 .DisposeWith(Lifetime)).
    private double _left, _top, _right, _bottom;
    private string _text = "";
    private string _translatedText = "";
    private ReadingDir _readingDir = ReadingDir.HorizontalLeftToRight;
    private SpeechTone _tone = SpeechTone.Auto;
    private bool _isSelected;
    private double _confidence = -1;
    private bool _include = true;

    /// <summary>처리로 포함(☑)=제거+번역 대상. 미체크는 박스 밖과 같이 원본 그대로 보존.</summary>
    public bool Include { get => _include; set => this.RaiseAndSetIfChanged(ref _include, value); }

    /// <summary>원본 글자 크기(px)— FromRegion→ToRegion 왕복에서 유지. 0=미측정.</summary>
    public double SrcCharPx { get; set; }
    /// <summary>원본이 볼드였는지 — 재조판 볼드 유지.</summary>
    public bool Bold { get; set; }

    private double _fontScale = 1.0;
    /// <summary>재조판 글자 크기 배율(사용자Knob): 1=자동(원본크기 match). 크게/작게 조절 가능.</summary>
    public double FontScale { get => _fontScale; set => this.RaiseAndSetIfChanged(ref _fontScale, value); }

    /// <summary>캔버스 클릭→선택 요청(View code-behind가 Raise).</summary>
    public event EventHandler? RequestSelect;

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    /// <summary>OCR 신뢰도(-1=미측정). UI 표시용.</summary>
    public double Confidence { get => _confidence; set => this.RaiseAndSetIfChanged(ref _confidence, value); }

    public void RaiseSelect() => RequestSelect?.Invoke(this, EventArgs.Empty);

    /// <summary>드래그 이동(px 델타).</summary>
    public void MoveBy(double dx, double dy)
    {
        Left += dx; Top += dy; Right += dx; Bottom += dy;
    }

    /// <summary>코너 드래그로 크기 조절. idx: 0=TL 1=TR 2=BL 3=BR. 최소 16px 유지.</summary>
    public void ResizeCorner(int idx, double dx, double dy)
    {
        const double Min = 16;
        switch (idx)
        {
            case 0: // TL 고정점=BR
                Left = Math.Min(Left + dx, Right - Min);
                Top = Math.Min(Top + dy, Bottom - Min);
                break;
            case 1: // TR 고정점=BL
                Right = Math.Max(Right + dx, Left + Min);
                Top = Math.Min(Top + dy, Bottom - Min);
                break;
            case 2: // BL 고정점=TR
                Left = Math.Min(Left + dx, Right - Min);
                Bottom = Math.Max(Bottom + dy, Top + Min);
                break;
            default: // BR 고정점=TL
                Right = Math.Max(Right + dx, Left + Min);
                Bottom = Math.Max(Bottom + dy, Top + Min);
                break;
        }
    }

    // Width/Height도 함께 알림 — Canvas 오버레이 레이아웃 갱신
    public double Left { get => _left; set { this.RaiseAndSetIfChanged(ref _left, value); this.RaisePropertyChanged(nameof(Width)); RaiseHandles(); } }
    public double Top { get => _top; set { this.RaiseAndSetIfChanged(ref _top, value); this.RaisePropertyChanged(nameof(Height)); RaiseHandles(); } }
    public double Right { get => _right; set { this.RaiseAndSetIfChanged(ref _right, value); this.RaisePropertyChanged(nameof(Width)); RaiseHandles(); } }
    public double Bottom { get => _bottom; set { this.RaiseAndSetIfChanged(ref _bottom, value); this.RaisePropertyChanged(nameof(Height)); RaiseHandles(); } }

    /// <summary>코너 핸들 좌표(18px 핸들의 Canvas 위치 = 코너∓9). 선택 시 표시.</summary>
    public double H0X => Left - 9;
    public double H0Y => Top - 9;
    public double H1X => Right - 9;
    public double H1Y => Top - 9;
    public double H2X => Left - 9;
    public double H2Y => Bottom - 9;
    public double H3X => Right - 9;
    public double H3Y => Bottom - 9;

    private void RaiseHandles()
    {
        foreach (var p in new[] { "H0X", "H0Y", "H1X", "H1Y", "H2X", "H2Y", "H3X", "H3Y" })
            this.RaisePropertyChanged(p);
    }

    public string Text { get => _text; set => this.RaiseAndSetIfChanged(ref _text, value); }
    public string TranslatedText { get => _translatedText; set => this.RaiseAndSetIfChanged(ref _translatedText, value); }

    /// <summary>읽기 방향(ComboBox 4택, 2026-10-26): 시작 모서리+축. 재조판 정렬·열 순서 결정.</summary>
    public ReadingDir ReadingDir
    {
        get => _readingDir;
        set
        {
            this.RaiseAndSetIfChanged(ref _readingDir, value);
            this.RaisePropertyChanged(nameof(Vertical));
            this.RaisePropertyChanged(nameof(ReadingDirOption));
        }
    }

    /// <summary>ComboBox 선택 항목(아이콘+라벨). 값 비교라 record equality로 복원bindOK.</summary>
    public ReadingDirOption ReadingDirOption
    {
        get => OptionFor(_readingDir);
        set { if (value != null) ReadingDir = value.Dir; }
    }

    /// <summary>4방향 옵션(단일 진실원) — 아이콘: 화살 시작 변 = 읽기 첫 위치. 세로=열 순서 화살표.</summary>
    public static IReadOnlyList<ReadingDirOption> ReadingDirs { get; } = new[]
    {
        new ReadingDirOption(ReadingDir.HorizontalLeftToRight,       "← 가로 왼쪽", "M1,7 H13 M10,4.2 L13,7 L10,9.8"),
        new ReadingDirOption(ReadingDir.HorizontalRightToLeft,       "→ 가로 오른쪽", "M13,7 H1 M4,4.2 L1,7 L4,9.8"),
        new ReadingDirOption(ReadingDir.VerticalTopToBottomLeftToRight,  "↓ 세로 왼쪽", "M3,1 V13 M0.2,10 L3,13 L5.8,10"),
        new ReadingDirOption(ReadingDir.VerticalTopToBottomRightToLeft,  "↓ 세로 오른쪽", "M11,1 V13 M8.2,10 L11,13 L13.8,10"),
    };

    private static ReadingDirOption OptionFor(ReadingDir d) =>
        System.Array.Find(ReadingDirs.ToArray(), o => o.Dir == d)!;
    /// <summary>세로 여부 = ReadingDir 파생(재인식 자동설정·라벨 등 하위호환).</summary>
    public bool Vertical
    {
        get => _readingDir is ReadingDir.VerticalTopToBottomLeftToRight or ReadingDir.VerticalTopToBottomRightToLeft;
        set => ReadingDir = value ? ReadingDir.VerticalTopToBottomRightToLeft : ReadingDir.HorizontalLeftToRight;
    }
    /// <summary>우측 시작(세로 열 우→좌 / 가로 우 정렬).</summary>
    public bool ColumnRightToLeft => _readingDir is ReadingDir.VerticalTopToBottomRightToLeft
        or ReadingDir.HorizontalRightToLeft;

    /// <summary>말투(번역 어투 힌트, 사용자 2026-10-27): 여성형/남성형 강제 또는 자동 추정.</summary>
    public SpeechTone Tone
    {
        get => _tone;
        set { this.RaiseAndSetIfChanged(ref _tone, value); this.RaisePropertyChanged(nameof(ToneOption)); }
    }

    public ToneOption ToneOption
    {
        get => new(_tone, ToneLabel(_tone));
        set { if (value != null) Tone = value.Tone; }
    }

    /// <summary>말투 3택 옵션(단일 진실원).</summary>
    public static IReadOnlyList<ToneOption> ToneOptions { get; } = new[]
    {
        new ToneOption(SpeechTone.Auto, "말투 자동"),
        new ToneOption(SpeechTone.Female, "여성형"),
        new ToneOption(SpeechTone.Male, "남성형"),
    };

    private static string ToneLabel(SpeechTone t) => t switch
    {
        SpeechTone.Female => "여성형",
        SpeechTone.Male => "남성형",
        _ => "말투 자동",
    };

    public double Width => Math.Max(0, Right - Left);
    public double Height => Math.Max(0, Bottom - Top);

    public ReactiveCommand<RxVoid, RxVoid> RemoveCommand { get; private set; } = null!;

    public event EventHandler? RequestRemove;
    /// <summary>🔄 버튼: 이 instance 하나만 OCR 재판독 요청 — 소유 편집기(PageEditor)가 구독 실행.</summary>
    public event EventHandler? RequestReocr;

    /// <summary>이 박스만 재인식(행 버튼 ✕ 옆 🔄).</summary>
    public ReactiveCommand<RxVoid, RxVoid> ReocrCommand { get; private set; } = null!;

    /// <summary>페이지 요소임을 갖는 요소도 대표 파일 경로 — 편집 재인식 시 원본 특정.</summary>
    public string? SourcePath { get; set; }

    public TextInstanceViewModel()
    {
        this.WhenActivated((MultipleDisposable d) => InitializeCommands(d));
        ActivateLifetime();
    }

    private void InitializeCommands(MultipleDisposable d)
    {
        RemoveCommand = ReactiveCommand.Create(() => RequestRemove?.Invoke(this, EventArgs.Empty)).DisposeWith(d);
        ReocrCommand = ReactiveCommand.Create(() => RequestReocr?.Invoke(this, EventArgs.Empty)).DisposeWith(d);
    }

    protected override void DisposeCore()
    {
        RemoveCommand.Dispose();
        ReocrCommand.Dispose();
        RequestRemove = null;
        RequestSelect = null;
        RequestReocr = null;
    }

    public static TextInstanceViewModel FromRegion(TextRegion r) => new()
    {
        Left = r.Box.Left, Top = r.Box.Top, Right = r.Box.Right, Bottom = r.Box.Bottom,
        Text = r.Text, ReadingDir = r.ReadingDir,
        TranslatedText = r.TranslatedText, Confidence = r.Confidence,
        SrcCharPx = r.Style.SrcCharPx, Bold = r.Style.Bold,
        Tone = r.Tone, Include = r.Include,
        FontScale = r.Style.FontScale,   // 통합 설정 왕복 보존(2026-10-28)
    };

    /// <summary>통합 설정(BatchProject) 항목 → 편집 인스턴스(설정 기반 재조판 전 하이드레이트).</summary>
    public static TextInstanceViewModel FromConfig(Core.Batch.RegionConfig rc) => new()
    {
        Left = rc.Left, Top = rc.Top, Right = rc.Right, Bottom = rc.Bottom,
        Text = rc.Text, TranslatedText = rc.TranslatedText,
        ReadingDir = (ReadingDir)rc.Dir, Tone = (SpeechTone)rc.Tone,
        Include = rc.Include, Confidence = rc.Confidence,
        SrcCharPx = rc.SrcCharPx, Bold = rc.Bold, FontScale = rc.FontScale,
    };

    public TextRegion ToRegion() => new()
    {
        Box = new BoundingBox((int)Left, (int)Top, (int)Right, (int)Bottom),
        Text = Text,
        ReadingDir = ReadingDir,
        Tone = Tone,
        Include = Include,   // ☑=제거+번역 대상 / ☐=원본 보존(2026-10-28 통합 설정 캐리어)
        Style = new TextStyle
        {
            FontSizePx = (float)Math.Clamp(Max(SrcCharPx, Height * 0.7), 12.0, 200.0),
            SrcCharPx = (float)SrcCharPx,   // OCR 원본 크기 유지 → 재조판 크기 MATCH
            Bold = Bold,
            FontScale = (float)FontScale,   // 사용자 크기 knob
        },
    };

    private static double Max(double a, double b) => a > b ? a : b;
}
