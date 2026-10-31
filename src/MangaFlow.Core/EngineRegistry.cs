using MangaFlow.Core.Interfaces;

namespace MangaFlow.Core;

/// <summary>
/// 엔진 교체 레지스트리. "모델을 바꿔 끼운다" = 여기에 다른 팩토리를 등록하거나
/// appsettings에서 엔진 키만 바꾼다. (예) ocr: "ppocr" → "vlm_ocr"
/// </summary>
public sealed class EngineRegistry
{
    private readonly Dictionary<string, Func<object>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    public void RegisterOcr(string key, Func<IOcrEngine> factory) => _factories[key] = factory;
    public void RegisterInpaint(string key, Func<IInpaintEngine> factory) => _factories[key] = factory;
    public void RegisterTranslation(string key, Func<ITranslationEngine> factory) => _factories[key] = factory;
    public void RegisterRenderer(string key, Func<ITextRenderer> factory) => _factories[key] = factory;

    public IOcrEngine CreateOcr(string key) => (IOcrEngine)_factories[key]();
    public IInpaintEngine CreateInpaint(string key) => (IInpaintEngine)_factories[key]();
    public ITranslationEngine CreateTranslation(string key) => (ITranslationEngine)_factories[key]();
    public ITextRenderer CreateRenderer(string key) => (ITextRenderer)_factories[key]();

    public IEnumerable<string> KeysOf<T>() => _factories.Keys;
}
