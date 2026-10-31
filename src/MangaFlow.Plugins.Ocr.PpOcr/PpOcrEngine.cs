using MangaFlow.Core;
using MangaFlow.Core.Imaging;
using MangaFlow.Core.Interfaces;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MangaFlow.Plugins.Ocr.PpOcr;

/// <summary>
/// PP-OCRv4(ch) det+rec ONNX 백엔드 — **native OpenCV 없이 순수 C# postprocess**.
/// (Linux/Android/Windows 전 플랫폼 동일 동작, libtesseract/ffmpeg SONAME 지옥 제거)
/// det: bilinear resize(long≤960,/32) → DB prob → threshold+dilate → 8경로 connected-components → score
/// rec: bbox crop(bilinear, H=48) / 세로는 CW·CCW transpose 중 conf 높은 쪽 → greedy CTC(blank=마지막 idx)
/// reading order: manga 기본 — 행 밴드(y asc) → 행내 x desc(우→좌). 사용자 편집으로 보정 가능.
/// </summary>
public sealed class PpOcrEngine : IOcrEngine, IDisposable
{
    public string Name => "ppocr";

    private const int DetMaxSide = 1280;   // limit_type=min: 저해상도는 이까지 upscale(얇은 손글씨 recall)
    private const float DetThresh = 0.2f;
    private const float BoxThresh = 0.6f;
    /// <summary>prob dilation 반지름 — 글자↔글자 연결(단어/문합). DB unclip 근사치.</summary>
    private const int DilateRadius = 5;
    private const int RecH = 48;
    private const int RecMaxW = 320;
    private const int MinArea = 90;      // prob 최소 유효 화소 수
    private const int MinSide = 8;

    private readonly InferenceSession _det;
    private readonly InferenceSession? _rec;      // null = det-only 모드(파이프라인 기본 — rec는 VLM/manga 담당)
    private readonly string[] _labels;             // blank = _labels.Length
    private readonly string _detInput, _recInput;

    /// <summary>세로쓰기 판정 기준(aspect). dev tuning용 public.</summary>
    public float VerticalAspect { get; set; } = 1.5f;

    /// <summary>rec 입력 letterbox(aspect 유지+흰 패딩). stretch(구동작) 대비 글자 변형 제거. env PP_LETTERBOX=0 비교.</summary>
    public bool RecLetterbox { get; set; }
    /// <summary>rec crop 외곽 확장 px — 잘린 획/컨텍스트 보완. env PP_RECPAD=n (기본 8).</summary>
    public int RecPadPx { get; set; }
    /// <summary>흰 외곽선(halo) 성장: det 박스 바깥으로 흰 띠가 반복되면 그 외곽(비흰 배경 시작)까지 박스를 확장.
    /// 만화 텍스트 = 색채움+흰 outline 구조 → 꽉 잡힌 박스는 outline 잘림(inpaint 잔상). env PP_GROW=n(최대성장px, 0=off, 기본 18).</summary>
    public int HaloGrowPx { get; set; } = 18;
    /// <summary>det 최종 박스 균등 외연(px, env PP_PAD). halo 성장이 outline 없는
    /// 손글씨에서 말풍선 경계까지 못 감 → pad로 여유 확보(재조판 큰 글자 장소·잔획 포함).
    /// 짧은 변의 40% 상한 가드. 기본 10.</summary>
    public int PadBoxPx { get; set; }
    /// <summary>흰 픽셀 판정 threshold(min 채널).</summary>
    private const int WhiteTh = 228;
    /// <summary>행/열의 흰 비율 — outline 행은 거의 전부 흰 (>0.72).</summary>
    private const int HaloWhiteRatio = 72; // percent*

    /// <summary>
    /// 파스텔/컬러 손글씨 잡아주려고 det 입력을 min(R,G,B) 그레이로 교체.
    /// 흰 배경(255)은 흰 그대로, 분홍(255,200,210)→200으로 진해짐. rec는 컬러 유지.
    /// </summary>
    public bool EnhanceColorText { get; set; } = true;

