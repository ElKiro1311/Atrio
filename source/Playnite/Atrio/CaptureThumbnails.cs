using Playnite.SDK;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Playnite.Atrio
{
    /// <summary>
    /// Small pictures of the captures: pictures are read at the size they're shown, clips get the frame Windows
    /// shows for them in the Explorer. Nothing is written next to the captures.
    /// </summary>
    public static class CaptureThumbnails
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        /// <summary>The picture at that width, ready for any thread. Null if it can't be read.</summary>
        public static BitmapSource GetPicture(string path, int width)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    if (width > 0)
                    {
                        image.DecodePixelWidth = width;
                    }

                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Debug(e, $"Failed to read the capture {path}.");
                return null;
            }
        }

        /// <summary>The small picture of any capture: a picture or the frame of a clip.</summary>
        public static BitmapSource Get(GameCapture capture, int width)
        {
            if (capture == null)
            {
                return null;
            }

            return capture.IsVideo ? GetShellThumbnail(capture.Path, width) : GetPicture(capture.Path, width);
        }

        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(NativeSize size, int flags, out IntPtr bitmap);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int Width;
            public int Height;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            string path,
            IntPtr context,
            [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        private const int BiggerSizeOk = 0x1;
        private const int ThumbnailOnly = 0x8;

        /// <summary>The frame the Explorer shows for a clip; null when Windows has none for that kind of file.</summary>
        public static BitmapSource GetShellThumbnail(string path, int width)
        {
            IShellItemImageFactory factory = null;
            try
            {
                SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
                var height = Math.Max(1, width * 9 / 16);
                if (factory.GetImage(new NativeSize { Width = width, Height = height }, BiggerSizeOk | ThumbnailOnly, out var bitmap) != 0 ||
                    bitmap == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
                finally
                {
                    DeleteObject(bitmap);
                }
            }
            catch (Exception e) when (e is COMException || e is ArgumentException || e is InvalidCastException || e is FileNotFoundException)
            {
                logger.Debug(e, $"Windows has no picture for the clip {path}.");
                return null;
            }
            finally
            {
                if (factory != null)
                {
                    Marshal.ReleaseComObject(factory);
                }
            }
        }
    }
}
