using System.IO.Compression;
using MangaTranslator.Core.Imaging;
using MangaTranslator.Core.Interfaces;
using MangaTranslator.Plugins.Ocr.PpOcr;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace MangaTranslator.Plugins.Inpaint.Lama;

/// <summary>
/// LaMa content-aware fill 백엔드. 라이선스 확인 완료: 코드/웨이트 모두 Apache-2.0.
/// 동작:
///   1) regions(BGRA 페이지 기준 박스) → hard mask(pad) → 입력 텐서
///   2) 512×512 타일(stride 384, overlap 128). mask와 무교차 타일은 스킵(페이지 전체 낭비 방지)
///   3) 타일 output을 feather weight로 weighted-average 합성, soft mask alpha로 원본과 블렌드
///   4) CUDA EP(데스크톱 RTX) 우선, 실패 시 CPU 폴백
/// 아주 빠른 폴백은 Core의 FlatFillInpaintEngine, 교체는 EngineRegistry 키 "inpaint"만 바꾼다.
/// </summary>
public sealed class LamaInpaintEngine : IInpaintEngine, ILoadableEngine, IUnloadable, IDisposable
{
    public string Name => "lama";
    public string? ModelPath { get; set; }

    // ---- 공개 튜닝 너브 (dev: OCR과 동일 철학) ----
    public int Tile { get; set; } = 512;      // big-lama는 512 고정 학습
    public int Stride { get; set; } = 384;    // overlap = Tile-Stride
    public int Pad { get; set; } = 8;          // 텍스트 박스 외연( rendrr inflate와 동기). 너무 크면 말풍선 외곽 회색 잔상 → 8.
    public int MaskFeather { get; set; } = 3; // 합성 alpha 연화 radius
    public bool PreferCuda { get; set; } = true;

    private InferenceSession? _session;
    private string _imgName = "image";
    private string _maskName = "mask";
    private string _outName = "out";

