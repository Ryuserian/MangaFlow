using System.Runtime.InteropServices;
using MangaTranslator.Core;
using MangaTranslator.Core.Imaging;
using MangaTranslator.Core.Interfaces;
using SkiaSharp;

namespace MangaTranslator.Plugins.Render.Skia;

/// <summary>
/// 한국어 재조판 렌더러 (SkiaSharp, Apache-2.0). OCR 스타일 추정(TextStyle)을 반영해
/// 같은 박스에 가로/세로(만화식 우→좌 열 내리기) 배치. 폰트는 카탈로그의 NotoSansKR(OFL).
/// GUI lifetime과 무관하게 ImageData↔BGRA로만 작동 → CLI 파이프라인 검증 가능.
/// 사용자 피드백(2026-10-24): stroke 제거 → 배경 밝기로 글자색 자동(밝은 말풍선=검정, 어두운=흰색).
/// 사용자 피드백(2026-10-25): inpaint로 회색 스크린톤이 복원된 배경 위 검은 글자가 안 읽힘 →
/// halo(외곽선) 부활. 글자와 **반대색** round-join stroke를 채움 beneath에 그린다(검정글자=흰 외곽).
/// env RENDER_OUTLINE=fs 대비 두께 배율(기본 0.18, 0=off). ① 자간→긴밀(진행 1.0em) 유지.
/// </summary>
public sealed class SkiaFontRenderer : ITextRenderer, ILoadableEngine, IDisposable
{
    private static readonly bool Debug = Environment.GetEnvironmentVariable("RENDER_DEBUG") == "1";
    public string Name => "skia";
    public string? RegularFontPath { get; set; }
    public string? BoldFontPath { get; set; }

    private static float FEnv(string name, float def, float lo, float hi) =>
        float.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? Math.Clamp(v, lo, hi) : def;

    /// <summary>가로 행 간격(행높이/폰트). 1.12→1.08 긴밀화.</summary>
    public float LineHeight { get; set; } = 1.08f;
    /// <summary>세로 글자 진행 배율(fs×이). 만화 세로쓰기는 글자가 거의 붙어 있다. env RENDER_VGAP.</summary>
    public float VCharGap { get; set; } = FEnv("RENDER_VGAP", 0.95f, 0.8f, 1.3f);
    /// <summary>세로 열 스텝 배율(fs×이). 열간 빽빽. env RENDER_COLGAP.</summary>
    public float ColGap { get; set; } = FEnv("RENDER_COLGAP", 0.95f, 0.9f, 1.5f);
    /// <summary>세로 열 가로 허용 오버플로 배율 — 텍스트가 감지 박스를 넘지 않도록 1.0으로 조임(사용자 피드백: 한글이 사각형보다 크다). env RENDER_WSLACK.</summary>
    public float WidthSlack { get; set; } = FEnv("RENDER_WSLACK", 1.0f, 1.0f, 1.6f);

    /// <summary>최소 폰트 크기 마지노선(env RENDER_MIN_FS). 2026-10-25 사용자 피드백: 한글이
    /// 박스보다 크면 줄여야 함 → 마지노선을 폐기(기본 0=off)하고 strict-fit 우선. 필요한만 env로 복원.</summary>
    public float MinFontSizePx { get; set; } = FEnv("RENDER_MIN_FS", 0f, 0f, 200f);
    /// <summary>렌더 박스를 사방으로 팽창(px, env RENDER_INFLATE). 2026-10-25 기본 4로 축소 —
    /// strict-fit과 함께 한글이 감지 사각형을 넘는 체감 방지. 0이면 원 박스만.</summary>
    public float BoxInflatePx { get; set; } = FEnv("RENDER_INFLATE", 4f, 0f, 80f);
    /// <summary>전역 크기 배율(env RENDER_SCALE). 모든 재조판 fs에 곱한다(사용자: "여전히 작다").</summary>
    public float GlobalScale { get; set; } = FEnv("RENDER_SCALE", 1.4f, 0.5f, 2.5f);

    /// <summary>글자 외곽 halo 두께 = fs × 이 배율(env RENDER_OUTLINE). 0=off.
    /// halo 색은 fill과 반대: 검은 글자→흰 halo, 흰 글자→검은 halo(만화 표준 가독성 처리).</summary>
    public float OutlineWidth { get; set; } = FEnv("RENDER_OUTLINE", 0.18f, 0f, 0.5f);

