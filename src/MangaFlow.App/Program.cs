using Avalonia;
using ReactiveUI.Avalonia; // Avalonia 12: UseReactiveUI() 확장(구 Avalonia.ReactiveUI 대체)
using MangaFlow.App.Smoke;

namespace MangaFlow.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // WinExe 전환 후에도 CLI(이 exe를 터미널에서 실행)의 stdout/stderr를 부모 콘솔에 재연결.
        ConsoleBridge.TryAttachParent();

        // GUI 없이 파이프라인 검증만: dotnet run -- --smoke [out.png]
        if (args.Length > 0 && args[0] == "--smoke")
            return SmokeTest.Run(args.Length > 1 ? args[1] : "out/smoke.png");

        // dev: --recrop <in.png> <x> <y> <w> <h> [cw|ccw]  — 특정 영역 rec 동작 검사
        if (args.Length >= 6 && args[0] == "--recrop")
            return RecropCli.Run(args[1],
                int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]),
                args.Length > 6 ? args[6] : "auto");

        // dev: --scale <in.png> <out.png> [maxW]
        if (args.Length >= 3 && args[0] == "--scale")
            return ScaleCli.Run(args[1], args[2], args.Length > 3 && int.TryParse(args[3], out var mw) ? mw : 900);

        // 실모델 OCR 테스트: dotnet run -- --ocrtest <image> [annot.png]
        if (args.Length > 0 && args[0] == "--ocrtest")
        {
            if (args.Length < 2) { Console.Error.WriteLine("usage: --ocrtest <image>"); return 2; }
            return OcrCli.Run(args[1], args.Length > 2 ? args[2] : null);
        }

        // 검증: --verify <image> [outDir] — 감지 박스를 crop해 해당 위치에 실제 일본어가 있는지 판정
        if (args.Length >= 2 && args[0] == "--verify")
            return VerifyCli.Run(args[1], args.Length > 2 ? args[2] : null);

        // dev: --inpainttest <in.png> [maxRegions] — LaMa content-fill 검증
        if (args.Length >= 2 && args[0] == "--inpainttest")
            return InpaintCli.Run(args[1], args.Length > 2 && int.TryParse(args[2], out var mr) ? mr : 0);

        // dev probe: --lamatest <image> — LaMa 정규화 스케일 계약 판정(흰색 채우기 디버그)
        if (args.Length >= 2 && args[0] == "--lamatest")
            return LamaProbeCli.Run(args[1]);

        // dev: --inpaintdiff <orig> <clean.png> — 박스 밖 픽셀 침범 여부 계측
        if (args.Length >= 3 && args[0] == "--inpaintdiff")
            return InpaintDiffCli.Run(args[1], args[2]);

        // dev: --preload — 스플래시 엔진 프리로드 시퀀스 검증
        if (args.Length >= 1 && args[0] == "--preload")
            return PreloadCli.Run();

        // 카탈로그 감사: --audit [download] — 없는 모델 목록+총 용량 출력 (MANGA_MODELS_DIR로 캐시 루트 지정 가능)
        if (args.Length >= 1 && args[0] == "--audit")
            return AuditCli.Run(args.Length > 1 && args[1] == "download");

        // 단일 모델 다운로드: --getmodel <catalog-key> — 스플래시 외부에서 백그라운드 확보용
        if (args.Length >= 2 && args[0] == "--getmodel")
            return AuditCli.Get(args[1]);

        // 종단 종단: --pipeline <in.png> [out.png] — OCR→inpaint→ 번역→재조판
        if (args.Length >= 2 && args[0] == "--pipeline")
            return PipelineCli.Run(args[1], args.Length > 2 ? args[2] : null);

        // dev: --onnxinfo <file.onnx> [...] — IO 시그니처 dump
        if (args.Length >= 2 && args[0] == "--onnxinfo")
            return OnnxInfo.Run(args[1..]);

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .UseReactiveUI(_ => { }) // ReactiveUI.Avalonia 12: builder 콜백 필수
            .LogToTrace();
}
