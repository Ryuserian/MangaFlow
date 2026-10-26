using MangaTranslator.App.Services;
using MangaTranslator.Core;
using MangaTranslator.Core.Imaging;
using MangaTranslator.Core.Interfaces;
using MangaTranslator.Plugins.Ocr.PpOcr;

namespace MangaTranslator.App.Smoke;

/// <summary>
/// dev CLI: dotnet run -- --verify <image> [outDir]
/// 사용자 제안 방식: 감지된 각 사각형을 원본에서 잘라내(sub-crop) 그 위치에
/// 실제로 일본어 텍스트가 존재하는지 검증한다.
///   ① 잉크 밀도: crop 내 어두운(텍스트로 추정) 픽셀 비율 — 빈 하늘/배경에
///      박스가 잘못 얹혔으면(좌표 스케일·오프셋 버그) ~0%
///   ② rec 재판독: crop만 PP rec로 다시 읽어 CJK가 나오는지
/// 박스별 crop PNG + PASS/FAIL 주석 페이지를 outDir에 저장하고,
/// 좌표 분포(cx/cy %와 3×3 히스토그램)를 출력해 "좌상단 편중"을 정량 확인.
/// </summary>
public static class VerifyCli
{
    /// <summary>잉크 판정: min(R,G,B)가 이보다 어두우면 텍스트 픽셀. 파스텔 손글씨(~200)를 잡느라 190.</summary>
    private const int InkThreshold = 190;

