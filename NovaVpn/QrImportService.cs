using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;
using FormatException = System.FormatException;

namespace NovaVpn;

public static class QrImportService
{
    public const int MaxPixels = 16 * 1024 * 1024;
    private static void ValidateSize(int width, int height)
    {
        if (width < 1 || height < 1 || (long)width * height > MaxPixels) throw new FormatException("Выделите меньшую область: изображение превышает 16 мегапикселей.");
    }
    public static string[] ReadFile(string path)
    {
        var file = new FileInfo(path);
        if (file.Length > 16 * 1024 * 1024) throw new FormatException("Изображение больше 16 МБ.");
        using (var stream = File.OpenRead(path))
        {
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            ValidateSize(frame.PixelWidth, frame.PixelHeight);
        }
        using (var bitmap = new Bitmap(path)) return Decode(bitmap);
    }
    public static string[] ReadClipboard()
    {
        if (!Clipboard.ContainsImage()) throw new FormatException("В буфере обмена нет изображения.");
        BitmapSource image = Clipboard.GetImage();
        if (image == null) throw new FormatException("Изображение буфера обмена недоступно.");
        ValidateSize(image.PixelWidth, image.PixelHeight);
        var formatted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        using (var bitmap = new Bitmap(formatted.PixelWidth, formatted.PixelHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
            try { formatted.CopyPixels(Int32Rect.Empty, data.Scan0, Math.Abs(data.Stride) * bitmap.Height, data.Stride); }
            finally { bitmap.UnlockBits(data); }
            return Decode(bitmap);
        }
    }
    public static string[] Decode(Bitmap image)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        ValidateSize(image.Width, image.Height);
        var decoder = new BarcodeReader { AutoRotate = true, Options = new ZXing.Common.DecodingOptions {
            TryHarder = true, PossibleFormats = new[] { BarcodeFormat.QR_CODE } } };
        var results = decoder.DecodeMultiple(image) ?? new Result[0];
        string[] values = results.Select(r => r.Text).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal).ToArray();
        if (values.Length == 0)
        {
            var single = decoder.Decode(image);
            if (single != null && !string.IsNullOrWhiteSpace(single.Text)) values = new[] { single.Text };
        }
        if (values.Length == 0) throw new FormatException("QR-код не найден. Выберите область с кодом целиком.");
        if (values.Any(v => v.Length > 4 * 1024 * 1024)) throw new FormatException("Содержимое QR-кода слишком большое.");
        return values;
    }
}
