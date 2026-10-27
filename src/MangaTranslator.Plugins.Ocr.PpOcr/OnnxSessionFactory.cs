using Microsoft.ML.OnnxRuntime;

namespace MangaTranslator.Plugins.Ocr.PpOcr;

/// <summary>
/// ONNX 세션 생성 팩토리 — GPU(CUDA EP) 우선, 없으면/실패하면 CPU 폴백. (CPU 의존 0 원칙의 Core는 못 나오므로 ORT를 쓰는 PpOcr에 둠)
/// 중요: AppendExecutionProvider_CUDA(0)의 try/catch만으론 부족하다 — GPU 네이티브 DLL(ort capi)이
/// 없거나 드라이버가 낡은 환경에선 세션 생성 자체가 throw하므로 InferenceSession 생성까지 fallback 한다.
/// </summary>
public static class OnnxSessionFactory
{
    /// <summary>CUDA EP 시도는 프로세스당 1회만 — 첫 실패 후 캐시(매 세션마다 ORT [E] 노이즈·재시도 비용 방지).</summary>
    private static bool _cudaUnavailable;
    private static bool _pathsProbed;

    /// <summary>
    /// 설치형 CUDA Toolkit/cuDNN 경로를 프로세스 PATH에 자동 주입(1회).
    /// ONNX CUDA EP는 cublasLt64_*.dll·cudnn64_9.dll을 LoadLibrary로 찾는데, 설치 도구들이
    /// 시스템 PATH에 넣지 않는 경우가 많아(실측: v13.4는 bin\x64, cuDNN은 별도 C:\Program Files\NVIDIA\CUDNN)
    /// 잘-known 위치를 직접 탐지한다. 오버라이드: MANGA_CUDA_BIN / MANGA_CUDNN_BIN.
    /// </summary>
    private static void EnsureNativeSearchPaths()
    {
        if (_pathsProbed) return;
        _pathsProbed = true;
        if (!OperatingSystem.IsWindows()) return;
        // portable 배포(앱 폴더에 DLL 동봉) 모드: 스캔·주입 완전 생략
        if (Environment.GetEnvironmentVariable("MANGA_CUDA_SCAN") == "0") return;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var adds = new List<string>();

        void AddDir(string dir)
        {
            if (!Directory.Exists(dir)) return;
            if (!path.Contains(dir, StringComparison.OrdinalIgnoreCase)) adds.Add(dir);
        }

        // env 오버라이드 우선(사용자 지정 경로는 항상 앞에)
        AddDir(Environment.GetEnvironmentVariable("MANGA_CUDA_BIN") ?? "");
        AddDir(Environment.GetEnvironmentVariable("MANGA_CUDNN_BIN") ?? "");

        var cudaRoot = @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA";
        if (Directory.Exists(cudaRoot))
        {
            // 최신 toolkit 우선 — bin\x64(cublas/cudart 소재, 실측 v13.4)
            var latest = Directory.GetDirectories(cudaRoot, "v*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (latest != null) AddDir(Path.Combine(latest, "bin", "x64"));
        }
        var cudnnRoot = @"C:\Program Files\NVIDIA\CUDNN";
        if (Directory.Exists(cudnnRoot))
        {
            // 최신 cuDNN 우선 — v9.27\bin\13.4\x64 구조(버전自然是 최신 우선)
            var latest = Directory.GetDirectories(cudnnRoot, "v*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (latest != null)
            {
                var bin = Path.Combine(latest, "bin");
                if (Directory.Exists(bin))
                {
                    var x64 = Directory.GetDirectories(bin, "*", SearchOption.TopDirectoryOnly)
                        .Select(d => Path.Combine(d, "x64"))
                        .Where(Directory.Exists)
                        .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                    if (x64 != null) AddDir(x64);
                }
            }
        }
        if (adds.Count > 0)
        {
            Environment.SetEnvironmentVariable("PATH", string.Join(';', adds) + ";" + path);
            Console.Error.WriteLine("[ort] CUDA/cuDNN PATH 자동 주입: " + string.Join(", ", adds));
        }
    }

    public static InferenceSession CreateWithCpuFallback(string onnxPath, bool cuda = true)
    {
        if (cuda && Environment.GetEnvironmentVariable("ORT_CPU") != "1") EnsureNativeSearchPaths();
        if (cuda && !_cudaUnavailable && Environment.GetEnvironmentVariable("ORT_CPU") != "1")
        {
            try
            {
                var go = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                go.AppendExecutionProvider_CUDA(0);
                return new InferenceSession(onnxPath, go);
            }
            catch
            {
                // CUDA EP 부재(cublasLt etc 네이티브 DLL missing)/초기화 실패 → CPU 폴백, 재시도 금지
                if (!_cudaUnavailable)
                {
                    _cudaUnavailable = true;
                    Console.Error.WriteLine("[ort] CUDA EP 사용 불가 — CPU 세션으로 폴백 (ORT_CPU=1로 강제 지정 가능)");
                }
            }
        }
        var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        return new InferenceSession(onnxPath, so);
    }
}
