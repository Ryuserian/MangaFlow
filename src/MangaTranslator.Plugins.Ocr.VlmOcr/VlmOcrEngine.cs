using MangaTranslator.Core;
using MangaTranslator.Core.Imaging;
using MangaTranslator.Core.Interfaces;
using MangaTranslator.Plugins.Ocr.PpOcr;

namespace MangaTranslator.Plugins.Ocr.VlmOcr;

/// <summary>
/// OCR 엔진 — PP-OCR det(말풍선·열 구조 분석) + VLM rec(Qwen3-VL-30B-A3B) 조합.
/// det가 만든 박스(세로 다중열 union 머지)를 VLM에 넘겨 원본 방향으로 판독한다.
///
/// v6 (2026-10-26): manga-ocr(ViT+BERT ONNX) rec 폴백 제거 — VLM이 유일한 rec.
/// sample/before·00020 실측에서 manga-ocr 출력은 전부 garbage(sim≈0)였고
/// VLM coverage 100%라 폴백이 사실상 죽은 코드. 폴백 제거로 ONNX 모델 3종(≈300MB)·
/// squashing split·회전 양방향 rec 헬퍼 전체가 사라져 파이프라인이 얇아짐.
/// det는 PpOcrEngine이 그대로 보유(재사용). 디렉토리/클래스명도 실체와 일치하게 재편
/// (구 MangaTranslator.Plugins.Ocr.MangaOcr.MangaOcrEngine → VlmOcr.VlmOcrEngine).
/// </summary>
public sealed class VlmOcrEngine : IOcrEngine, ILoadableEngine, IUnloadable, IDisposable
{
    public string Name => "vlm_ocr";

    private readonly PpOcrEngine _det;

    /// <summary>세로 열 머지: 수평 간격 ≤ ratio×열너비 이면 같은 말풍선으로 결합.</summary>
    public float MergeGapRatio { get; set; } = 0.8f;
    public bool MergeVerticalColumns { get; set; } = true;

    /// <summary>rec 전 crop 외곽 확장 px — 획 잘림 보완. env MO_RECPAD=n.</summary>
    public int RecPadPx { get; set; }

    /// <summary>rec VLM(Qwen3-VL). null이면 인식 불가(명시 에러).</summary>
    public IVlmRecEngine? VlmRec { get; set; }
    /// <summary>true면 Dispose가 VlmRec도 종료(native leak 방지).</summary>
    public bool OwnsVlm { get; set; }

    /// <summary>VLM crop 추가 패딩 px — det 파편화(열 잘림) 컨텍스트 보완(00015 r03/r04 실측).</summary>
    public int VlmExtraPad { get; set; } = 8;

    public VlmOcrEngine(string detOnnx, string recOnnx, string dictPath)
    {
        var padEnv = Environment.GetEnvironmentVariable("MO_RECPAD");
        RecPadPx = padEnv != null && int.TryParse(padEnv, out var pe) ? Math.Clamp(pe, 0, 32) : 4;
        // det는 PP-OCR 전적으로 재사용(recOnnx/dictPath는 PpOcrEngine ctor 요구 — det-only passes null-safe)
        _det = new PpOcrEngine(detOnnx, recOnnx, dictPath);
    }

    /// <summary>내장 VLM(Qwen3-VL GGUF) 확정 로드 — 스플래시 MEM_KEEP=1 시 호출. lazy가 기본.</summary>
    public Task EnsureLoadedAsync(CancellationToken ct = default) =>
        VlmRec is ILoadableEngine le ? le.EnsureLoadedAsync(ct) : Task.CompletedTask;

    /// <summary>OCR 스테이지 종료 시 내장 VLM GGUF(Qwen3-VL 17GB)를 내려 VRAM을 해방한다
    /// (순차 상재). det ONNX 세션은 가볍게 유지하고 다음 rec 시 VLM lazy 재로드.</summary>
    public bool IsLoaded => VlmRec is IUnloadable u && u.IsLoaded;

    public void Unload()
    {
        if (VlmRec is IUnloadable u) u.Unload();
    }

    public bool SupportsLanguage(string langCode) => langCode is "ja" or "zh" or "en" or "ch";

    public Task<IReadOnlyList<TextRegion>> RecognizeAsync(ImageData page, CancellationToken ct = default)
        => Task.Run(() => Recognize(page, ct), ct);

