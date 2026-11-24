using System;
using SkiaSharp;

namespace DungeonMapper.Models;

/// Pawn image upload normalization: every uploaded image becomes a 50x50 PNG.
/// The largest centered square is cropped from the source, scaled to exactly
/// 50x50 (small images are upscaled), and re-encoded as PNG. base64 inflates
/// bytes ~1.33x, so keeping glyphs at 50x50 keeps project and import/export
/// files small. SkiaSharp is used directly (it flows in transitively via
/// Avalonia.Desktop): Avalonia 12's CroppedBitmap is not a Bitmap and has no
/// Save/scale, so the crop+scale+encode pipeline lives in Skia.
public static class PawnImage
{
    public const int Side = 50;

    /// true when the bytes are already a 50x50 png that decodes
    public static bool IsNormalized(byte[] bytes)
    {
        if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != (byte)'P' || bytes[2] != (byte)'N' || bytes[3] != (byte)'G') return false;
        using var sk = SKBitmap.Decode(bytes);
        return sk is not null && sk.Width == Side && sk.Height == Side;
    }

    /// Compresses any decodable image (png/jpg) to 50x50 PNG bytes.
    public static byte[] Compress(byte[] source)
    {
        using var sk = SKBitmap.Decode(source);
        int side = Math.Min(sk.Width, sk.Height);
        int x = (sk.Width - side) / 2, y = (sk.Height - side) / 2;
        var srcRect = new SKRect(x, y, x + side, y + side);
        using var img = SKImage.FromBitmap(sk);
        using var dst = new SKBitmap(Side, Side);
        using (var ctx = new SKCanvas(dst))
            ctx.DrawImage(img, srcRect, new SKRect(0, 0, Side, Side),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
        using var data = dst.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
