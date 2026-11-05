using Playnite.Atrio;
using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Playnite.Converters
{
    /// <summary>
    /// Loads the icons of the achievements add-on for the pages of Atrio. Its paths carry two markers
    /// that a plain image can't read: "token|path" to refresh a changed file, and "gray:" for locked ones.
    /// </summary>
    public class AchievementIconConverter : IValueConverter
    {
        private const int IconSize = 96;
        private const string GrayPrefix = "gray:";

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var source = value as string;
            if (string.IsNullOrEmpty(source))
            {
                return null;
            }

            var separator = source.LastIndexOf('|');
            if (separator >= 0)
            {
                source = source.Substring(separator + 1);
            }

            var gray = source.StartsWith(GrayPrefix, StringComparison.OrdinalIgnoreCase);
            if (gray)
            {
                source = source.Substring(GrayPrefix.Length);
            }

            if (!source.Contains("://") && !Path.IsPathRooted(source))
            {
                source = Path.Combine(PlaynitePaths.ExtensionsDataPath, AtrioInfo.AchievementsPluginId.ToString(), source);
            }

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(source);
                image.DecodePixelWidth = IconSize;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.EndInit();
                if (!gray || image.IsDownloading)
                {
                    return image;
                }

                var converted = new FormatConvertedBitmap(image, PixelFormats.Gray8, null, 0);
                converted.Freeze();
                return converted;
            }
            catch (Exception e) when (e is IOException || e is UriFormatException || e is NotSupportedException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
