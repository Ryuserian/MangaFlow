using MangaFlow.Core.Imaging;
using MangaFlow.Core;

namespace MangaFlow.Core.Interfaces;

/// <summary>② 일본어 문자 인식. 위치(Box)와 내용을 함께 반환한다.</summary>
public interface IOcrEngine
{
    string Name { get; }
    /// <summary>언어 감지 지원 여부(사용자 요구: 전용이 아니어도 되고 정확하면 됨).</summary>
    bool SupportsLanguage(string langCode);
    /// <summary>page 전체를 입력받아 텍스트 영역 목록(읽기 순서 정렬 포함)을 반환.</summary>
    Task<IReadOnlyList<TextRegion>> RecognizeAsync(ImageData page, CancellationToken ct = default);
}

/// <summary>②-b VLM rec 보강: crop 하나의 판독(ground-truth급). specialist rec의 빈/저신뢰 결과 복구용.</summary>
public interface IVlmRecEngine : IDisposable
{
    string Name { get; }
    /// <summary>crop→텍스트. 세로 다중 열은 우→좌 열별 newline 구분 포함. 실패 시 "".</summary>
    Task<string> RecognizeCropAsync(ImageData crop, CancellationToken ct = default);
}

/// <summary>무거운 엔진(ONNX session/GGUF/폰트)의 지연 로드 훅.
/// 앱 부팅 스플래시가 이걸 순차 호출해 progress를 채운다. 멱등·스레드 안전해야 한다.</summary>
public interface ILoadableEngine
{
    Task EnsureLoadedAsync(CancellationToken ct = default);
}

/// <summary>VRAM 언로드 훅(2026-10-25 사용자 요청: 메모리는 순차 상재).
/// 큰 GGUF(OCR VLM·번역 LLM)가 동시에 GPU에 뜨면 32GB를 초과하므로, 파이프라인은
/// 각 스테이지 종료 시 이것을 호출해 VRAM을 내린다. 엔진은 lazy 재로드로 응답해야 한다
/// (Unload 후 다음 사용 시 EnsureLoaded가 다시 로드). CPU 전용·경량 엔진은 미구현.</summary>
public interface IUnloadable
{
    /// <summary>true면 VRAM에 가중치가 상재 중.</summary>
    bool IsLoaded { get; }
    /// <summary>가중치 세션을 해제해 VRAM을 반환한다. 멱등. 다음 호출 시 lazy 재로드.</summary>
    void Unload();
}

/// <summary>③ 인식된 텍스트를 지우고 배경을 content-aware fill.</summary>
public interface IInpaintEngine
{
    string Name { get; }
    /// <summary>입력 이미지와 동일한 크기/포맷의 새 이미지를 반환한다(입력 수정 금지).</summary>
    Task<ImageData> FillAsync(ImageData page, IReadOnlyList<BoundingBox> regions, CancellationToken ct = default);
}

/// <summary>④ 번역. 문장 리스트 → 같은 인덱스의 번역 리스트.</summary>
public interface ITranslationEngine
{
    string Name { get; }
    int MaxConcurrentSentences { get; }
    bool RequiresGpu { get; }
    /// <summary>tones: 화자 말투 힌트(동일 길이, null=전부 Auto). LLM 엔진은 (남)/(여) 마커로
    /// 어투를 맞추고, seq2seq(google·deepl) 엔진은 무시한다.</summary>
    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLang, string targetLang,
        IReadOnlyList<SpeechTone>? tones = null, CancellationToken ct = default);
}

/// <summary>⑤ 번역문을 같은 위치·비슷한 스타일로 재조판.</summary>
public interface ITextRenderer
{
    string Name { get; }
    /// <summary>page 위에 region.TranslatedText를 그린다(병합, 입력 복사본에 작업 권장).</summary>
    void Render(ImageData page, IReadOnlyList<TextRegion> regions);
}
