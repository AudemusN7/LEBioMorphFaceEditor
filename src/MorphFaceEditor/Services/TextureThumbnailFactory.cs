using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MorphFaceEditor.Core.Materials;

namespace MorphFaceEditor.Services;

public static class TextureThumbnailFactory
{
    public static ImageSource Create(DecodedTextureAsset texture, int maximumDimension = 192)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (texture.Width <= 0 || texture.Height <= 0 ||
            texture.Rgba8.Length != checked(texture.Width * texture.Height * 4))
        {
            throw new InvalidDataException("Decoded texture dimensions do not match its RGBA payload.");
        }

        var scale = Math.Min(1d, maximumDimension / (double)Math.Max(texture.Width, texture.Height));
        var width = Math.Max(1, (int)Math.Round(texture.Width * scale));
        var height = Math.Max(1, (int)Math.Round(texture.Height * scale));
        var bgra = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(texture.Height - 1, y * texture.Height / height);
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(texture.Width - 1, x * texture.Width / width);
                var source = (sourceY * texture.Width + sourceX) * 4;
                var destination = (y * width + x) * 4;
                bgra[destination] = texture.Rgba8[source + 2];
                bgra[destination + 1] = texture.Rgba8[source + 1];
                bgra[destination + 2] = texture.Rgba8[source];
                bgra[destination + 3] = byte.MaxValue; // previews intentionally ignore alpha
            }
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            bgra,
            checked(width * 4));
        bitmap.Freeze();
        return bitmap;
    }
}