    private static float EffMin(float minFs, float scale) => minFs > 0 ? minFs * Math.Clamp(scale, 0.4f, 3f) : 0f;

    private SKTypeface? _regular;
    private SKTypeface? _bold;

    /// <summary>스플래시 프리로드: NotoSansKR 타입페이스 선로드(첫 렌더 지연 제거).</summary>
    public Task EnsureLoadedAsync(CancellationToken ct = default) =>
        Task.Run(() => { _ = Typeface(false); _ = Typeface(true); }, ct);

    private SKTypeface Typeface(bool bold) =>
        bold ? _bold ??= Load(BoldFontPath) : _regular ??= Load(RegularFontPath);

    private static SKTypeface Load(string? path) =>
        path != null && File.Exists(path)
            ? SKTypeface.FromFile(path)
            : SKTypeface.Default ?? throw new InvalidOperationException("no font available");

    public void Render(ImageData page, IReadOnlyList<TextRegion> regions)
    {
        // 회귀 버그: 과거 코드 Marshal.Copy(page.Bgra→bmp)는 방향 반대 — 캔버스 드로잉이
        // 페이지 픽셀로 덮어써진 뒤 환원도 없어 렌더가 통째로 사라졌다.
        // InstallPixels로 SKBitmap을 page.Bgra에 제로카피 래핑 → 드로잉이 곧 페이지 수정.
        var handle = GCHandle.Alloc(page.Bgra, GCHandleType.Pinned);
        try
        {
            using var bmp = new SKBitmap();
            bmp.InstallPixels(
                new SKImageInfo(page.Width, page.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul),
                handle.AddrOfPinnedObject(), page.Stride);
            using var canvas = new SKCanvas(bmp);
            foreach (var r in regions)
                if (!string.IsNullOrEmpty(r.TranslatedText))
                    DrawRegion(canvas, r, page);
            canvas.Flush();
        }
        finally { handle.Free(); }
    }

    private void DrawRegion(SKCanvas canvas, TextRegion r, ImageData page)
    {
        // 전각 공백(U+3000)→반각 정규화: 번역 출력에 섞여 들어와 자간이 넓어 보인다(사용자 피드백).
        var text = NormalizeText(r.TranslatedText);
        if (text.Length == 0) return;
        // 렌더 영역 = 감지 박스 + 팽창. detection box는 말풍선 내측보다 좁게 잡혀
        // 한글이 원본보다 작아진다 → 말풍선 안 여백을 허용. 비율 상한(15%)으로
        // 작은 박스·파널 프레임 인접 침범 방지.
        var b = r.Box;
        var inf = (int)Math.Min(BoxInflatePx, Math.Min(b.Width, b.Height) * 0.15f);
        var box = new BoundingBox(
            Math.Max(0, b.Left - inf), Math.Max(0, b.Top - inf),
            Math.Min(page.Width, b.Right + inf), Math.Min(page.Height, b.Bottom + inf));
        if (box.Width <= 4 || box.Height <= 4) return;

        var typeface = Typeface(r.Style.Bold);
        // 배경 밝기로 글자 색 자동 — 흰 말풍선=검정, 어두운 배경=흰색.
        var fill = AutoTextColor(page, box);
        // halo = fill과 반대색: 회색 스크린톤 위 검은 글자를 흰 외곽으로 살림(사용자 2026-10-25)
        var halo = (fill.Red * 11 + fill.Green * 59 + fill.Blue * 30) / 100 > 128
            ? SKColors.Black : SKColors.White;
        var srcCharPx = r.Style.SrcCharPx > 0 ? r.Style.SrcCharPx : r.Style.FontSizePx;
        var scale = Math.Clamp(r.Style.FontScale * GlobalScale, 0.5f, 3.75f); // 사용자 knob × 전역배율

        if (Debug) System.Console.WriteLine(
            $"[render] {(r.Vertical?"V":"H")} box={box.Width}x{box.Height} srcCharPx={srcCharPx:0} " +
            $"scale={scale:0.00} text[{text.Length}]='{(text.Length > 12 ? text[..12] : text)}'");

        // 사용자 규칙(2026-10-26): 방향은 ReadingDir 단일 진실원 — 한국어 기본=가로 왼쪽부터 채움.
        if (r.Vertical) DrawVertical(canvas, text, box, typeface, fill, halo, srcCharPx, scale, r.ColumnRightToLeft);
        else DrawHorizontal(canvas, text, box, typeface, fill, halo, srcCharPx, scale, r.ColumnRightToLeft);
    }

