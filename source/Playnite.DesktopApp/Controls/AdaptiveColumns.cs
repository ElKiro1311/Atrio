using System;
using System.Windows;
using System.Windows.Controls;

namespace Playnite.DesktopApp.Controls
{
    /// <summary>
    /// Two columns that follow the window: the second one takes a share of the width within limits, and
    /// goes under the first one when there is no room for both. A second column with nothing to show
    /// gives its space back. Made for pages inside a scroll viewer, so the height is not constrained.
    /// </summary>
    public class AdaptiveColumns : Panel
    {
        public double SideShare { get; set; } = 0.33;
        public double SideMinWidth { get; set; } = 340;
        public double SideMaxWidth { get; set; } = 620;
        public double Spacing { get; set; } = 24;
        public double StackBelow { get; set; } = 820;

        private UIElement Main => InternalChildren.Count > 0 ? InternalChildren[0] : null;
        private UIElement Side => InternalChildren.Count > 1 ? InternalChildren[1] : null;

        private double GetSideWidth(double width)
        {
            return Math.Min(SideMaxWidth, Math.Max(SideMinWidth, width * SideShare));
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var width = double.IsInfinity(availableSize.Width) ? SideMaxWidth * 3 : availableSize.Width;
            if (Main == null)
            {
                return new Size(width, 0);
            }

            if (Side == null)
            {
                Main.Measure(new Size(width, double.PositiveInfinity));
                return new Size(width, Main.DesiredSize.Height);
            }

            var stacked = width < StackBelow;
            Side.Measure(new Size(stacked ? width : GetSideWidth(width), double.PositiveInfinity));
            var hasSide = Side.DesiredSize.Height > 0;
            Main.Measure(new Size(stacked || !hasSide ? width : width - GetSideWidth(width) - Spacing, double.PositiveInfinity));
            var height = !hasSide ? Main.DesiredSize.Height :
                stacked ? Main.DesiredSize.Height + Spacing + Side.DesiredSize.Height :
                Math.Max(Main.DesiredSize.Height, Side.DesiredSize.Height);
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (Main == null)
            {
                return finalSize;
            }

            var hasSide = Side != null && Side.DesiredSize.Height > 0;
            if (!hasSide)
            {
                Main.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
                Side?.Arrange(new Rect(0, 0, 0, 0));
            }
            else if (finalSize.Width < StackBelow)
            {
                Main.Arrange(new Rect(0, 0, finalSize.Width, Main.DesiredSize.Height));
                Side.Arrange(new Rect(0, Main.DesiredSize.Height + Spacing, finalSize.Width, Side.DesiredSize.Height));
            }
            else
            {
                var sideWidth = GetSideWidth(finalSize.Width);
                Main.Arrange(new Rect(0, 0, finalSize.Width - sideWidth - Spacing, finalSize.Height));
                Side.Arrange(new Rect(finalSize.Width - sideWidth, 0, sideWidth, finalSize.Height));
            }

            return finalSize;
        }
    }
}