    /// <summary>det-only: recOnnxPath/dictPath가 없으면 rec 미로드 — DetectBoxes만 동작(기본 파이프라인).
    /// rec 사용(standalone CLI·PP_REC_MODEL) 시에는 둘 다 있어야 한다.</summary>
    public PpOcrEngine(string detOnnxPath, string? recOnnxPath = null, string? dictPath = null)
    {
        if (!File.Exists(detOnnxPath)) throw new FileNotFoundException("det onnx", detOnnxPath);
        var hasRec = recOnnxPath != null && dictPath != null
            && File.Exists(recOnnxPath) && File.Exists(dictPath);
        // 실측 recbench: letterbox CJK hit 4/16 vs stretch 14/16 → stretch 기본 유지. env PP_LETTERBOX=1로 실험
        RecLetterbox = Environment.GetEnvironmentVariable("PP_LETTERBOX") == "1";
        var growEnv = Environment.GetEnvironmentVariable("PP_GROW");
        HaloGrowPx = growEnv != null && int.TryParse(growEnv, out var ge) ? Math.Clamp(ge, 0, 48) : 18;
        var padBoxEnv = Environment.GetEnvironmentVariable("PP_PAD");
        PadBoxPx = padBoxEnv != null && int.TryParse(padBoxEnv, out var pbe) ? Math.Clamp(pbe, 0, 40) : 10;
        var padEnv = Environment.GetEnvironmentVariable("PP_RECPAD");
        RecPadPx = padEnv != null && int.TryParse(padEnv, out var pe) ? Math.Clamp(pe, 0, 32) : 8;

        // GPU 우선(CUDA)·없으면/실패 시 CPU 폴백 — 세션 생성 자체까지 fallback(크래시 방지)
        _det = OnnxSessionFactory.CreateWithCpuFallback(detOnnxPath, cuda: true);
        _detInput = _det.InputNames.First();
        if (hasRec)
        {
            _rec = OnnxSessionFactory.CreateWithCpuFallback(recOnnxPath!, cuda: true);
            _recInput = _rec.InputNames.First();
            _labels = File.ReadAllLines(dictPath!, System.Text.Encoding.UTF8);
        }
        else { _rec = null; _recInput = ""; _labels = Array.Empty<string>(); }
    }

    /// <summary>rec 미탑재(det-only) 모드에서 rec 호출 시 명시적 오류.</summary>
    private void EnsureRec()
    {
        if (_rec == null) throw new InvalidOperationException(
            "PP-OCR rec 모델 없음(det-only 모드) — rec는 Qwen3-VL/manga-ocr이 담당. " +
            "standalone PP rec가 필요하면 --getmodel ocr.ppocr.rec");
    }

    public bool SupportsLanguage(string langCode) => langCode is "ja" or "zh" or "en" or "ch";

    public Task<IReadOnlyList<TextRegion>> RecognizeAsync(ImageData page, CancellationToken ct = default)
        => Task.Run(() => Recognize(page, ct), ct);