    /// <summary>카탈로그가 zip을 가리키면 같은 자리에 언팩(1회 캐싱). fp32.onnx 직접 경로면 그대로.</summary>
    public static string ResolveOnnx(string zipOrOnnx)
    {
        if (!zipOrOnnx.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return zipOrOnnx;
        var dir = Path.GetDirectoryName(zipOrOnnx)!;
        var outPath = Path.Combine(dir, "lama_fp32.onnx");
        if (!File.Exists(outPath))
        {
            using var zip = ZipFile.OpenRead(zipOrOnnx);
            foreach (var e in zip.Entries)
                if (e.FullName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
                { e.ExtractToFile(outPath, true); break; }
            if (!File.Exists(outPath))
                throw new InvalidDataException($"no .onnx inside {zipOrOnnx}");
        }
        return outPath;
    }

    private InferenceSession Session => _session ??= CreateSession();

    /// <summary>스플래시 프리로드: 첫 Fill 시 세션 생성 지연을 앱 부팅 시점으로 앞당긴다.</summary>
    public Task EnsureLoadedAsync(CancellationToken ct = default) => Task.Run(() => _ = Session, ct);

    /// <summary>VRAM 순차 상재(2026-10-25): inpaint 스테이지 종료 후 세션(~200MB VRAM)을 내린다.
    /// Session 프로퍼티가 lazy라 다음 Fill 시 자동 재생성.</summary>
    public bool IsLoaded => _session != null;

    public void Unload()
    {
        try { _session?.Dispose(); } catch { }
        _session = null;
    }

    private InferenceSession CreateSession()
    {
        if (string.IsNullOrEmpty(ModelPath) || !File.Exists(ModelPath))
            throw new FileNotFoundException(
                "LaMa ONNX weight not wired. Download via ModelManager key 'inpaint.lama_big' then set ModelPath.");
        var onnx = ResolveOnnx(ModelPath);
        var s = OnnxSessionFactory.CreateWithCpuFallback(onnx, PreferCuda);

        foreach (var n in s.InputNames)
            if (n.Contains("mask", StringComparison.OrdinalIgnoreCase)) _maskName = n; else _imgName = n;
        _outName = s.OutputNames.First();
        return s;
    }

    public Task<ImageData> FillAsync(ImageData page, IReadOnlyList<BoundingBox> regions, CancellationToken ct = default)
        => Task.Run(() => Fill(page, regions, ct), ct);

    private ImageData Fill(ImageData page, IReadOnlyList<BoundingBox> regions, CancellationToken ct)
    {
        var w = page.Width; var h = page.Height;

        // 1) hard mask(padded boxes)
        var mask = new float[w * h];
        var anyMask = false;
        foreach (var r in regions)
        {
            var b = new BoundingBox(r.Left - Pad, r.Top - Pad, r.Right + Pad, r.Bottom + Pad).Clamp(w, h);
            for (var y = b.Top; y < b.Bottom; y++)
                mask.AsSpan(y * w + b.Left, b.Right - b.Left).Fill(1f);
            if (b.Right > b.Left && b.Bottom > b.Top) anyMask = true;
        }
        if (!anyMask) return page.Clone();

        // 합성 alpha: soft mask — 가장자리 feather로 밴딩/잔상 방지
        var alpha = BoxBlur(mask, w, h, MaskFeather);

        // 2) 타일 origin(가장자리는 뒤로 밀어 완전 커버, 중복 제거)
        var origins = new HashSet<(int, int)>();
        for (var y = 0; y < h; y += Stride)
            for (var x = 0; x < w; x += Stride)
            {
                var xe = Math.Min(x + Tile, w); var ye = Math.Min(y + Tile, h);
                origins.Add((Math.Max(0, xe - Tile), Math.Max(0, ye - Tile)));
            }

        var acc = new float[w * h * 3];
        var wsum = new float[w * h];
        var ramp = BuildRamp();
        var tileImg = new float[3 * Tile * Tile];
        var tileMask = new float[Tile * Tile];
        var thw = new int[] { 1, 3, Tile, Tile };
        var plane = Tile * Tile;

        foreach (var (x0, y0) in origins)
        {
            ct.ThrowIfCancellationRequested();

            // mask와 무교차 타일 스킵 — 2297×3123에서 대폭 절약
            var x1 = Math.Min(x0 + Tile, w); var y1 = Math.Min(y0 + Tile, h);
            var hit = false;
            for (var y = y0; y < y1 && !hit; y++)
            {
                var row = mask.AsSpan(y * w + x0, x1 - x0);
                for (var i = 0; i < row.Length; i++) if (row[i] > 0f) { hit = true; break; }
            }
            if (!hit) continue;

            // BGRA → CHW [0,1]; mask는 하드(0/1). 잘림 변은 mask에 없음(원본이 개움을 보증).
            for (var ty = 0; ty < y1 - y0; ty++)
            {
                var sRow = page.Row(y0 + ty)[(x0 * 4)..(x1 * 4)];
                var sMask = mask.AsSpan((y0 + ty) * w + x0, x1 - x0);
                var rowBase = ty * Tile;
                for (var tx = 0; tx < x1 - x0; tx++)
                {
                    var o = rowBase + tx;
                    tileImg[o] = sRow[tx * 4 + 2] / 255f;
                    tileImg[plane + o] = sRow[tx * 4 + 1] / 255f;
                    tileImg[2 * plane + o] = sRow[tx * 4] / 255f;
                    tileMask[o] = sMask[tx];
                }
            }
            // 잘림 변: 이전 타일 잔류값 제거 — mask 0·rgb 0으로 명시적 black padding
            for (var ty = y1 - y0; ty < Tile; ty++)
            {
                var rb = ty * Tile;
                Array.Clear(tileImg, rb, Tile);
                Array.Clear(tileImg, plane + rb, Tile);
                Array.Clear(tileImg, 2 * plane + rb, Tile);
                Array.Clear(tileMask, rb, Tile);
            }
            for (var ty = 0; ty < y1 - y0; ty++)
            {
                var rb = ty * Tile + (x1 - x0);
                Array.Clear(tileImg, rb, Tile - (x1 - x0));
                Array.Clear(tileImg, plane + rb, Tile - (x1 - x0));
                Array.Clear(tileImg, 2 * plane + rb, Tile - (x1 - x0));
                Array.Clear(tileMask, rb, Tile - (x1 - x0));
            }

            // DenseTensor가 버퍼를 래핑(복사 없음) — Run 완료가 다음 채우기보다 먼저
            var imgT = new DenseTensor<float>(tileImg, thw);
            var mskT = new DenseTensor<float>(tileMask, new int[] { 1, 1, Tile, Tile });
            using var res = Session.Run(new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_imgName, imgT),
                NamedOnnxValue.CreateFromTensor(_maskName, mskT),
            });
            var outT = ((DenseTensor<float>)res.First().AsTensor<float>()).Buffer.Span; // flat CHW

            // 3) feather 가중치 누적.
            // 출력 스케일 계약(실측 --lamatest): 이 Carve ONNX export는 [0,255] 반환(*255 불요).
            //昔 버그: *255f 하면 회색 0.74→188→188*255 클립 → 스크린톤 배경이 흰색으로 칠해짐(0088/p_144 재현).
            //단, 다른 export([0,1])와 호환 위해 max>1.5 아니면 255승수로 자동 판정하지 않는다 — 고정 [0,255].
            for (var ty = 0; ty < y1 - y0; ty++)
            {
                var gy = y0 + ty;
                var wr = ramp[ty];
                var rowBase = ty * Tile;
                for (var tx = 0; tx < x1 - x0; tx++)
                {
                    var gx = x0 + tx;
                    var o = rowBase + tx;
                    var wt = alpha[gy * w + gx] * wr * ramp[tx];
                    if (wt <= 0f) continue;
                    var gi = gy * w + gx;
                    wsum[gi] += wt;
                    acc[gi * 3] += outT[o] * wt;
                    acc[gi * 3 + 1] += outT[plane + o] * wt;
                    acc[gi * 3 + 2] += outT[2 * plane + o] * wt;
                }
            }
        }

        // 4) alpha 합성: p = orig*(1-α) + inpaint*α, α=min(wsum,1)
        var dst = page.Clone();
        for (var y = 0; y < h; y++)
        {
            var dRow = dst.Row(y);
            for (var x = 0; x < w; x++)
            {
                var gi = y * w + x;
                var a = Math.Min(1f, wsum[gi]);
                if (a <= 0f) continue;
                var inv = 1f / Math.Max(wsum[gi], 1e-6f);
                var o = x * 4;
                dRow[o] = Lerp(dRow[o], acc[gi * 3] * inv, a);
                dRow[o + 1] = Lerp(dRow[o + 1], acc[gi * 3 + 1] * inv, a);
                dRow[o + 2] = Lerp(dRow[o + 2], acc[gi * 3 + 2] * inv, a);
            }
        }
        return dst;
    }

