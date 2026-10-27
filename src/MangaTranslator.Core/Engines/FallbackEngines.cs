using MangaTranslator.Core.Imaging;
using MangaTranslator.Core.Interfaces;

namespace MangaTranslator.Core.Engines;

/// <summary>
/// 스모크 테스트/모델 부재 시의 placeholder 묶음. 실제 엔진과 인터페이스가 동일하므로
/// 파이프라인 검증(직렬화·메모리·예외 경로)를 모델 다운로드 없이 돌릴 수 있다.
/// </summary>
public sealed class NullOcrEngine : IOcrEngine
{
    public string Name => "stub.null";
    public bool SupportsLanguage(string langCode) => true;
    public Task<IReadOnlyList<TextRegion>> RecognizeAsync(ImageData page, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TextRegion>>(Array.Empty<TextRegion>());
}

public sealed class DemoTextOcrEngine : IOcrEngine
{
    public string Name => "stub.demo";
    public bool SupportsLanguage(string langCode) => true;
    public Task<IReadOnlyList<TextRegion>> RecognizeAsync(ImageData page, CancellationToken ct = default)
    {
        // 480px당 한 칸씩 목업 영역 생성 (렌더/inpaint 경로 검증용)
        var list = new List<TextRegion>();
        int y = 40;
        foreach (var text in new[] { "すごい！", "行くぞ…", "行くな！", "何してるんだ"} )
        {
            var box = new BoundingBox(60, y, 60 + 16 * text.Length, y + 32);
            list.Add(new TextRegion
            {
                Box = box, Text = text, Vertical = false,
                Style = new TextStyle { FontSizePx = 28, Balloon = BalloonKind.SpeechBubble },
            });
            y += 90;
        }
        return Task.FromResult<IReadOnlyList<TextRegion>>(list);
    }
}

/// <summary>채우기만 하는(v0) 폴백 — AI 없이 흰색+연한 회색 테두리. Lama 플러그인의 교체 대상.</summary>
public sealed class FlatFillInpaintEngine : IInpaintEngine
{
    public string Name => "stub.flatfill";
    public Task<ImageData> FillAsync(ImageData page, IReadOnlyList<BoundingBox> regions, CancellationToken ct = default)
    {
        var outp = page.Clone();
        foreach (var r in regions)
        {
            outp.FillRect(r, 245, 245, 245);
        }
        return Task.FromResult(outp);
    }
}

public sealed class PassthroughTranslationEngine : ITranslationEngine
{
    public string Name => "stub.passthrough";
    public int MaxConcurrentSentences => 64;
    public bool RequiresGpu => false;
    public Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLang, string targetLang,
        IReadOnlyList<SpeechTone>? tones = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(texts);
}

/// <summary>블록만 그리는(v0) 렌더러. 실제 폰트 렌더러는 App의 AvaloniaFontRenderer.</summary>
public sealed class BlockTextRenderer : ITextRenderer
{
    public string Name => "stub.block";
    public void Render(ImageData page, IReadOnlyList<TextRegion> regions)
    {
        foreach (var r in regions)
        {
            var box = r.InpaintBox(-2);
            page.FillRect(box, 0, 0, 0, 255); // 검은 배경 블록 = 번역문 자리
        }
    }
}
