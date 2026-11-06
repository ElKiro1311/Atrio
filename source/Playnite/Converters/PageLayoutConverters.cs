using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Playnite.Converters
{
    /// <summary>
    /// Where everything goes in the art at the top of a game page. On an ordinary window the picture takes
    /// the whole width. It stops growing at the size where it still shows what it shows there, because
    /// past that point only a thin band of it would fit. On a wider window it stays in the middle and its
    /// sides fade into a blurred copy of it, which is what Steam does on those screens.
    /// </summary>
    public class HeroLayout
    {
        /// <summary>Width of the pictures of Steam for each unit of their height.</summary>
        public const double PictureShape = 3.1;
        private const double MinHeight = 230;
        private const double MaxHeight = 430;

        // Past the ordinary height only if the window has room to spare: the page has to start near the top.
        private const double RoomyMaxHeight = 640;
        private const double RoomyShare = 0.38;

        // The widest picture at the ordinary height, a window of 1920 pixels has to stay as it is, and how
        // wide it gets after that for each unit of height, which keeps the band of it that is seen.
        private const double PictureMaxWidth = 1600;
        private const double WidthPerHeight = 3.6;

        // The subject of these pictures usually stands in the lower half, so the band is taken below the middle.
        private const double BandPosition = 0.58;

        // How much of the picture each fade takes, in steps that start and end softly.
        private const double FadeShare = 0.16;
        private const int FadeSteps = 8;

        public double Height { get; }

        /// <summary>The widest the picture gets, it's centered if the window is wider.</summary>
        public double PictureWidth { get; }

        /// <summary>Height of the picture, of which <see cref="Height"/> is seen.</summary>
        public double PictureHeight { get; }

        public Thickness PictureMargin { get; }

        /// <summary>Fades the sides of the picture. Null when it reaches them.</summary>
        public Brush PictureMask { get; }

        /// <param name="width">Width of the page.</param>
        /// <param name="viewHeight">Height of the part of the page that is seen.</param>
        public HeroLayout(double width, double viewHeight)
        {
            var roomyHeight = Math.Min(RoomyMaxHeight, Math.Min(viewHeight * RoomyShare, width / WidthPerHeight));
            Height = Math.Max(Math.Max(MinHeight, Math.Min(MaxHeight, width / PictureShape)), roomyHeight);
            PictureWidth = Math.Min(width, Math.Max(PictureMaxWidth, Height * WidthPerHeight));
            PictureHeight = Math.Max(Height, PictureWidth / PictureShape);
            PictureMargin = new Thickness(0, -(PictureHeight - Height) * BandPosition, 0, 0);

            var spare = width - PictureWidth;
            if (spare >= 1)
            {
                PictureMask = CreateMask(Math.Min(FadeShare, spare / PictureWidth));
            }
        }

        private static Brush CreateMask(double fade)
        {
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            for (var step = 0; step <= FadeSteps; step++)
            {
                var position = (double)step / FadeSteps;
                var opacity = position * position * (3 - 2 * position);
                var color = Color.FromArgb((byte)Math.Round(opacity * 255), 0, 0, 0);
                mask.GradientStops.Add(new GradientStop(color, position * fade));
                mask.GradientStops.Add(new GradientStop(color, 1 - position * fade));
            }

            mask.Freeze();
            return mask;
        }
    }

    /// <summary>The layout of the art for the width of the page and the height of what is seen of it.</summary>
    public class HeroLayoutConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            return new HeroLayout(values[0] as double? ?? 0, values[1] as double? ?? 0);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>The picture of the art. The values are if it's on its way and its path: until it comes nothing changes.</summary>
    public class HeroPictureConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            return values[0] as bool? == true ? Binding.DoNothing : values[1];
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// The picture behind the sides of the art: the one of the art, else the background of the game. The
    /// values are if the first one is on its way, both pictures and the layout. Nothing changes until it
    /// comes, and nothing is loaded for a window where the art reaches the sides.
    /// </summary>
    public class HeroBackdropConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if ((values[3] as HeroLayout)?.PictureMask == null)
            {
                return null;
            }

            return values[0] as bool? == true ? Binding.DoNothing : values[1] as string ?? values[2];
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
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
