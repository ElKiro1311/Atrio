using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Playnite.DesktopApp.Controls
{
    /// <summary>
    /// Lays out children side by side with a width proportional to their weight.
    /// Used to draw the bar with the share of each store.
    /// </summary>
    public class ProportionalPanel : Panel
    {
        public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
            "Weight",
            typeof(double),
            typeof(ProportionalPanel),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsParentArrange));

        public static double GetWeight(DependencyObject element)
        {
            return (double)element.GetValue(WeightProperty);
        }

        public static void SetWeight(DependencyObject element, double value)
        {
            element.SetValue(WeightProperty, value);
        }

        private double GetTotalWeight()
        {
            var total = 0.0;
            foreach (UIElement child in InternalChildren)
            {
                total += GetWeight(child);
            }

            return total;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var height = 0.0;
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                height = Math.Max(height, child.DesiredSize.Height);
            }

            return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var total = GetTotalWeight();
            var x = 0.0;
            foreach (UIElement child in InternalChildren)
            {
                var width = total <= 0 ? 0 : finalSize.Width * GetWeight(child) / total;
                child.Arrange(new Rect(x, 0, width, finalSize.Height));
                x += width;
            }

            return finalSize;
        }
    }

    /// <summary>
    /// A horizontal list inside a vertical page swallows the mouse wheel. With this set to True
    /// the wheel keeps scrolling the page, as anyone would expect.
    /// </summary>
    public static class PageWheel
    {
        public static readonly DependencyProperty PassToParentProperty = DependencyProperty.RegisterAttached(
            "PassToParent",
            typeof(bool),
            typeof(PageWheel),
            new PropertyMetadata(false, OnPassToParentChanged));

        public static bool GetPassToParent(DependencyObject element)
        {
            return (bool)element.GetValue(PassToParentProperty);
        }

        public static void SetPassToParent(DependencyObject element, bool value)
        {
            element.SetValue(PassToParentProperty, value);
        }

        private static void OnPassToParentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is UIElement element)
            {
                element.PreviewMouseWheel -= Element_PreviewMouseWheel;
                if ((bool)e.NewValue)
                {
                    element.PreviewMouseWheel += Element_PreviewMouseWheel;
                }
            }
        }

        private static void Element_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || !(sender is DependencyObject element))
            {
                return;
            }

            e.Handled = true;
            var parent = VisualTreeHelper.GetParent(element) as UIElement;
            parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            });
        }
    }
}