    private IReadOnlyList<TextRegion> Recognize(ImageData page, CancellationToken ct)
    {
        if (VlmRec == null)
            throw new InvalidOperationException(
                "rec VLM 미연결 — Ocr.Qwen3-VL GGUF/models 배치 후 재시작");

        var boxes = _det.DetectBoxes(page);
        var groups = MergeVerticalColumns ? GroupColumns(boxes) : boxes.Select(b => (b.Box, b.Score, 1));

        var regions = new List<TextRegion>();
        foreach (var (rect, score, cols) in groups)
        {
            ct.ThrowIfCancellationRequested();
            if (rect.Width < 6 || rect.Height < 6) continue;
            var (text, vertical, colsEff) = RecognizeVlm(page, rect, cols);
            if (!HasCjk(text)) continue; // 페이지번호·점잡음 드롭
            // 원본 글자 크기 = 세로:열너비/열수 / 가로:박스높이 — 재조판 시 동일 덩어리감 유지
            float srcChar = vertical ? rect.Width / (float)colsEff : rect.Height;
            regions.Add(new TextRegion
            {
                Box = rect, Text = text, Vertical = vertical,
                Confidence = 0.8f, // VLM fixed confidence (ground-truth급 실측)
                Style = new TextStyle
                {
                    FontSizePx = Math.Clamp(srcChar * 0.92f, 12, 128),
                    SrcCharPx = Math.Clamp(srcChar, 10, 160),
                    SrcCols = colsEff,
                },
            });
        }
        return SuppressOverlaps(regions)
            .OrderBy(r => r.Box.Top / 24)
            .ThenByDescending(r => r.Box.Left)
            .ToList();
    }

    /// <summary>박스 하나만 VLM 재판독(GUI 🔄 버튼). pad+extra crop → 네이티브 방향 판독.</summary>
    public (string text, double score, bool vertical) RecognizeBoxAsync(ImageData page, BoundingBox rect)
    {
        var (text, vertical, _) = RecognizeVlm(page, rect, 1);
        return (text, HasCjk(text) ? 0.6 : -0.9, vertical); // 0.6→conf 0.8 스케일
    }

    /// <summary>박스 → pad+extra crop → VLM 네이티브 방향 판독(회전·split 불요).
    /// 세로 박스는 wide crop(주변 컨텍스트)이 잘림 보완에 유리.</summary>
    private (string text, bool vertical, int cols) RecognizeVlm(ImageData page, BoundingBox rect, int cols)
    {
        bool vertical = rect.Height > rect.Width * 1.3f;
        var padBox = new BoundingBox(rect.Left - RecPadPx, rect.Top - RecPadPx,
                                      rect.Right + RecPadPx, rect.Bottom + RecPadPx);
        var wideBox = new BoundingBox(padBox.Left - VlmExtraPad, padBox.Top - VlmExtraPad,
                                       padBox.Right + VlmExtraPad, padBox.Bottom + VlmExtraPad);
        var wide = page.Crop(wideBox);
        var vt = VlmRec!.RecognizeCropAsync(wide).GetAwaiter().GetResult();
        return (vt.Replace("\n", ""), vertical, Math.Max(1, cols));
    }

    /// <summary>
    /// 겹치는 검출 박스 제거(사용자 규칙 2026-10-25):
    ///  - 큰 박스가 대체로 더 많은 대사를 담고, 신뢰도가 기준. 승자(고신뢰→대구간)를 기준으로
    ///    50% 이상 contained 된 서브 박스·IoU≥0.30 중복 박스는 드롭.
    ///  - 서브 박스 텍스트가 승자 텍스트에 통째로 포함되면 그 자체로 중복.(p_144 r04/r05 실측 쌍)
    /// </summary>
    private static List<TextRegion> SuppressOverlaps(List<TextRegion> regions)
    {
        var byRank = regions
            .OrderByDescending(r => r.Confidence)
            .ThenByDescending(r => (long)r.Box.Width * r.Box.Height)
            .ToList();
        var keep = new List<TextRegion>();
        foreach (var r in byRank)
        {
            bool drop = false;
            foreach (var k in keep)
            {
                var iw = InterWidth(r.Box, k.Box);
                var ih = InterHeight(r.Box, k.Box);
                if (iw <= 0 || ih <= 0) continue;
                var inter = (long)iw * ih;
                var aR = (long)r.Box.Width * r.Box.Height;
                var aK = (long)k.Box.Width * k.Box.Height;
                var contR = (double)inter / Math.Max(1, aR); // r이 k에 얼마나 contained
                var union = aR + aK - inter;
                var iou = (double)inter / Math.Max(1, union);
                // 승자=k가 확정적(고신뢰) — r이 절반 이상 contained거나 상당 중복이면 제거
                if (contR >= 0.5 || iou >= 0.3)
                {
                    drop = true;
                    break;
                }
                // 낮은 duplicate but text 완전 포함 → 텍스트가 실질 증거
                var tR = r.Text.Replace("\n", "");
                var tK = k.Text.Replace("\n", "");
                if (tR.Length >= 2 && tK.Contains(tR, StringComparison.Ordinal))
                {
                    drop = true;
                    break;
                }
            }
            if (!drop) keep.Add(r);
        }
        return keep;
    }

