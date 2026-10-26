using System.Diagnostics;
using MangaTranslator.App.Services;

namespace MangaTranslator.App.Smoke;

/// <summary>dev: --preload — 스플래시 VM의 엔진 프리로드 시퀀스를 콘솔로 재현.
/// 엔진 캐시·ILoadableEngine 배선·네이티브 백엔드(CPU/GPU) 로드를 GUI 없이 검증한다.</summary>
public static class PreloadCli
{
    public static int Run()
    {
        var ctx = EngineContext.CreateDefault();
        var keys = ctx.DefaultKeys();
        Console.WriteLine($"keys: ocr={keys.Ocr} inpaint={keys.Inpaint} translate={keys.Translate} render={keys.Render}");
        var sw = Stopwatch.StartNew();
        var prog = new Progress<(double Percent, string Message)>();
        prog.ProgressChanged += (_, v) =>
            Console.WriteLine($"[{v.Percent:P0} {sw.Elapsed.TotalSeconds,6:0.0}s] {v.Message}");
        try { ctx.PreloadAsync(keys, prog).GetAwaiter().GetResult(); }
        finally { ctx.Dispose(); }
        Console.WriteLine($"preload done {sw.Elapsed.TotalSeconds:0.0}s");
        return 0;
    }
}
