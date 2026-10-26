using MangaTranslator.Core.Imaging;
using SixLabors.ImageSharp;

namespace MangaTranslator.App.Smoke;

/// <summary>
/// dev IO: png 외 형식(jpeg 포함) 디코딩. 순수 C# ImageSharp —
/// **진보적 JPEG(SOF C2)는 SkiaSharp 2.88이 미지원**이라 ImageSharp를 쓴다 (Apache-2.0/SixLabors Split, 개인 이용 OK).
/// </summary>
public static class ImageIO
{
    public static ImageData Decode(string path)
    {
        using var img = Image.Load(path);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        ms.Position = 0;
        return PngCodec.Decode(ms);
    }
}