    private IReadOnlyList<TextRegion> Recognize(ImageData page, CancellationToken ct)
    {
        EnsureRec();
        var boxes = Detect(page);
        var regions = new List<TextRegion>(boxes.Count);
        foreach (var b in boxes)
        {
            ct.ThrowIfCancellationRequested();
            var (text, conf, vertical) = RecognizeCrop(page, b.Box);
            if (string.IsNullOrWhiteSpace(text) && RecPadPx > 0) // 잘림 의심: pad 확장 재판독
                (text, conf, vertical) = RecognizeCrop(page, new BoundingBox(
                    b.Box.Left - RecPadPx, b.Box.Top - RecPadPx,
                    b.Box.Right + RecPadPx, b.Box.Bottom + RecPadPx));
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!ContainsCjk(text)) continue; // 페이지번호·점·라틴 잡음 드롭(번역 소스 오염 방지)
            regions.Add(new TextRegion
            {
                Box = b.Box, Text = text, Vertical = vertical, Confidence = conf,
                Style = new TextStyle { FontSizePx = Math.Clamp(b.Box.Height * 0.8f, 12, 128) },
            });
        }
        return regions
            .OrderBy(r => r.Box.Top / 24)
            .ThenByDescending(r => r.Box.Left)
            .ToList();
    }

    /// <summary>일본어 원문 가드: 히라가나/가타카ana/한자 1자 이상.</summary>
    private static bool ContainsCjk(string s)
    {
        foreach (var c in s)
            if ((c >= '぀' && c <= 'ヿ') || (c >= '一' && c <= '鿿')) return true;
        return false;
    }

    // ---------- det ----------
    /// <summary>det 후보 박스(page 좌표). VlmOcr 등 det-소비 엔진이 재사용한다.</summary>
    public readonly record struct OcrDetBox(BoundingBox Box, float Score);

    /// <summary>det 박스 목록 노출(rec 교체composed 엔진용).</summary>
    public IReadOnlyList<OcrDetBox> DetectBoxes(ImageData page) =>
        Detect(page).ConvertAll(b => new OcrDetBox(b.Box, b.Score));

    private readonly record struct DetBox(BoundingBox Box, float Score);

    /// <summary>dev: prob맵(0~1)을 그레이 ImageData로 반환 — 디버깅 전용.</summary>
    public ImageData DebugProbMap(ImageData src)
    {
        var chw = BuildDetInput(src, allowUpscale: true);
        using var results = _det.Run(new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_detInput, chw) });
        var t = results.First().AsTensor<float>();
        int oh = t.Dimensions[^2], ow = t.Dimensions[^1];
        var img = new ImageData(ow, oh);
        var buf = t.ToArray();
        for (int i = 0; i < oh * ow; i++)
        {
            byte g = (byte)(Math.Clamp(buf[i], 0f, 1f) * 255f);
            img.Bgra[i * 4] = img.Bgra[i * 4 + 1] = img.Bgra[i * 4 + 2] = g;
            img.Bgra[i * 4 + 3] = 255;
        }
        return img;
    }

    /// <summary>저해상도 페이지 upscale 상한(×). 타일은 native 유지.</summary>
    public const double DetMaxUpscale = 1.5;

    private DenseTensor<float> BuildDetInput(ImageData src, bool allowUpscale = false)
    {
        int h = src.Height, w = src.Width;
        double ratio = (double)DetMaxSide / Math.Max(h, w);
        if (!allowUpscale) ratio = Math.Min(1.0, ratio); // 타일은 native — 768→1280 upscale은 비용 낭비
        else ratio = Math.Min(ratio, DetMaxUpscale);
        int rh = Math.Max(32, (int)(h * ratio / 32 + 0.5) * 32);
        int rw = Math.Max(32, (int)(w * ratio / 32 + 0.5) * 32);
        var chw = new DenseTensor<float>(new[] { 1, 3, rh, rw });
        FillDetSpan(src, chw, rh, rw);
        return chw;
    }

    private void FillDetSpan(ImageData src, DenseTensor<float> chw, int rh, int rw)
    {
        int h = src.Height, w = src.Width;
        var span = chw.Buffer.Span;
        int plane = rh * rw;
        // bilinear resample + ImageNet normalize(EnhanceColorText면 min-ch 그레이) → chw float
        for (int y = 0; y < rh; y++)
        {
            double sy = Math.Min(h - 1, y * h / (double)rh);
            int y0 = (int)sy, y1 = Math.Min(h - 1, y0 + 1);
            double fy = sy - y0;
            var r0 = src.Row(y0); var r1 = src.Row(y1);
            for (int x = 0; x < rw; x++)
            {
                double sx = Math.Min(w - 1, x * w / (double)rw);
                int x0 = (int)sx, x1 = Math.Min(w - 1, x0 + 1);
                double fx = sx - x0;
                int o0 = x0 * 4, o1 = x1 * 4;
                double wx1 = 1 - fx, wy1 = 1 - fy;
                if (EnhanceColorText)
                {
                    double mn = double.MaxValue;
                    for (int k = 0; k < 3; k++)
                    {
                        int ch = 2 - k;
                        double v =
                            (r0[o0 + ch] * wx1 + r0[o1 + ch] * fx) * wy1 +
                            (r1[o0 + ch] * wx1 + r1[o1 + ch] * fx) * fy;
                        if (v < mn) mn = v;
                    }
                    // 콘트라스트 스트레치: 파스텔 손글씨(200~230)를 먹 text 수준(60~120)로 심화.
                    // 흰 배경(≥Lo)은 흰 유지 → det prob가 말풍선 안 필기에서 활발히 반응.
                    const double Lo = 165, Hi = 250;
                    double g = mn <= Lo ? 0 : mn >= Hi ? 255 : (mn - Lo) * 255.0 / (Hi - Lo);
                    // PP-OCR det 규격 정규화 = (x/255−0.5)/0.5 (det 백본은 ImageNet이 아님 — 0.485 쓰면 prob 붕괴)
                    for (int c = 0; c < 3; c++)
                        span[c * plane + y * rw + x] = (float)((g / 255.0 - 0.5) / 0.5);
                }
                else
                {
                    for (int c = 0; c < 3; c++)
                    {
                        int ch = 2 - c; // chw[0]=R
                        double v =
                            (r0[o0 + ch] * wx1 + r0[o1 + ch] * fx) * wy1 +
                            (r1[o0 + ch] * wx1 + r1[o1 + ch] * fx) * fy;
                        span[c * plane + y * rw + x] = (float)((v / 255.0 - 0.5) / 0.5);
                    }
                }
            }
        }
    }

    /// <summary>
    /// multi-scale tiling — 사용자 제안 "작은 타일先从(선행)→큰 타일".
    /// 작은 타일(512)은 native crop 후 det 입력으로 최대 1.5× upscale → 얇은 손글씨 픽셀 ratio 극대화(핵심 recall 레버),
    /// 이어서 큰 타일(768, native)이 타일 경계 spanning 텍스트를 회수. 스케일 간 IoU merge로 중복 제거.
    /// env PP_DET_SCALES="512,768" 로 스케일 조정, "legacy"면 과거 단일-스케일 동작.
    /// </summary>
    private int[]? _detScales;

    private int[] ResolveDetScales()
    {
        if (_detScales != null) return _detScales;
        var env = Environment.GetEnvironmentVariable("PP_DET_SCALES");
        if (string.Equals(env, "legacy", StringComparison.OrdinalIgnoreCase))
            _detScales = Array.Empty<int>();
        else if (!string.IsNullOrWhiteSpace(env))
            _detScales = env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(int.Parse).Where(s => s is >= 128 and <= 1280).OrderBy(s => s).Distinct().ToArray();
        else
            _detScales = new[] { 512, 768 };
        return _detScales;
    }

    /// <summary>overlap multi-scale 타일 → 좌표복원 → containment/IoU union merge(고정점까지 반복).
    /// - IoU>0.45 (중복) 또는 소박스의 72%+가 대박스에 contained → union 확장.
    ///   삭제식 merge는 고득점 단편이 저득점 전체 박스를 죽여 recall 손실(00020 실측 7→5),
    ///   순수 union-only는 단편이 개별 잔존해 중복インスタ스 살포(0015 실측) → 둘 다 잡는다.
    /// - 고정점 반복: 스케일 다른 파편 사슬(A~B, B~C)도 하나의 말풍선으로 수렴.</summary>
    private List<DetBox> Detect(ImageData page)
    {
        var scales = ResolveDetScales();
        var kept = scales.Length == 0
            ? DetectLegacy(page)
            : DetectMultiScale(page, scales);

        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < kept.Count && !changed; i++)
                for (int j = i + 1; j < kept.Count; j++)
                {
                    if (!ShouldMerge(kept[i].Box, kept[j].Box)) continue;
                    var u = new BoundingBox(
                        Math.Min(kept[i].Box.Left, kept[j].Box.Left), Math.Min(kept[i].Box.Top, kept[j].Box.Top),
                        Math.Max(kept[i].Box.Right, kept[j].Box.Right), Math.Max(kept[i].Box.Bottom, kept[j].Box.Bottom))
                        .Clamp(page.Width, page.Height);
                    kept[i] = new DetBox(u, Math.Max(kept[i].Score, kept[j].Score));
                    kept.RemoveAt(j);
                    changed = true;
                    break;
                }
        }
        return kept.OrderByDescending(b => b.Score).ToList();
    }

    /// <summary>merge 판정: IoU 0.45 초과 overlap 또는 최소폭 박스의 72%+가 상대편에 내포(containment).</summary>
    private static bool ShouldMerge(BoundingBox a, BoundingBox b)
    {
        if (IoU(a, b) > 0.45) return true;
        int ix = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        int iy = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        double inter = (double)ix * iy;
        double minArea = Math.Min(a.Area, b.Area);
        return minArea > 0 && inter / minArea > 0.72;
    }

    private List<DetBox> DetectMultiScale(ImageData page, int[] scalesAsc)
    {
        int w = page.Width, h = page.Height;
        var all = new List<DetBox>();
        foreach (var side in scalesAsc) //Asc = 작은 타일 먼저
        {
            int step = Math.Max(64, side - Math.Max(64, side / 4)); // overlap = side/4(최소 64)
            for (int oy = 0; ; )
            {
                for (int ox = 0; ; )
                {
                    var area = new BoundingBox(ox, oy, ox + side, oy + side);
                    if (area.Width >= 32 && area.Height >= 32)
                    {
                        var tile = page.Crop(area);
                        // upscale 허용: 512타일→입력 768(max1.5×)으로 text 픽셀 ratio↑
                        foreach (var b in DetectCore(tile, ox, oy, allowUpscale: true))
                            all.Add(b);
                    }
                    if (ox + side >= w) break;
                    ox = Math.Max(0, Math.Min(ox + step, w - side));
                }
                if (oy + side >= h) break;
                oy = Math.Max(0, Math.Min(oy + step, h - side));
            }
        }
        return all;
    }

    /// <summary>과거 동작: ≤1280은 full-page upscale, 그 외 768 native 타일.</summary>
    private List<DetBox> DetectLegacy(ImageData page)
    {
        int maxSide = Math.Max(page.Width, page.Height);
        const int Tile = 768, Overlap = 128;
        if (maxSide <= 1280)
            return DetectCore(page, 0, 0, allowUpscale: true); // 720×1024→896×1280: 얇은 손글씨 recall

        var step = Tile - Overlap;
        var all = new List<DetBox>();
        for (int y = 0; y < page.Height; y += step)
        for (int x = 0; x < page.Width; x += step)
        {
            int tx = Math.Min(x, page.Width - 1);
            int ty = Math.Min(y, page.Height - 1);
            var area = new BoundingBox(tx, ty,
                Math.Min(tx + Tile, page.Width), Math.Min(ty + Tile, page.Height));
            if (area.Width < 32 || area.Height < 32) continue;
            var tile = page.Crop(area);
            foreach (var b in DetectCore(tile, area.Left, area.Top))
                all.Add(b);
            if (x + Tile >= page.Width) break;
        }
        return all;
    }

    private static double IoU(BoundingBox a, BoundingBox b)
    {
        int ix = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        int iy = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        double inter = (double)ix * iy;
        return inter <= 0 ? 0 : inter / (a.Area + b.Area - inter);
    }

    private List<DetBox> DetectCore(ImageData src, int offX, int offY, bool allowUpscale = false)
    {
        int h = src.Height, w = src.Width;
        var chw = BuildDetInput(src, allowUpscale);

        float[] prob;
        int oh, ow;
        using (var results = _det.Run(new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_detInput, chw) }))
        {
            var t = results.First().AsTensor<float>();
            var d = t.Dimensions; oh = d[^2]; ow = d[^1];
            prob = t.ToArray();
        }

        // threshold → dilate(5×5) → CC
        var bin = new byte[oh * ow];
        for (int i = 0; i < bin.Length; i++) bin[i] = prob[i] > DetThresh ? (byte)1 : (byte)0;
        var dil = Dilate(bin, ow, oh, DilateRadius);

        var boxes = new List<DetBox>();
        var visited = new bool[ow * oh];
        var stack = new Stack<int>();
        for (int i = 0; i < dil.Length; i++)
        {
            if (dil[i] == 0 || visited[i]) continue;
            visited[i] = true; stack.Push(i);
            int minX = ow, minY = oh, maxX = -1, maxY = -1, count = 0;
            double probSum = 0;
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int px = p % ow, py = p / ow;
                if (px < minX) minX = px; if (px > maxX) maxX = px;
                if (py < minY) minY = py; if (py > maxY) maxY = py;
                // score는 원 prob가 살아있던 픽셀만 — dilation으로 먹힌 배경은 평균 왜곡
                if (bin[p] != 0) { count++; probSum += prob[p]; }
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = px + dx, ny = py + dy;
                    if (nx < 0 || ny < 0 || nx >= ow || ny >= oh) continue;
                    int q = ny * ow + nx;
                    if (dil[q] != 0 && !visited[q]) { visited[q] = true; stack.Push(q); }
                }
            }

            if (count < MinArea) continue;
            float score = (float)(probSum / count);
            if (score < BoxThresh) continue;

            double scale = (double)w / ow;
            // 타일 로컬 좌표 → 흰 halo 성장(로컬) → 페이지 좌표로 오프셋 적용
            var local = new BoundingBox(
                (int)(minX * scale), (int)(minY * scale),
                (int)Math.Ceiling((maxX + 1) * scale), (int)Math.Ceiling((maxY + 1) * scale))
                .Clamp(w, h);
            local = GrowToHalo(src, local);
            // halo 확장 후 균등 pad — 말풍선 내측 여유까지 박스를 키워 재조판 공간 확보
            if (PadBoxPx > 0)
            {
                var pp = (int)Math.Min(PadBoxPx, Math.Min(local.Width, local.Height) * 0.4);
                if (pp > 0)
                    local = new BoundingBox(local.Left - pp, local.Top - pp,
                        local.Right + pp, local.Bottom + pp).Clamp(w, h);
            }
            var bbox = new BoundingBox(local.Left + offX, local.Top + offY,
                                       local.Right + offX, local.Bottom + offY)
                .Clamp(offX + w, offY + h);
            if (bbox.Width < MinSide || bbox.Height < MinSide) continue;
            boxes.Add(new DetBox(bbox, score));
        }
        return boxes;
    }

    /// <summary>박스 4방 바깥을 스캔: 연속 흰 띠(≥2행/열, 흰비율≥72%) 뒤에 비흰 배경이 오면
    /// 그 경계까지 박스 확장 = 흰 outline 통째 포함. 끝까지 흰(순백 배경)이면 확장 없음.</summary>
    private BoundingBox GrowToHalo(ImageData src, BoundingBox b)
    {
        if (HaloGrowPx <= 0) return b;
        int L = GrowEdge(src, b.Left, b.Top, b.Bottom, -1);
        int R = GrowEdge(src, b.Right, b.Top, b.Bottom, +1);
        int T = GrowEdgeY(src, b.Top, b.Left, b.Right, -1);
        int B = GrowEdgeY(src, b.Bottom, b.Left, b.Right, +1);
        return new BoundingBox(L, T, R, B).Clamp(src.Width, src.Height);
    }

    /// <summary>x0 열에서 dir 방향으로 흰 띠 주행 → 경계(비흰 열) 반환. 확장 없으면 x0.</summary>
    private int GrowEdge(ImageData src, int x0, int y0, int y1, int dir)
    {
        int run = 0;
        int max = Math.Min(HaloGrowPx, dir > 0 ? src.Width - x0 : x0);
        for (int k = 1; k <= max; k++)
        {
            int x = x0 + dir * k;
            if (x < 0 || x >= src.Width) break;
            double wf = ColWhiteFrac(src, x, y0, y1);
            if (wf * 100 >= HaloWhiteRatio) { run++; continue; }
            return run >= 2 ? (dir > 0 ? x : x + 1) : x0;
        }
        return x0;
    }

    /// <summary>y0 행에서 dir 방향으로 흰 띠 주행 → 경계 반환.</summary>
    private int GrowEdgeY(ImageData src, int y0, int x0, int x1, int dir)
    {
        int run = 0;
        int max = Math.Min(HaloGrowPx, dir > 0 ? src.Height - y0 : y0);
        for (int k = 1; k <= max; k++)
        {
            int y = y0 + dir * k;
            if (y < 0 || y >= src.Height) break;
            double wf = RowWhiteFrac(src, y, x0, x1);
            if (wf * 100 >= HaloWhiteRatio) { run++; continue; }
            return run >= 2 ? (dir > 0 ? y : y + 1) : y0;
        }
        return y0;
    }

    private static double ColWhiteFrac(ImageData src, int x, int y0, int y1)
    {
        int n = 0, w = 0;
        for (int y = Math.Max(0, y0); y < Math.Min(src.Height, y1); y++)
        {
            var row = src.Row(y);
            int o = x * 4;
            byte mn = row[o] < row[o + 1] ? row[o] : row[o + 1];
            if (row[o + 2] < mn) mn = row[o + 2];
            if (mn >= WhiteTh) n++;
            w++;
        }
        return w == 0 ? 0 : (double)n / w;
    }

    private static double RowWhiteFrac(ImageData src, int y, int x0, int x1)
    {
        int n = 0, w = 0;
        var row = src.Row(y);
        for (int x = Math.Max(0, x0); x < Math.Min(src.Width, x1); x++)
        {
            int o = x * 4;
            byte mn = row[o] < row[o + 1] ? row[o] : row[o + 1];
            if (row[o + 2] < mn) mn = row[o + 2];
            if (mn >= WhiteTh) n++;
            w++;
        }
        return w == 0 ? 0 : (double)n / w;
    }

    private static byte[] Dilate(byte[] src, int w, int h, int r)
    {
        var dst = new byte[src.Length];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            if (src[y * w + x] == 0) continue;
            for (int dy = -r; dy <= r; dy++)
            {
                int ny = y + dy; if (ny < 0 || ny >= h) continue;
                for (int dx = -r; dx <= r; dx++)
                {
                    int nx = x + dx; if (nx < 0 || nx >= w) continue;
                    dst[ny * w + nx] = 1;
                }
            }
        }
        return dst;
    }

    // ---------- rec ----------
    /// <summary>dev: rec 스텝별 argmax/라벨 dump — CTC 출력 구조 진단. chw 회색 복원 이미지도 저장.</summary>
    private void DumpSteps(ImageData crop)
    {
        DumpChw(crop);
        var (buf, T, C) = RecRun(crop);
        Console.WriteLine($"  rec out: T={T} C={C} dictLines={_labels.Length}");
        int blank = _labels.Length >= C ? C - 1 : _labels.Length;
        for (int t = 0; t < T; t++)
        {
            int argmax = 0; float max = float.MinValue;
            int rowOff = t * C;
            for (int c = 0; c < C; c++)
            {
                float v = buf[rowOff + c];
                if (v > max) { max = v; argmax = c; }
            }
            string lbl = argmax == blank ? "<blank>" : (argmax < _labels.Length ? _labels[argmax] : $"<{argmax}>");
            Console.WriteLine($"  step {t,3}: idx={argmax,5} lbl='{lbl}' p={max:0.000}");
        }
    }

    /// <summary>dev: rec 입력 chw 텐서를 그레이로 복원 저장 — 전처리 왜곡 검사용.</summary>
    private void DumpChw(ImageData crop)
    {
        var chw = BuildRecInput(crop, out int w);
        var span = chw.Buffer.Span;
        int plane = RecH * w;
        var img = new ImageData(w, RecH);
        for (int y = 0; y < RecH; y++)
        for (int x = 0; x < w; x++)
        {
            float v = span[plane + y * w + x]; // G channel
            byte g8 = (byte)(Math.Clamp(v * 0.5f + 0.5f, 0f, 1f) * 255f);
            int d = (y * w + x) * 4;
            img.Bgra[d] = img.Bgra[d + 1] = img.Bgra[d + 2] = g8; img.Bgra[d + 3] = 255;
        }
        PngCodec.Encode(img, "/tmp/mangat/recchw.png");
        Console.WriteLine($"  chw dump -> /tmp/mangat/recchw.png ({w}x{RecH})");
    }

    private DenseTensor<float> BuildRecInput(ImageData crop, out int w)
    {
        w = Math.Max(8, Math.Min(RecMaxW,
            (int)(crop.Width * (RecH / (double)Math.Max(crop.Height, 1)) / 8 + 0.5) * 8));
        var chw = new DenseTensor<float>(new[] { 1, 3, RecH, w });
        var span = chw.Buffer.Span;
        int plane = RecH * w;

        int dw, dh, ox;
        if (RecLetterbox)
        {
            // aspect 유지: height→RecH 고정비율. 좁은 crop은 흰 패딩으로 좌우 여백(글자 세로stretch 제거)
            double sc = Math.Min(1.0, (double)RecH / Math.Max(1, crop.Height));
            double scaleW = crop.Width * sc;
            if (scaleW < w)
            {
                dw = Math.Max(1, (int)scaleW); ox = (w - dw) / 2; dh = RecH;
                for (int i = 0; i < span.Length; i++) span[i] = 1f; // 흰 패딩
            }
            else { dw = w; dh = RecH; ox = 0; } // pad 가득: 사실상 stretch
        }
        else { dw = w; dh = RecH; ox = 0; }

        int dySpan = 0; // letterbox의 세로 pad(최상단 정렬 — paddle rec는 좌상단 기준 학습)
        if (RecLetterbox && dh < RecH) dySpan = 0;

        for (int y = 0; y < dh; y++)
        {
            double sy = dh > 1 && crop.Height > 1
                ? Math.Min(crop.Height - 1, y * crop.Height / (double)dh) : 0;
            int y0 = (int)sy, y1 = Math.Min(crop.Height - 1, y0 + 1);
            double fy = sy - y0;
            var r0 = crop.Row(y0);
            var r1 = crop.Row(y1);
            int ty = y + dySpan;
            for (int x = 0; x < dw; x++)
            {
                double sx = dw > 1 && crop.Width > 1
                    ? Math.Min(crop.Width - 1, x * crop.Width / (double)dw) : 0;
                int x0 = (int)sx, x1 = Math.Min(crop.Width - 1, x0 + 1);
                double fx = sx - x0;
                int o0 = x0 * 4, o1 = x1 * 4;
                double wx1 = 1 - fx, wy1 = 1 - fy;
                int tx = x + ox;
                for (int c = 0; c < 3; c++)
                {
                    int ch = 2 - c; // R-first
                    double v =
                        (r0[o0 + ch] * wx1 + r0[o1 + ch] * fx) * wy1 +
                        (r1[o0 + ch] * wx1 + r1[o1 + ch] * fx) * fy;
                    span[c * plane + ty * w + tx] = (float)((v / 255.0 - 0.5) / 0.5);
                }
            }
        }
        return chw;
    }

    /// <summary>dev: crop 하나를 직접 rec(mode: auto|cw|ccw|raw) — 품질 검사용.</summary>
    public (string text, double conf, bool vertical) RecognizeOne(ImageData crop, string mode = "auto")
    {
        switch (mode)
        {
            case "raw":
                return Decode(crop, false);
            case "dump": // steps 상세 출력(진단용)
                DumpSteps(crop);
                return Decode(crop, false);
            case "cw": return Decode(Transpose(crop, true), true);
            case "ccw": return Decode(Transpose(crop, false), true);
            default:
                var raw = Decode(crop, false);
                var cw = Decode(Transpose(crop, true), true);
                var ccw = Decode(Transpose(crop, false), true);
                Console.WriteLine($"   raw='{raw.text}'({raw.conf:0.00}) cw='{cw.text}'({cw.conf:0.00}) ccw='{ccw.text}'({ccw.conf:0.00})");
                return raw;
        }
    }

    private (string text, double conf, bool vertical) RecognizeCrop(ImageData page, BoundingBox box)
    {
        bool verticalGuess = box.Height > box.Width * VerticalAspect ||
                             (double)Math.Max(box.Width, box.Height) / Math.Max(Math.Min(box.Width, box.Height), 1) > 4;

        if (!verticalGuess)
        {
            var crop = page.Crop(box);
            return Decode(crop, false);
        }

        var area = page.Crop(box);
        var cw = Transpose(area, clockwise: true);
        var ccw = Transpose(area, clockwise: false);
        var a = Decode(cw, true);
        var b = Decode(ccw, true);
        return a.conf >= b.conf ? a : b;
    }

    private static ImageData Transpose(ImageData src, bool clockwise)
    {
        var dst = new ImageData(src.Height, src.Width);
        for (int y = 0; y < src.Height; y++)
        {
            var s = src.Row(y);
            for (int x = 0; x < src.Width; x++)
            {
                int u = clockwise ? (src.Height - 1 - y) : y;       // CW:(x,y)→(H-1-y,x) CCW:(x,y)→(y,W-1-x)
                int v = clockwise ? x : (src.Width - 1 - x);
                var drow = dst.Row(v);
                drow[u * 4 + 0] = s[x * 4 + 0];
                drow[u * 4 + 1] = s[x * 4 + 1];
                drow[u * 4 + 2] = s[x * 4 + 2];
                drow[u * 4 + 3] = s[x * 4 + 3];
            }
        }
        return dst;
    }

    private (float[] buf, int T, int C) RecRun(ImageData crop)
    {
        EnsureRec();
        var chw = BuildRecInput(crop, out _);
        using var results = _rec!.Run(new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_recInput, chw) });
        var outT = results.First().AsTensor<float>();
        var d = outT.Dimensions;           // [1,T,C]
        return (outT.ToArray(), d[1], d[2]);
    }

    private (string text, double conf, bool vertical) Decode(ImageData crop, bool vertical)
    {
        var (buf, T, C) = RecRun(crop);
        // paddle/PaddleOCR CTC: blank = class 0 (사전 첫 항목=공백). 마지막 아님!
        const int blank = 0;
        // 참고: labels[t]가 비어있으면 해당 클래스도 blank 취급. labels↔idx는 paddle keys 기준 0부터.

        var sb = new System.Text.StringBuilder();
        double sum = 0; int n = 0; int prev = -1;
        for (int t = 0; t < T; t++)
        {
            int argmax = 0; float max = float.MinValue;
            int rowOff = t * C;
            for (int c = 0; c < C; c++)
            {
                float v = buf[rowOff + c];
                if (v > max) { max = v; argmax = c; }
            }
            if (argmax == prev) continue;
            prev = argmax;
            if (argmax == blank) continue;
            // 사전 첫 줄 공백/빈 항목 → 스킵(CTC가 띄어쓰기 대신 뱉는 경우 다수)
            if (argmax < _labels.Length && !string.IsNullOrEmpty(_labels[argmax]))
            { sb.Append(_labels[argmax]); sum += max; n++; }
        }
        double conf = n > 0 ? sum / n : 0;
        return (sb.ToString(), conf, vertical);
    }

    public void Dispose()
    {
        _det.Dispose();
        _rec?.Dispose();
    }
}
