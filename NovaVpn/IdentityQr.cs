using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using ZXing;
using ZXing.QrCode;
namespace NovaVpn;
public static class IdentityQr
{
    public static byte[] Png(string code)
    {
        if(code==null||!code.StartsWith("NOVA-ID1:")||code.Length>1800)throw new System.FormatException("Сначала создайте код переноса. Если код длиннее 1800 символов, используйте файл.");
        var writer=new BarcodeWriter {Format=BarcodeFormat.QR_CODE,Options=new QrCodeEncodingOptions {Width=768,Height=768,Margin=4,ErrorCorrection=ZXing.QrCode.Internal.ErrorCorrectionLevel.M}};
        using(var bitmap=writer.Write(code))using(var stream=new MemoryStream()){bitmap.Save(stream,System.Drawing.Imaging.ImageFormat.Png);return stream.ToArray();}
    }
    public static string Read(string path)
    {
        if(new FileInfo(path).Length>8*1024*1024)throw new System.FormatException("Изображение слишком большое.");
        using(var stream=File.OpenRead(path)) {var frame=BitmapFrame.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);if(frame.PixelWidth>4096||frame.PixelHeight>4096)throw new System.FormatException("Изображение больше 4096 пикселей.");}
        using(var bitmap=new System.Drawing.Bitmap(path)){var result=new BarcodeReader {Options=new ZXing.Common.DecodingOptions {TryHarder=true,PossibleFormats=new[]{BarcodeFormat.QR_CODE}}}.Decode(bitmap);if(result==null||!result.Text.StartsWith("NOVA-ID1:"))throw new System.FormatException("QR-код идентичности NOVA не найден.");return result.Text;}
    }
    public static void Show(Window owner,string code)
    {
        var bytes=Png(code);var image=new BitmapImage();using(var stream=new MemoryStream(bytes)){image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.StreamSource=stream;image.EndInit();}image.Freeze();
        var panel=new StackPanel {Margin=new Thickness(20)};panel.Children.Add(new TextBlock {Text="Зашифрованный профиль. Пароль передайте отдельно.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)});panel.Children.Add(new Image {Source=image,Width=400,Height=400});
        var save=new Button {Content="Сохранить PNG",Padding=new Thickness(12,8,12,8),Margin=new Thickness(0,12,0,0)};panel.Children.Add(save);
        var dialog=new Window {Owner=owner,Title="Перенос через QR",Width=460,Height=540,Content=panel,WindowStartupLocation=WindowStartupLocation.CenterOwner};save.Click+=(s,e)=>{var d=new Microsoft.Win32.SaveFileDialog {Filter="PNG|*.png",FileName="identity-qr.png"};if(d.ShowDialog(dialog)==true)File.WriteAllBytes(d.FileName,bytes);};dialog.ShowDialog();
    }
}

