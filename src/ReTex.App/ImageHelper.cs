using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReTex.Core.Paa;

namespace ReTex.App;

public static class ImageHelper
{
    /// <summary>Converts a decoded PAA (BGRA32) into a frozen, cross-thread-usable BitmapSource.</summary>
    public static BitmapSource ToBitmap(PaaImage img)
    {
        var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96,
            PixelFormats.Bgra32, null, img.Bgra, img.Width * 4);
        bmp.Freeze();
        return bmp;
    }
}
