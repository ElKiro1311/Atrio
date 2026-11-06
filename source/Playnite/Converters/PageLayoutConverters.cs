using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Playnite.Converters
{
    /// <summary>
    /// Height of the art at the top of a game page. It keeps the shape of the picture while the window
    /// is ordinary, and stops growing on very wide screens so that the page still starts near the top.
    /// </summary>
    public class HeroHeightConverter : IValueConverter
    {
        public const double PictureShape = 3.1;
        private const double MinHeight = 230;
        private const double MaxHeight = 430;

        public static double GetHeight(double width)
        {
            return Math.Max(MinHeight, Math.Min(MaxHeight, width / PictureShape));
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return GetHeight(value as double? ?? 0);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Where the picture of the art goes on a very wide window, when only a band of it fits. The subject
    /// of these pictures usually stands in the lower half, so the band is taken below the middle.
    /// </summary>
    public class HeroPictureMarginConverter : IValueConverter
    {
        private const double BandPosition = 0.58;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var width = value as double? ?? 0;
            var hidden = width / HeroHeightConverter.PictureShape - HeroHeightConverter.GetHeight(width);
            return new System.Windows.Thickness(0, hidden > 0 ? -hidden * BandPosition : 0, 0, 0);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Height of that picture: the whole of it, or what fills the art if the window is narrow.</summary>
    public class HeroPictureHeightConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var width = value as double? ?? 0;
            return Math.Max(HeroHeightConverter.GetHeight(width), width / HeroHeightConverter.PictureShape);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// A picture from the web at the size a card needs. Steam posts can carry images of several
    /// thousand pixels. The parameter is the width in pixels.
    /// </summary>
    public class RemoteImageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is string url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = uri;
                image.DecodePixelWidth = System.Convert.ToInt32(parameter, CultureInfo.InvariantCulture);
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.EndInit();
                return image;
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                // A converter that throws takes the whole application with it, for a picture of a post.
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
