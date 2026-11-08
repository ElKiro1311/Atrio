using Playnite.Atrio;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Playnite.Controls
{
    /// <summary>
    /// The keyboard for a list of achievements whose items open their card with a toggle button named "Icon":
    /// with one card open, the arrows open the one before or after it, and Escape closes it.
    /// </summary>
    public static class AchievementKeys
    {
        public const string ToggleName = "Icon";

        public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(AchievementKeys), new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject obj)
        {
            return (bool)obj.GetValue(IsEnabledProperty);
        }

        public static void SetIsEnabled(DependencyObject obj, bool value)
        {
            obj.SetValue(IsEnabledProperty, value);
        }

        private static readonly RoutedEventHandler checkedHandler = Toggle_Checked;

        private static void OnIsEnabledChanged(DependencyObject obj, DependencyPropertyChangedEventArgs e)
        {
            if (!(obj is ItemsControl list))
            {
                return;
            }

            list.PreviewKeyDown -= List_PreviewKeyDown;
            list.RemoveHandler(ToggleButton.CheckedEvent, checkedHandler);
            if ((bool)e.NewValue)
            {
                list.PreviewKeyDown += List_PreviewKeyDown;
                list.AddHandler(ToggleButton.CheckedEvent, checkedHandler);
            }
        }

        // The keys reach the list through the button whose card is open, wherever the focus was before.
        private static void Toggle_Checked(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is ToggleButton toggle && toggle.Name == ToggleName && toggle.Focusable && !toggle.IsKeyboardFocused)
            {
                toggle.Focus();
            }
        }

        private static void List_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var list = (ItemsControl)sender;
            var toggles = GetToggles(list);
            var open = toggles.FindIndex(a => a?.IsChecked == true);
            if (open < 0)
            {
                return;
            }

            if (e.Key == Key.Escape)
            {
                toggles[open].IsChecked = false;
                e.Handled = true;
                return;
            }

            var step = AchievementCard.GetKeyStep(e.Key);
            if (step == 0)
            {
                return;
            }

            // At the first or the last one the key does nothing, rather than scrolling the page under the card.
            e.Handled = true;
            var next = open + step;
            if (next < 0 || next >= toggles.Count || toggles[next] == null)
            {
                return;
            }

            toggles[open].IsChecked = false;
            toggles[next].BringIntoView();
            toggles[next].IsChecked = true;
        }

        private static List<ToggleButton> GetToggles(ItemsControl list)
        {
            var toggles = new List<ToggleButton>(list.Items.Count);
            for (var i = 0; i < list.Items.Count; i++)
            {
                toggles.Add(FindToggle(list.ItemContainerGenerator.ContainerFromIndex(i)));
            }

            return toggles;
        }

        private static ToggleButton FindToggle(DependencyObject root)
        {
            if (root == null)
            {
                return null;
            }

            var pending = new Queue<DependencyObject>();
            pending.Enqueue(root);
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                if (current is ToggleButton toggle && toggle.Name == ToggleName)
                {
                    return toggle;
                }

                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
                {
                    pending.Enqueue(VisualTreeHelper.GetChild(current, i));
                }
            }

            return null;
        }
    }
}
