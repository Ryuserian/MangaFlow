using System.IO.Compression;

namespace MangaFlow.Core.Imaging;

/// <summary>
/// BCL-only(truecolor RGBA8) PNG 인코더. native 라이브러리/서드파티 없이
/// Core에서 출력 이미지를 보장적으로 쓴다. 디코딩은 App(Avalonia Bitmap)이 담당.
/// </summary>
public static class PngCodec
{
    public static void Encode(ImageData img, string path)
    {
        using var fs = File.Create(path);
        Encode(img, fs);
    }

    /// <summary>PNG → byte[](VLM base64 전송 등 메모리 전달용).</summary>
    public static byte[] EncodeBytes(ImageData img)
    {
        using var ms = new MemoryStream();
        Encode(img, ms);
        return ms.ToArray();
    }

    public static void Encode(ImageData img, Stream stream)
    {
        stream.Write(stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WriteChunk(stream, "IHDR", w =>
        {
            WriteBE(w, img.Width); WriteBE(w, img.Height);
            w.Write((byte)8); w.Write((byte)6); // bit depth 8, color type RGBA
            w.Write((byte)0); w.Write((byte)0); w.Write((byte)0);
        });

        // scanline filter type 0. ImageData는 BGRA, PNG colorType6는 RGBA → R/B 스왑
        byte[] raw = new byte[img.Height * (1 + img.Stride)];
        for (int y = 0; y < img.Height; y++)
        {
            int o = y * (1 + img.Stride);
            raw[o] = 0;
            var row = img.Row(y);
            for (int x = 0; x < img.Width; x++)
            {
                raw[o + 1 + x * 4 + 0] = row[x * 4 + 2]; // R
                raw[o + 1 + x * 4 + 1] = row[x * 4 + 1]; // G
                raw[o + 1 + x * 4 + 2] = row[x * 4 + 0]; // B
                raw[o + 1 + x * 4 + 3] = row[x * 4 + 3]; // A
            }
        }
        byte[] deflated = Deflate(raw);
        WriteChunkBytes(stream, "IDAT", deflated);
        WriteChunkBytes(stream, "IEND", Array.Empty<byte>());
    }

    // ================= decode =================

    /// <summary>
    /// 8bit PNG(colorType 0/2/3/4/6, non-interlace) → ImageData(BGRA).
    /// interlace/16bit/jpg는 미지원 — 필요 시 엔진 교체로 해결.
    /// </summary>
    public static ImageData Decode(string path)
    {
        using var fs = File.OpenRead(path);
        return Decode(fs);
    }

    public static ImageData Decode(Stream stream)
    {
        var sig = new byte[8];
        stream.ReadExactly(sig);
        if (sig[0] != 0x89 || sig[1] != 0x50 || sig[2] != 0x4E || sig[3] != 0x47)
            throw new InvalidDataException("not a png");

        int width = 0, height = 0, bitDepth = 8, colorType = 6, interlace = 0;
        byte[]? palette = null;
        var idat = new MemoryStream();

        var head = new byte[8];
        while (true)
        {
            if (stream.Read(head, 0, 8) < 8) break;
            int len = (head[0] << 24) | (head[1] << 16) | (head[2] << 8) | head[3];
            var type = System.Text.Encoding.ASCII.GetString(head, 4, 4);
            var data = new byte[len];
            stream.ReadExactly(data);
            stream.ReadExactly(new byte[4]); // crc (skip verify)

            switch (type)
            {
                case "IHDR":
                    width = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
                    height = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];
                    bitDepth = data[8]; colorType = data[9]; interlace = data[12];
                    break;
                case "PLTE": palette = data; break;
                case "IDAT": idat.Write(data); break;
                case "IEND": goto done;
            }
        }
        done:;

        if (bitDepth != 8) throw new NotSupportedException($"bitDepth {bitDepth}");
        if (interlace != 0) throw new NotSupportedException("interlaced png");
        int ch = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new NotSupportedException($"colorType {colorType}") };
        if (colorType == 3 && palette == null) throw new InvalidDataException("palette png without PLTE");

        int stride = width * ch;
        byte[] raw;
        idat.Position = 0;
        raw = new byte[height * (1 + stride)];
        using (var z = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int got = 0, n;
            while ((n = z.Read(raw, got, raw.Length - got)) > 0) got += n;
            if (got < raw.Length) throw new InvalidDataException("png idat truncated");
        }

