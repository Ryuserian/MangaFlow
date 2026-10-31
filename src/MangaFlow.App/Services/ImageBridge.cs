using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MangaFlow.Core.Imaging;

namespace MangaFlow.App.Services;

/// <summary>Avalonia Bitmap ↔ Core ImageData(BGRA8) 변환 브리지.</summary>
public static class ImageBridge
{
    public static Bitmap ToBitmap(ImageData d)
    {
        var wb = new WriteableBitmap(
            new PixelSize(d.Width, d.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = wb.Lock();
        for (int y = 0; y < d.Height; y++)
            Marshal.Copy(d.Bgra, y * d.Stride, fb.Address + y * fb.RowBytes, d.Stride);
        return wb;
    }

    public static ImageData FromBitmap(Bitmap bmp)
    {
        int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;
        var data = new ImageData(w, h);
        var handle = GCHandle.Alloc(data.Bgra, GCHandleType.Pinned);
        try
        {
            bmp.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), data.Bgra.Length, data.Stride);
        }
        finally
        {
            handle.Free();
        }
        return data;
    }
}
