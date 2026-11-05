using System.Windows;
using System.Windows.Media;

namespace Playnite.DesktopApp.Controls
{
    /// <summary>
    /// Rounds the corners of any element, including images. Themes use it as
    /// RoundedClip.Radius="10" because an Image has no corner radius of its own.
    /// </summary>
    public static class RoundedClip
    {
        public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
            "Radius",
            typeof(double),
            typeof(RoundedClip),
            new PropertyMetadata(0.0, OnRadiusChanged));

        public static double GetRadius(DependencyObject element)
        {
            return (double)element.GetValue(RadiusProperty);
        }

        public static void SetRadius(DependencyObject element, double value)
        {
            element.SetValue(RadiusProperty, value);
        }

        private static void OnRadiusChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                element.SizeChanged -= Element_SizeChanged;
                if ((double)e.NewValue > 0)
                {
                    element.SizeChanged += Element_SizeChanged;
                    UpdateClip(element);
                }
                else
                {
                    element.Clip = null;
                }
            }
        }

        private static void Element_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateClip((FrameworkElement)sender);
        }

        private static void UpdateClip(FrameworkElement element)
        {
            var radius = GetRadius(element);
            element.Clip = new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight), radius, radius);
        }
    }
}