    /// <summary>overlap(Stride&lt;Tile) 구간 삼각 ramp — 타일 경계 seam 방지.</summary>
    private float[] BuildRamp()
    {
        var ramp = new float[Tile];
        var ov = Math.Max(1, Tile - Stride);
        for (var i = 0; i < Tile; i++)
            ramp[i] = i < ov ? (float)i / ov : i > Tile - 1 - ov ? (float)(Tile - 1 - i) / ov : 1f;
        return ramp;
    }

    private static float[] BoxBlur(float[] src, int w, int h, int r)
    {
        if (r <= 0) return src;
        var tmp = new float[w * h]; var dst = new float[w * h];
        for (var y = 0; y < h; y++)
        {
            var row = y * w; float sum = 0;
            for (var x = -r; x <= r; x++) sum += src[row + Math.Clamp(x, 0, w - 1)];
            for (var x = 0; x < w; x++)
            {
                tmp[row + x] = sum / (2 * r + 1);
                sum += src[row + Math.Min(x + r + 1, w - 1)] - src[row + Math.Clamp(x - r, 0, w - 1)];
            }
        }
        for (var x = 0; x < w; x++)
        {
            float sum = 0;
            for (var y = -r; y <= r; y++) sum += tmp[Math.Clamp(y, 0, h - 1) * w + x];
            for (var y = 0; y < h; y++)
            {
                dst[y * w + x] = sum / (2 * r + 1);
                sum += tmp[Math.Min(y + r + 1, h - 1) * w + x] - tmp[Math.Clamp(y - r, 0, h - 1) * w + x];
            }
        }
        return dst;
    }

    private static byte Lerp(byte a, float b, float t) => (byte)Math.Clamp(a + (b - a) * t, 0, 255);

    public void Dispose() => _session?.Dispose();
}