    private static int InterWidth(BoundingBox a, BoundingBox b)
        => Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);

    private static int InterHeight(BoundingBox a, BoundingBox b)
        => Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);

    /// <summary>det 박스만 노출(rec 검증용).</summary>
    public IReadOnlyList<PpOcrEngine.OcrDetBox> DetectOnly(ImageData page) => _det.DetectBoxes(page);

    // ---------- 세로 열 머지 (det 후처리 — 유지) ----------

    private IEnumerable<(BoundingBox, float, int)> GroupColumns(IReadOnlyList<PpOcrEngine.OcrDetBox> boxes)
    {
        // ① 중복/파편 union: 교집합 ≥ min박스 50%면 하나로 합침(한 열이 det에서 잘려 나가는 00015 r03/r04 패턴)
        var unioned = UnionOverlap(boxes).ToList();

        var vert = unioned.Where(b => b.Box.Height > b.Box.Width * 1.5f).OrderBy(b => b.Box.Top).ToList();
        var flat = unioned.Where(b => b.Box.Height <= b.Box.Width * 1.5f).Select(b => (b.Box, b.Score, 1));
        if (vert.Count == 0) return flat;

        // y-overlapping 밴드 클러스터
        var clusters = new List<List<PpOcrEngine.OcrDetBox>>();
        foreach (var b in vert)
        {
            var cl = clusters.FirstOrDefault(c =>
                c.Any(k => OverlapFrac(k.Box, b.Box) > 0.4f));
            if (cl == null) clusters.Add(new List<PpOcrEngine.OcrDetBox> { b });
            else { cl.Add(b); MergeBands(clusters, cl); }
        }

        var result = flat.ToList();
        foreach (var cl in clusters)
        foreach (var group in AdjacentRuns(cl))
        {
            var box = group[0].Box;
            foreach (var g in group.Skip(1))
                box = new BoundingBox(Math.Min(box.Left, g.Box.Left), Math.Min(box.Top, g.Box.Top),
                    Math.Max(box.Right, g.Box.Right), Math.Max(box.Bottom, g.Box.Bottom));
            result.Add((box, group.Min(g => g.Score), group.Count)); // 열수 기록→원본字号 복원
        }

        // ② column-run 결과와 flat 박스가 서로 겹칠 수 있다 — 최종 union 패스
        var merged = UnionOverlap(result.Select(x => new PpOcrEngine.OcrDetBox(x.Box, x.Score)).ToList());
        return merged.Select(b => (b.Box, b.Score, 1));
    }

    /// <summary>교집합 ≥ min(박스) 면적×0.5인 박스들을 union으로 반복 병합(고정점).</summary>
    private static IEnumerable<PpOcrEngine.OcrDetBox> UnionOverlap(IReadOnlyList<PpOcrEngine.OcrDetBox> boxes)
    {
        var list = boxes.ToList();
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < list.Count && !changed; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    var a = list[i].Box; var b = list[j].Box;
                    int ix = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
                    int iy = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
                    if (ix <= 0 || iy <= 0) continue;
                    if ((double)ix * iy >= 0.5 * Math.Min(a.Area, b.Area))
                    {
                        var u = new BoundingBox(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top),
                                               Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
                        list[i] = new PpOcrEngine.OcrDetBox(u, Math.Max(list[i].Score, list[j].Score));
                        list.RemoveAt(j);
                        changed = true;
                        break;
                    }
                }
        }
        return list;
    }

    private static void MergeBands(List<List<PpOcrEngine.OcrDetBox>> clusters, List<PpOcrEngine.OcrDetBox> touched)
    {
        // 밴드가 서로 overlaps면 하나로 흡수
        for (int i = clusters.Count - 1; i >= 0; i--)
        {
            var c = clusters[i];
            if (c == touched) continue;
            if (c.Any(k => touched.Any(t => OverlapFrac(k.Box, t.Box) > 0.4f)))
            { touched.AddRange(c); clusters.RemoveAt(i); }
        }
    }

    /// <summary>밴드 내 열들을 x 내림차순(우→좌) 정렬 후 인접 gap≤ratio×너비 런으로 묶음.</summary>
    private List<List<PpOcrEngine.OcrDetBox>> AdjacentRuns(List<PpOcrEngine.OcrDetBox> cols)
    {
        var sorted = cols.OrderByDescending(c => c.Box.Left).ToList();
        var runs = new List<List<PpOcrEngine.OcrDetBox>>();
        foreach (var c in sorted)
        {
            var run = runs.LastOrDefault();
            if (run != null)
            {
                var last = run[^1];
                double gap = last.Box.Left - c.Box.Right;
                double width = (last.Box.Width + c.Box.Width) / 2.0;
                if (gap <= MergeGapRatio * width + 2 && OverlapFrac(last.Box, c.Box) > 0.5f)
                { run.Add(c); continue; }
            }
            runs.Add(new List<PpOcrEngine.OcrDetBox> { c });
        }
        return runs;
    }

    private static double OverlapFrac(BoundingBox a, BoundingBox b)
    {
        int iy = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (iy <= 0) return 0;
        double minH = Math.Min(a.Height, b.Height);
        return iy / minH;
    }

    private static bool HasCjk(string s)
    {
        foreach (var c in s)
            if ((c >= '\u3040' && c <= '\u30ff') || (c >= '\u4e00' && c <= '\u9fff')) return true;
        return false;
    }

    public void Dispose()
    {
        _det.Dispose();
        if (OwnsVlm) VlmRec?.Dispose();
    }
}
