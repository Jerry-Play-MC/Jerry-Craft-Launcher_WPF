using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Jerry_Craft_Launcher.NET_Framework_4._5_WPF
{
    public static class WebPHelper
    {
        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        public static BitmapSource DecodeToBitmapSource(byte[] data, string contentType)
        {
            if (data == null || data.Length == 0) return null;

            // 1. WPF 原生解码
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(data);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { }

            // 2. System.Drawing 解码
            try
            {
                using (var ms = new MemoryStream(data))
                using (var img = Image.FromStream(ms))
                    return BitmapToBitmapSource(new Bitmap(img));
            }
            catch { }

            // 3. WebP 解码
            if (IsWebP(data) || IsWebPContentType(contentType))
            {
                try
                {
                    using (var webp = new WebPWrapper.WebP())
                    using (var bmp = webp.Decode(data))
                    {
                        if (bmp != null)
                            return BitmapToBitmapSource(new Bitmap(bmp));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[WebP] 解码失败：" + ex.Message);
                }
            }

            return null;
        }

        private static BitmapSource BitmapToBitmapSource(Bitmap bmp)
        {
            IntPtr hBitmap = bmp.GetHbitmap();
            try
            {
                return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }

        private static bool IsWebP(byte[] data)
        {
            if (data == null || data.Length < 12) return false;
            return data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
                   data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50;
        }

        private static bool IsWebPContentType(string contentType)
        {
            return !string.IsNullOrEmpty(contentType) &&
                   contentType.ToLowerInvariant().Contains("webp");
        }
    }
}