        // unfilter + RGBA→BGRA 변환을 행 단위 단일 루프로
        var img = new ImageData(width, height);
        var dst = img.Bgra;
        byte[] cur = new byte[stride];
        byte[] prev = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int o = y * (1 + stride);
            Unfilter(raw[o], raw.AsSpan(o + 1, stride), y == 0 ? null : prev, cur, ch);

            for (int x = 0; x < width; x++)
            {
                int s = x * ch, d = (y * width + x) * 4;
                switch (colorType)
                {
                    case 6:
                        dst[d] = cur[s + 2]; dst[d + 1] = cur[s + 1]; dst[d + 2] = cur[s]; dst[d + 3] = cur[s + 3];
                        break;
                    case 2:
                        dst[d] = cur[s + 2]; dst[d + 1] = cur[s + 1]; dst[d + 2] = cur[s]; dst[d + 3] = 255;
                        break;
                    case 0:
                        dst[d] = dst[d + 1] = dst[d + 2] = cur[s]; dst[d + 3] = 255;
                        break;
                    case 4:
                        dst[d] = dst[d + 1] = dst[d + 2] = cur[s]; dst[d + 3] = cur[s + 1];
                        break;
                    case 3:
                        int pi = cur[s] * 3;
                        dst[d] = palette![pi + 2]; dst[d + 1] = palette[pi + 1]; dst[d + 2] = palette[pi]; dst[d + 3] = 255;
                        break;
                }
            }

            (cur, prev) = (prev, cur); // prev ← 방금 복원한 행
        }
        return img;
    }

    private static void Unfilter(byte filterType, Span<byte> line, byte[]? prev, byte[] cur, int bpp)
    {
        switch (filterType)
        {
            case 0:
                line.CopyTo(cur);
                break;
            case 1: // Sub
                for (int i = 0; i < line.Length; i++)
                    cur[i] = (byte)(line[i] + (i >= bpp ? cur[i - bpp] : 0));
                break;
            case 2: // Up
                for (int i = 0; i < line.Length; i++)
                    cur[i] = (byte)(line[i] + (prev != null ? prev[i] : 0));
                break;
            case 3: // Average
                for (int i = 0; i < line.Length; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0;
                    int b = prev != null ? prev[i] : 0;
                    cur[i] = (byte)(line[i] + (a + b) / 2);
                }
                break;
            case 4: // Paeth
                for (int i = 0; i < line.Length; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0;
                    int b = prev != null ? prev[i] : 0;
                    int c = (prev != null && i >= bpp) ? prev[i - bpp] : 0;
                    cur[i] = (byte)(line[i] + Paeth(a, b, c));
                }
                break;
            default:
                throw new InvalidDataException($"png filter {filterType}");
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static byte[] Deflate(byte[] data)
    {
        // PNG IDAT는 zlib 컨테이너 필수(raw deflate 아님)
        using var ms = new MemoryStream();
        using (var d = new ZLibStream(ms, CompressionLevel.Optimal, true))
            d.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    private static void WriteBE(BinaryWriter w, int v)
    {
        w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16)); w.Write((byte)(v >> 8)); w.Write((byte)v);
    }

    private static void WriteChunk(Stream s, string type, Action<BinaryWriter> body)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms)) body(bw);
        WriteChunkBytes(s, type, ms.ToArray());
    }

    private static void WriteChunkBytes(Stream s, string type, byte[] data)
    {
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        int l = data.Length;
        s.WriteByte((byte)(l >> 24)); s.WriteByte((byte)(l >> 16)); s.WriteByte((byte)(l >> 8)); s.WriteByte((byte)l);
        s.Write(t);
        s.Write(data, 0, l);
        uint crc = Crc32(t, data);
        s.WriteByte((byte)(crc >> 24)); s.WriteByte((byte)(crc >> 16)); s.WriteByte((byte)(crc >> 8)); s.WriteByte((byte)crc);
    }

    private static uint[]? _crcTable;
    private static uint Crc32(byte[] type, byte[] data)
    {
        _crcTable ??= BuildCrcTable();
        uint c = 0xFFFFFFFF;
        foreach (var b in type) c = _crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = _crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
