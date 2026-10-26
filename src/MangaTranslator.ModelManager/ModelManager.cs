using System.Security.Cryptography;
using System.Text.Json;

namespace MangaTranslator.ModelManager;

/// <summary>catalog.json 한 항목. repo/rev/sha256가 채워진 항목만 자동 다운로드된다.</summary>
public sealed record ModelSpec(
    string Key,          // "ocr.ppocr.det", "ocr.dots.gguf", "translate.qwen3_14b_gguf" …
    string Kind,         // ocr | inpaint | translate | font
    string Repo,         // huggingface repo id (verified a HF에서 확인된 것만 기입)
    string File,
    string Rev = "main",
    string? Sha256 = null,
    bool Downloaded = false,
    string Host = "hf",  // hf | github — gated HF 리포는 GitHub raw 미러로 우회(notofonts 사례)
    string? LocalPath = null,  // 캐시 내 상대 경로 오버라이드 — 엔진이 읽는 실제 경로와 단일 진실원 일치
    bool Optional = false,     // true: 감사·자동다운로드 대상 아님(dev 실험용, --getmodel로만 확보)
    string? Group = null);     // 같은 Group 중 하나만 있어도 충족. 전부 없으면 감사에 전 후보 리스팅(카탈로그 순서=기본순위) — UI에서 사용자가 택1

