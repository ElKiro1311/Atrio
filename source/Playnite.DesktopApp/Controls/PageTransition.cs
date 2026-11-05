using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Playnite.DesktopApp.Controls
{
    /// <summary>
    /// Pages fade in rising a few pixels when the user moves between them.
    /// </summary>
    public static class PageTransition
    {
        private const double Offset = 12;
        private static readonly Duration duration = new Duration(TimeSpan.FromMilliseconds(220));

        public static void Play(FrameworkElement view)
        {
            if (view == null)
            {
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var translate = new TranslateTransform(0, Offset);
            view.RenderTransform = translate;
            view.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(Offset, 0, duration) { EasingFunction = ease });
        }
    }
}