    public static int Run(string imagePath, string? outDir)
    {
        try
        {
            outDir ??= Path.Combine("verify", Path.GetFileNameWithoutExtension(imagePath));
            Directory.CreateDirectory(outDir);

            // GUI 기본값과 동일: det+Qwen3-VL rec; OCR_ENGINE=ppocr 로 det+PP rec 교체 가능
            bool pp = Environment.GetEnvironmentVariable("OCR_ENGINE") == "ppocr";
            using var engine = new PpOcrEngine(
                EngineContext.PpOcrDetPath, EngineContext.PpOcrRecPath, EngineContext.PpOcrDictPath);
            using var vlmEngine = new MangaTranslator.Plugins.Ocr.VlmOcr.VlmOcrEngine(
                EngineContext.PpOcrDetPath, EngineContext.PpOcrRecPath, EngineContext.PpOcrDictPath)
            { VlmRec = EngineContext.CreateVlmRec(), OwnsVlm = true };
            IOcrEngine active = pp ? engine : vlmEngine;

            var page = imagePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? PngCodec.Decode(imagePath) : ImageIO.Decode(imagePath);

            var regions = active.RecognizeAsync(page).GetAwaiter().GetResult();
            Console.WriteLine($"== verify {Path.GetFileName(imagePath)}  page={page.Width}x{page.Height}  " +
                              $"engine={active.Name}  regions={regions.Count}");

            var vis = page.Clone();
            int pass = 0;
            var hist = new int[9]; // 3x3 관측: (cy/33%)*3 + cx/33%
            int idx = 0;
            foreach (var r in regions)
            {
                var box = r.Box.Clamp(page.Width, page.Height);
                if (box.Width < 2 || box.Height < 2) { Console.WriteLine($"[{idx}] Degenerate box"); idx++; continue; }
                var crop = page.Crop(box);
                double ink = InkRatio(crop);
                var (txt, conf) = RecCheck(engine, crop, r.Vertical);
                bool hasCjk = ContainsCjk(txt);
                bool ok = hasCjk && ink > 0.003;
                // PP rec가 세로 crop을 못 읽는 한계: 잉크 충족 + 주엔진(VLM) 텍스트 유 → 간접 PASS
                bool indirect = !ok && ContainsCjk(r.Text) && ink > 0.003;
                if (ok || indirect) pass++;

                double cx = (double)box.CenterX / page.Width * 100;
                double cy = (double)box.CenterY / page.Height * 100;
                hist[Clamp0((int)(cy / 33.4)) * 3 + Clamp0((int)(cx / 33.4))]++;

                var cropPath = Path.Combine(outDir, $"r{idx:00}.png");
                PngCodec.Encode(crop, cropPath);

                // 오차 신호: rec가 다른 텍스트를 뱉으면 좌표가 어긋난 것일 수 있음
                var status = ok ? "PASS" : indirect ? "PASS*" : "FAIL"; // PASS*=PP 한계, orig(VLM) 근거
                var mismatch = hasCjk && !SameText(txt, r.Text) ? " [rec≠orig]" : "";
                Console.WriteLine($"[{idx:00}] {status} box=({box.Left},{box.Top})~({box.Right},{box.Bottom}) " +
                    $"center=({cx:0.0}%,{cy:0.0}%) ink={ink * 100:0.00}% rec='{txt}' conf={conf:0.00} orig='{Trim(r.Text)}'{mismatch}");

                // 초록=PASS / 주황=PASS* / 빨강=FAIL
                DrawRect(vis, box, ok ? (byte)0 : (byte)255, ok ? (byte)255 : (byte)140, ok || indirect ? (byte)0 : (byte)255);
                idx++;
            }

            var visPath = Path.Combine(outDir, "_page.png");
            PngCodec.Encode(vis, visPath);

            Console.WriteLine($"RESULT: pass {pass}/{regions.Count}  -> {visPath}");
            Console.WriteLine("center 3x3 histogram (row=top→bottom, col=left→right):");
            for (int gy = 0; gy < 3; gy++)
                Console.WriteLine($"  {hist[gy * 3],4} {hist[gy * 3 + 1],4} {hist[gy * 3 + 2],4}");

            int topLeft = hist[0];
            if (regions.Count >= 4 && topLeft == regions.Count)
                Console.WriteLine("!! WARNING: 모든 박스가 좌상단 1/3 구간에 집중 — 좌표 변환 버그 의심");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"VERIFY FAILED: {ex}");
            return 1;
        }
    }

    /// <summary>crop의 텍스트 잉크 비율. (색약자/컬러 손글씨 대비 min-channel 기준)</summary>
    private static double InkRatio(ImageData crop)
    {
        int n = 0, w = crop.Width, h = crop.Height;
        for (int y = 0; y < h; y++)
        {
            var row = crop.Row(y);
            for (int x = 0; x < w; x++)
            {
                int o = x * 4;
                byte mn = row[o] < row[o + 1] ? row[o] : row[o + 1];
                if (row[o + 2] < mn) mn = row[o + 2];
                if (mn < InkThreshold) n++;
            }
        }
        return (double)n / (w * h);
    }

    /// <summary>crop을 PP rec로 재판독(세로는 cw/ccw 중 high-conf). (text, conf)</summary>
    private static (string, double) RecCheck(PpOcrEngine pp, ImageData crop, bool vertical)
    {
        if (!vertical)
        {
            var (t, c, _) = pp.RecognizeOne(crop, "raw");
            return (t, c);
        }
        var a = pp.RecognizeOne(crop, "cw");
        var b = pp.RecognizeOne(crop, "ccw");
        return a.conf >= b.conf ? (a.text, a.conf) : (b.text, b.conf);
    }

    private static bool ContainsCjk(string s)
    {
        foreach (var c in s)
            if ((c >= '぀' && c <= 'ヿ') || (c >= '一' && c <= '鿿')) return true;
        return false;
    }

    private static bool SameText(string a, string b)
    {
        a = a.Replace(" ", ""); b = b.Replace(" ", "");
        return a.Length > 0 && (a.Contains(b) || b.Contains(a));
    }

    private static string Trim(string s) => s.Length <= 24 ? s : s[..24] + "…";

    private static int Clamp0(int v) => Math.Clamp(v, 0, 2);

    private static void DrawRect(ImageData img, BoundingBox b, byte b2, byte g, byte r, int t = 3)
    {
        img.FillRect(new BoundingBox(b.Left, b.Top, b.Right, b.Top + t), b2, g, r);
        img.FillRect(new BoundingBox(b.Left, b.Bottom - t, b.Right, b.Bottom), b2, g, r);
        img.FillRect(new BoundingBox(b.Left, b.Top, b.Left + t, b.Bottom), b2, g, r);
        img.FillRect(new BoundingBox(b.Right - t, b.Top, b.Right, b.Bottom), b2, g, r);
    }
}
