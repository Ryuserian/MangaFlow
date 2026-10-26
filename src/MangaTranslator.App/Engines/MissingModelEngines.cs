using MangaTranslator.Core;
using MangaTranslator.Core.Imaging;
using MangaTranslator.Core.Interfaces;

namespace MangaTranslator.App.Engines;

/// <summary>모델 미다운로드 상태에서 UI가 죽지 않게 명시적 안내를 던지는 엔진.</summary>
public sealed class PpOcrMissingEngine : IOcrEngine
{
    public string Name => "ppocr(missing)";
    public bool SupportsLanguage(string langCode) => true;

    public Task<IReadOnlyList<TextRegion>> RecognizeAsync(ImageData page, CancellationToken ct = default)
        => throw new InvalidOperationException(
            "PP-OCR 모델이 다운로드되어 있지 않습니다. 모델을 <저장소>/models/ocr/ch_PP-OCRv4/ 에 받거나 OCR 엔진을 stub.demo로 바꿔세요.");
}
