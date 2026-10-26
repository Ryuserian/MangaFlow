namespace MangaTranslator.Core.Imaging;

/// <summary>
/// Avalonia/ONNX 플러그인 모두와 native 충돌 없이 교환하는 순수 BGRA8 픽셀 버퍼.
/// stride = Width * 4. 모든 엔진(IOcrEngine 등)은 이 타입만 주고받는다.
/// </summary>
public sealed class ImageData
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Bgra { get; }
    public int Stride => Width * 4;

    public ImageData(int width, int height, byte[]? buffer = null, byte fillA = 255)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException();
        Width = width;
        Height = height;
        Bgra = buffer ?? new byte[width * height * 4];
        if (Bgra.Length != width * height * 4) throw new ArgumentException("buffer length mismatch");
        if (buffer == null)
            for (int i = 3; i < Bgra.Length; i += 4) Bgra[i] = fillA;
    }

    public ImageData Clone() => new(Width, Height, (byte[])Bgra.Clone());

    public Span<byte> Row(int y) => Bgra.AsSpan(y * Stride, Stride);

    public void FillRect(BoundingBox box, byte b, byte g, byte r, byte a = 255)
    {
        var clamped = box.Clamp(Width, Height);
        for (int y = clamped.Top; y < clamped.Bottom; y++)
        {
            var row = Row(y);
            for (int x = clamped.Left; x < clamped.Right; x++)
            {
                int o = x * 4;
                row[o] = b; row[o + 1] = g; row[o + 2] = r; row[o + 3] = a;
            }
        }
    }

    /// <summary>OCR 라인 이미지용 크롭.</summary>
    public ImageData Crop(BoundingBox box)
    {
        var c = box.Clamp(Width, Height);
        var w = c.Right - c.Left; var h = c.Bottom - c.Top;
        var dst = new ImageData(w, h);
        for (int y = 0; y < h; y++)
            Row(c.Top + y)[(c.Left * 4)..(c.Right * 4)].CopyTo(dst.Row(y));
        return dst;
    }
}

public readonly record struct BoundingBox(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public double Area => (double)Width * Height;
    public int CenterX => (Left + Right) / 2;
    public int CenterY => (Top + Bottom) / 2;
    public bool IsVertical => Height > Width * 1.2f;

    public BoundingBox Clamp(int imgW, int imgH) => new(
        Math.Max(0, Math.Min(Left, imgW)), Math.Max(0, Math.Min(Top, imgH)),
        Math.Max(0, Math.Min(Right, imgW)), Math.Max(0, Math.Min(Bottom, imgH)));

    public static BoundingBox FromCenter(int cx, int cy, int w, int h) =>
        new(cx - w / 2, cy - h / 2, cx - w / 2 + w, cy - h / 2 + h);
}