/// <summary>
/// Hugging Face Hub에서 모델을 내려받아 로컬 캐시에 두고, 엔진마다 자신의 키로 경로를 찾아 쓴다.
/// 키를 바꿔치기하는 것이 곧 "모델 교체"의 사용자 인터페이스가 된다.
/// </summary>
public sealed class ModelCatalogManager
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>저장소 루트(slnx 마커 상향 탐색). tools/(llama.cpp 런타임 등) 위치 결정에 쓴다.</summary>
    public static string RepoRoot
    {
        get
        {
            for (var dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
                if (File.Exists(Path.Combine(dir, "MangaTranslator.slnx"))) return dir;
            return Environment.CurrentDirectory;
        }
    }

    /// <summary>
    /// 모델 캐시 루트. 기본은 저장소 루트의 `models/` (slnx 마커 파일로 상향 탐색) —
    /// 모델·사전도 프로젝트 트리의 산출물로 관리한다. env MANGA_MODELS_DIR로 덮어쓰기 가능.
    /// </summary>
    public static string DefaultRootDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("MANGA_MODELS_DIR");
            if (!string.IsNullOrEmpty(env)) return env;
            // 이식 배포(self-contained exe 폴더 + 그 옆 models/): slnx 마커가 없는 배포 트리에서는
            // exe 인접 models/가 단일 진실원(2026-10-26 ftg 배포 실측 지원).
            var local = Path.Combine(AppContext.BaseDirectory, "models");
            if (Directory.Exists(local)) return local;
            return Path.Combine(RepoRoot, "models");
        }
    }

    public string RootDir { get; }
    private readonly List<ModelSpec> _catalog;

    public ModelCatalogManager(string? rootDir = null, string? catalogPath = null)
    {
        RootDir = rootDir ?? DefaultRootDir;
        Directory.CreateDirectory(RootDir);

        if (catalogPath != null && File.Exists(catalogPath))
        {
            var bytes = File.ReadAllBytes(catalogPath);
            _catalog = bytes.Length > 0 ? JsonSerializer.Deserialize<List<ModelSpec>>(bytes)! : new();
        }
        else
        {
            _catalog = MangaTranslator.ModelManager.Catalog.Default;
        }
    }

    public IReadOnlyList<ModelSpec> Catalog => _catalog;

    public ModelSpec? Find(string key) => _catalog.FirstOrDefault(c => c.Key == key);

    /// <summary>캐시 내 실체 경로. LocalPath 지정 시 그것을 따름 — 엔진이 읽는 경로와 단일 진실원 유지.</summary>
    public string PathFor(ModelSpec spec) => spec.LocalPath != null
        ? Path.Combine(RootDir, spec.LocalPath)
        : Path.Combine(RootDir, spec.Kind, spec.Repo.Replace('/', '_'), spec.File);

    public bool IsReady(string key)
    {
        var s = Find(key);
        return s != null && File.Exists(PathFor(s));
    }

    /// <summary>HF resolve URL → .part로 받아하고 sha256 확인 후 확정한다.</summary>
    public async Task<string> EnsureAsync(ModelSpec spec, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dest = PathFor(spec);
        if (File.Exists(dest)) return dest;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

        var url = $"https://huggingface.co/{spec.Repo}/resolve/{spec.Rev}/{Uri.EscapeDataString(spec.File)}";
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MangaTranslator/0.1");

        using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength ?? 0;

        var part = dest + ".part";
        await using (var src = await res.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(part))
        {
            var buf = new byte[1 << 20];
            long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        if (spec.Sha256 != null)
        {
            await using var fs = File.OpenRead(part);
            var hash = Convert.ToHexString(SHA256.HashData(fs));
            if (!hash.Equals(spec.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"sha256 mismatch: {spec.Key}");
        }

        File.Move(part, dest, true);
        return dest;
    }

    public void SaveCatalog() =>
        File.WriteAllText(Path.Combine(RootDir, "catalog.json"), JsonSerializer.Serialize(_catalog, JsonOpts));
}

public static class Catalog
{
    /// <summary>
    /// 기본 카탈로그. Repo는 조사로 HF 실존/라이선스 확인된 것만 채워 두었다.
    /// SHA256과 가중치 파일명은 각 모델의 files 탭을 보고 채운 뒤 자동 다운로드가 동작한다.
    /// </summary>
    /// <summary>
    /// 기본 카탈로그 = 파이프라인 필수분만 리스팅(2026-10-26 개편).
    ///   · OCR rec는 **Qwen3-VL-30B-A3B**(단일 rec) — PP-OCR는 det 전용(0.5MB만 받는 이유).
    ///     manga-ocr 폴백 삭제(2026-10-26, 실측 garbage).
    ///   · 번역은 Group "qwen3" **둘 중 하나 선택** — 기본(첫 번째)=32B(≈18.4GiB), 14B(≈8.4GiB)는 저사양 선택지.
    ///     하나만 디스크에 있어도 충족되며 엔진(QwenGgufPath)이 자동 선택(큰 쪽 우선). "고정 기본값" 없음.
    ///   · Optional=감사·자동다운로드 무시, --getmodel로만 확보(dev 실험: japan rec 등).
    /// LocalPath는 엔진이 실제로 읽는 캐시 경로(models/ 상대)와 일치시킬 것(단일 진실원).
    /// </summary>
    public static List<ModelSpec> Default { get; } = new()
    {
        // PP-OCRv4 det (Apache-2.0, SWHL/RapidOCR) — 말풍선 박스 검출 전용. rec는 VLM/manga가 담당.
        new("ocr.ppocr.det", "ocr", "SWHL/RapidOCR", "PP-OCRv4/ch_PP-OCRv4_det_infer.onnx",
            Sha256: "d2a7720d45a54257208b1e13e36a8479894cb74155a5efe29462512d42f49da9",
            LocalPath: "ocr/ch_PP-OCRv4/ch_PP-OCRv4_det_infer.onnx"),
        // OCR rec = Qwen3-VL-30B-A3B (Apache-2.0, MoE 활성3B=속도 경량·판독 상위 tier) — rec 필수.
        // EngineContext.VlmDir/LocalPath 일치. Q4_K_M≈17.3GiB+mmproj Q8≈0.66GiB.
        // 해시: HF tree API lfs oid 실측 고정(2026-10-25).
        new("ocr.qwen3vl.gguf", "ocr", "Qwen/Qwen3-VL-30B-A3B-Instruct-GGUF",
            "Qwen3VL-30B-A3B-Instruct-Q4_K_M.gguf",
            Sha256: "87bb374d849f80ebdfabb304189fac9e0bd35a0f74506e6a59c51b206cbe863b",
            LocalPath: "ocr/Qwen3-VL-30B-A3B-Instruct-GGUF/Qwen3VL-30B-A3B-Instruct-Q4_K_M.gguf"),
        new("ocr.qwen3vl.mmproj", "ocr", "Qwen/Qwen3-VL-30B-A3B-Instruct-GGUF",
            "mmproj-Qwen3VL-30B-A3B-Instruct-Q8_0.gguf",
            Sha256: "82a9966edfdbc1b18a27fd90af96d50cbf111a51484f6290e946dccf461c8150",
            LocalPath: "ocr/Qwen3-VL-30B-A3B-Instruct-GGUF/mmproj-Qwen3VL-30B-A3B-Instruct-Q8_0.gguf"),
        // apache-2.0. (smartywu/big-lama zip은 best.ckpt라 폐기 — ONNX는 Carve/LaMa-ONNX 사용)
        new("inpaint.lama_big", "inpaint", "Carve/LaMa-ONNX", "lama_fp32.onnx",
            Sha256: "1faef5301d78db7dda502fe59966957ec4b79dd64e16f03ed96913c7a4eb68d6"),
        // 번역 = Qwen3 GGUF q4_k_m (Apache-2.0) — 둘 중 하나 **선택**. 카탈로그 순서=기본순위:
        // 32B가 첫 항목(다운로드 기본·권장, RTX 4090/5090·mac 32GB+), 14B는 저사양 선택(RTX 3060 12GB+·16GB mac).
        // 보유 파일은 QwenGgufPath가 자동 선택(큰 쪽 우선). nllb·qwen2.5 폴백 삭제(2026-10-24).
        // 32B 해시: ggml-org tree API lfs oid 실측 고정(2026-10-25, 로컬 복사분과 일치 확인됨).
        new("translate.qwen3_32b_gguf", "translate", "ggml-org/Qwen3-32B-GGUF", "Qwen3-32B-Q4_K_M.gguf",
            Sha256: "4d7312a48e7f11572045afe2b27b3bc3407f3f01ceb9aedb594ea82364f91194",
            Group: "qwen3", LocalPath: "translate/Qwen_Qwen3-32B-GGUF/Qwen3-32B-Q4_K_M.gguf"), // apache-2.0
        new("translate.qwen3_14b_gguf", "translate", "unsloth/Qwen3-14B-GGUF", "Qwen3-14B-Q4_K_M.gguf",
            Group: "qwen3", LocalPath: "translate/Qwen_Qwen3-14B-GGUF/Qwen3-14B-Q4_K_M.gguf"), // apache-2.0
        // googlefonts HF 리포는 gated(401) → googlefonts GitHub raw 미러 사용 (Host="github")
        new("font.noto_sans_kr", "font", "googlefonts/noto-cjk", "Sans/SubsetOTF/KR/NotoSansKR-Regular.otf", Host: "github"),   // OFL
        new("font.noto_sans_kr_bold", "font", "googlefonts/noto-cjk", "Sans/SubsetOTF/KR/NotoSansKR-Bold.otf", Host: "github"),   // OFL
        // dev 실험용 Optional — 파이프라인 필수 아님. `--getmodel <key>`로만 확보.
        new("ocr.ppocr.rec", "ocr", "SWHL/RapidOCR", "PP-OCRv4/ch_PP-OCRv4_rec_infer.onnx",
            Sha256: "48fc40f24f6d2a207a2b1091d3437eb3cc3eb6b676dc3ef9c37384005483683b",
            Optional: true, LocalPath: "ocr/ch_PP-OCRv4/ch_PP-OCRv4_rec_infer.onnx"),
        new("ocr.ppocr.rec_japan", "ocr", "SWHL/RapidOCR", "PP-OCRv4/japan_PP-OCRv4_rec_infer.onnx",
            Optional: true, LocalPath: "ocr/ch_PP-OCRv4/japan_PP-OCRv4_rec_infer.onnx"),
    };
}