    /// <summary>박스 내 배경 밝기 표본 → 밝으면 거의 검정, 어두우면 흰색 글자. (inpaint 후 채움 영역 표본)</summary>
    private static SKColor AutoTextColor(ImageData page, BoundingBox box)
    {
        long sum = 0; int n = 0;
        var x0 = Math.Max(0, box.Left); var x1 = Math.Min(page.Width, box.Right);
        var y0 = Math.Max(0, box.Top); var y1 = Math.Min(page.Height, box.Bottom);
        for (var y = y0; y < y1; y += 4)
        {
            var row = page.Row(y);
            for (var x = x0; x < x1; x += 4)
            {
                var o = x * 4; // BGRA
                sum += (row[o] * 11 + row[o + 1] * 59 + row[o + 2] * 30) / 100;
                n++;
            }
        }
        var lum = n > 0 ? (double)sum / n : 255.0;
        return lum > 140 ? new SKColor(26, 26, 26) : SKColors.White;
    }

    // ---------- 가로: grow-until-fit, 방향 정렬(좌 기준=한국 기본 / 우=rare) ----------
    private void DrawHorizontal(SKCanvas canvas, string text, BoundingBox box,
        SKTypeface tf, SKColor fill, SKColor halo, float srcCharPx, float scale, bool rightAlign)
    {
        // 사용자 피드백: 글자가 너무 작다 → 원본크기에서 출발해 박스에 들어가는 만큼 계속 키운다
        var fs = Math.Clamp((srcCharPx > 0 ? srcCharPx : box.Height * 0.75f) * 0.95f * scale, 11f, 220f);
        using var paint = new SKPaint { Typeface = tf, IsAntialias = true, TextEncoding = SKTextEncoding.Utf16 };

        List<string> lines = new();
        while (fs < 220f)
        {
            paint.TextSize = fs * 1.06f;
            var probe = Wrap(text, paint, box.Width);
            if (probe.Count * fs * 1.06f * LineHeight > box.Height + 2) break;
            fs *= 1.06f;
        }
        paint.TextSize = fs;
        // halo stroke가 glyph 좌우로 fs*OutlineWidth/2씩 확장 → fit 여유에서 차감(박스 월침 방지)
        float Halo() => OutlineWidth > 0f ? paint.TextSize * OutlineWidth * 0.5f : 0f;
        lines = Wrap(text, paint, (int)(box.Width - Halo()));
        while (lines.Count * fs * LineHeight > box.Height + 2 && fs > 9)
        {
            fs *= 0.94f; paint.TextSize = fs;
            lines = Wrap(text, paint, (int)(box.Width - Halo()));
        }
        // floor(마지노선)는 말풍선 안에 들어올 때만 적용 — 밖으로 넘치면 적용하지 않는다(사용자 피드백).
        var floorH = EffMin(MinFontSizePx, scale);
        if (fs < floorH)
        {
            paint.TextSize = floorH;
            var probe = Wrap(text, paint, (int)(box.Width + floorH));
            if (probe.Count * floorH * LineHeight <= box.Height * 1.12f)
            { fs = floorH; lines = probe; }
        }

        var lh = fs * LineHeight;
        var y = box.Top + (box.Height - lines.Count * lh) / 2f + fs * 0.82f;
        foreach (var line in lines)
        {
            var w = paint.MeasureText(line);
            // 왼쪽부터 채움(사용자 2026-10-26) — centering 폐기. 좌측 halo 여유 확보.
            var x = rightAlign ? box.Right - w - Halo() : box.Left + Halo();
            if (OutlineWidth > 0f)
            {
                paint.Style = SKPaintStyle.Stroke; paint.StrokeJoin = SKStrokeJoin.Round;
                paint.StrokeWidth = Math.Max(1.5f, fs * OutlineWidth); paint.Color = halo;
                canvas.DrawText(line, x, y, paint);
            }
            paint.Style = SKPaintStyle.Fill; paint.Color = fill;
            canvas.DrawText(line, x, y, paint);
            y += lh;
        }
    }

    private static List<string> Wrap(string text, SKPaint paint, int maxW)
    {
        var lines = new List<string>();
        var cur = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            cur.Append(ch);
            if (paint.MeasureText(cur.ToString()) > maxW && cur.Length > 1)
            {
                cur.Length--;
                lines.Add(cur.ToString());
                cur.Clear(); cur.Append(ch);
            }
        }
        if (cur.Length > 0) lines.Add(cur.ToString());
        return lines;
    }

    // ---------- 세로: 열 방향(우→좌 만화 관행 / 좌→우 한국식) grow-until-fit ----------
    private void DrawVertical(SKCanvas canvas, string text, BoundingBox box,
        SKTypeface tf, SKColor fill, SKColor halo, float srcCharPx, float scale, bool rightToLeft)
    {
        // 원본 크기 출발 growth search: fs 키우며 (열 width, chars per열) 가 박스 안에 드는 최대.
        var len = Math.Max(1, text.Length);
        var fs = Math.Clamp((srcCharPx > 0 ? srcCharPx : Math.Min(box.Width, box.Height) * 0.7f) * 0.95f * scale, 11f, 220f);
        using var paint = new SKPaint { Typeface = tf, IsAntialias = true, TextEncoding = SKTextEncoding.Utf16 };

        List<string> ColsAt(float f)
        {
            var nCols = Math.Max(1, (int)(box.Width / (f * ColGap)));
            var perCol = Math.Max(1, (int)Math.Ceiling((double)len / nCols));
            var cols = new List<string>();
            for (var i = 0; i < len; i += perCol)
                cols.Add(text.Substring(i, Math.Min(perCol, len - i)));
            return cols;
        }
        // 세로 여유: 2026-10-25 사용자 피드백 — 한글이 감지 사각형보다 크면 안 된다 → strict-fit(박스+2px만 허용).
        // halo stroke 확장분(f*OutlineWidth/2)도 여유에서 차감.
        var slackH = box.Height + 2f;
        float Ov(float f) => OutlineWidth > 0f ? f * OutlineWidth * 0.5f : 0f;
        bool Fits(List<string> cols, float f) =>
            cols.Max(c => c.Length) * f * VCharGap <= slackH - Ov(f) &&
            cols.Count * f * ColGap <= box.Width * WidthSlack + 2 - Ov(f) * 2f;

        while (fs < 220f && Fits(ColsAt(fs * 1.06f), fs * 1.06f)) fs *= 1.06f;

        var gc = ColsAt(fs);
        while (!Fits(gc, fs) && fs > 9)
        {
            fs *= 0.94f;
            gc = ColsAt(fs);
        }
        // floor도 Fits 통과(말풍선 안)일 때만 적용 — 넘치면 fit 크기 유지
        var floorFs = EffMin(MinFontSizePx, scale);
        if (fs < floorFs)
        {
            var g2 = ColsAt(floorFs);
            if (Fits(g2, floorFs)) { fs = floorFs; gc = g2; }
        }
        paint.TextSize = fs; // floor bump 후 paint 동기화 유지
        var cols = gc;

        // 첫 열 = 우(RTL 만화)/좌(LTR) 시작, halo 확장분 안쪽으로
        var colX = rightToLeft ? box.Right - fs * 0.5f - Ov(fs) : box.Left + fs * 0.5f + Ov(fs);
        var step = rightToLeft ? -fs * ColGap : fs * ColGap;
        foreach (var col in cols)
        {
            var y = box.Top + fs * 0.82f + Ov(fs);
            foreach (var ch in col)
            {
                var s = ch.ToString();
                var w = paint.MeasureText(s);
                var x = colX - w / 2f;
                if (OutlineWidth > 0f)
                {
                    paint.Style = SKPaintStyle.Stroke; paint.StrokeJoin = SKStrokeJoin.Round;
                    paint.StrokeWidth = Math.Max(1.5f, fs * OutlineWidth); paint.Color = halo;
                    canvas.DrawText(s, x, y, paint);
                }
                paint.Style = SKPaintStyle.Fill; paint.Color = fill;
                canvas.DrawText(s, x, y, paint);
                y += fs * VCharGap;   // 긴밀: fs*VCharGap(기본 1.0em)
            }
            colX += step;             // 열 스텝(방향부호)
        }
    }

    // 전각→반각 정규화는 Core.TextNormalizer.ToHalfWidth로 통합(파이프라인/렌더 공유).
    private static string NormalizeText(string s) => TextNormalizer.ToHalfWidth(s);

    public void Dispose()
    {
        _regular?.Dispose();
        _bold?.Dispose();
    }
}
