using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace Playnite.DesktopApp.Atrio
{
    /// <summary>
    /// The page of the achievements add-on with the look of the rest of Atrio: one line of header,
    /// the games and the achievements as two cards with compact rows, and no lines between the parts.
    /// The add-on is not a reference of this project, so its controls are found by name and type.
    /// Only the page on screen changes: nothing goes into the settings of the add-on, its own windows
    /// keep their look, and so do the themes that don't come from Atrio.
    /// </summary>
    internal static class AchievementsPageLook
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string OverviewType = "PlayniteAchievements.Views.OverviewControl";
        private const string CardTag = "AtrioCard";

        /// <summary>Height of a row in the list of games, which shows the icon of each game instead of its cover.</summary>
        public const double GameRowHeight = 48;

        /// <summary>Height of a row in the lists of achievements: icon, name and two lines of description.</summary>
        public const double AchievementRowHeight = 58;

        /// <summary>The narrowest the column with the name and description of each achievement gets.</summary>
        public const double AchievementColumnMinWidth = 300;

        /// <summary>The narrowest the column with the name of each game gets.</summary>
        public const double GameColumnMinWidth = 240;

        /// <summary>
        /// The minimum for the column of the names: what it asks for, but never more than 40 % of the table,
        /// so that a narrow window still has room for the dates and the rarity.
        /// </summary>
        public static double GetNameMinWidth(double tableWidth, double wanted)
        {
            if (double.IsNaN(tableWidth) || tableWidth <= 0)
            {
                return wanted;
            }

            return Math.Max(22, Math.Floor(Math.Min(wanted, tableWidth * 0.4)));
        }

        /// <summary>
        /// The colors of the add-on that make it look like two windows. The value is the brush of the
        /// theme to use instead, or null to let the card under them show.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> Brushes = new Dictionary<string, string>
        {
            ["PlayAch.Brush.Window.Background"] = null,
            ["PlayAch.Brush.ControlBorder"] = null,
            ["PlayAch.Brush.Border"] = null,
            ["PlayAch.Brush.Surface"] = "AtrioCardBrush",
            ["PlayAch.Brush.Grid.Background"] = null,
            ["PlayAch.Brush.Grid.HeaderBackground"] = null,
            ["PlayAch.Brush.Grid.RowBackground"] = null,
            ["PlayAch.Brush.Grid.RowHoverBackground"] = "AtrioCardHoverBrush",
            ["PlayAch.Brush.Grid.RowSelectedBackground"] = "HighlightGlyphBrush",
            ["PlayAch.Brush.Progress.Fill"] = "GlyphBrush",
            ["PlayAch.Brush.Progress.Border"] = "GlyphBrush",
            ["PlayAch.Brush.Progress.Track"] = "AtrioCardBrush"
        };

        private static Panel buttonsHome;
        private static FrameworkElement buttons;
        private static bool registered;

        /// <summary>
        /// From now on, every page of the add-on that opens in the main window gets the look.
        /// The buttons of Atrio go into its header, and back to their place while the add-on shows its first steps.
        /// </summary>
        public static void Register(Panel home, FrameworkElement atrioButtons)
        {
            buttonsHome = home;
            buttons = atrioButtons;
            if (!registered)
            {
                EventManager.RegisterClassHandler(typeof(UserControl), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
                registered = true;
            }
        }

        /// <summary>The themes of Atrio say so. The classic one and the ones from other people keep the look of the add-on.</summary>
        public static bool ThemeWantsIt()
        {
            return Application.Current?.TryFindResource("AtrioLibraryPanes") as bool? == true;
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (sender?.GetType().FullName != OverviewType || !(sender is UserControl overview))
            {
                return;
            }

            if (!(Window.GetWindow(overview) is Windows.MainWindow) || !ThemeWantsIt())
            {
                return;
            }

            try
            {
                Apply(overview);
            }
            catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(exc, "Failed to give the achievements page the look of Atrio.");
            }
        }

        private static void Apply(UserControl overview)
        {
            foreach (var pair in Brushes)
            {
                overview.Resources[pair.Key] = pair.Value == null ? (Brush)System.Windows.Media.Brushes.Transparent :
                    Application.Current.TryFindResource(pair.Value) as Brush ?? System.Windows.Media.Brushes.Transparent;
            }

            // The add-on leaves room for the caption of a window, which in Atrio is the bar on top.
            if (overview.FindName("HeaderCaptionSpacerRow") is RowDefinition spacer)
            {
                spacer.Height = new GridLength(0);
            }

            if (overview.Content is Border root)
            {
                root.Padding = new Thickness(20, 6, 20, 14);
            }

            MoveButtons(overview);
            if (overview.FindName("OverviewContent") is Grid content)
            {
                MakeCards(content);
            }

            // The column of captures stays only for whoever has captures of their unlocks, which are all in the recent list.
            var recent = overview.FindName("RecentAchievementsDataGrid") as FrameworkElement;
            var noCaptures = !HasCaptures(recent?.GetType().GetProperty("ItemsSource")?.GetValue(recent) as System.Collections.IEnumerable);
            foreach (var name in new[] { "GameSummariesGridControl", "RecentAchievementsDataGrid", "GameAchievementsGrid" })
            {
                if (overview.FindName(name) is FrameworkElement list)
                {
                    var games = name == "GameSummariesGridControl";
                    SetProperty(list, "FixedRowHeight", (double?)(games ? GameRowHeight : AchievementRowHeight));
                    if (games)
                    {
                        // The icon of each game instead of its cover.
                        SetProperty(list, "UseCoverImages", false);
                    }
                    else
                    {
                        if (noCaptures)
                        {
                            SetProperty(list, "HiddenColumnKeys", new List<string> { "Captures" });
                        }

                        if (name == "GameAchievementsGrid")
                        {
                            // Every row is from the game whose name is on top.
                            SetProperty(list, "ShowGameColumn", false);
                        }
                    }

                    list.Margin = new Thickness(8, 0, 8, 10);
                    WatchColumns(list, name);
                }
            }

            if (overview.FindName("MiniShowcaseStrip") is Border strip)
            {
                strip.BorderThickness = new Thickness(0);
                strip.Margin = new Thickness(0, 12, 0, -14);
            }
        }

        /// <summary>Two cards under the lists, and some room between them instead of the line that splits them.</summary>
        private static void MakeCards(Grid content)
        {
            if (content.ColumnDefinitions.Count != 3)
            {
                return;
            }

            content.ColumnDefinitions[1].Width = new GridLength(14);
            foreach (var splitter in content.Children.OfType<GridSplitter>())
            {
                // Still there to drag, without the line.
                splitter.Opacity = 0;
            }

            foreach (UIElement child in content.Children)
            {
                if (Grid.GetRow(child) != 0)
                {
                    continue;
                }

                // The headers of both lists, inside the cards now.
                if (child is Border header && header.Tag == null && Grid.GetColumn(child) == 0)
                {
                    header.Padding = new Thickness(16, 0, 10, 0);
                }
                else if (child is Grid headers && Grid.GetColumn(child) == 2)
                {
                    foreach (var part in headers.Children.OfType<Border>())
                    {
                        part.Padding = new Thickness(16, 0, 10, 0);
                    }
                }
            }

            if (content.Children.OfType<Border>().Any(a => a.Tag as string == CardTag))
            {
                return;
            }

            foreach (var column in new[] { 0, 2 })
            {
                var card = new Border { Tag = CardTag, IsHitTestVisible = false, SnapsToDevicePixels = true };
                card.SetResourceReference(Border.BackgroundProperty, "AtrioCardBrush");
                card.SetResourceReference(Border.CornerRadiusProperty, "AtrioCardRadius");
                Grid.SetColumn(card, column);
                Grid.SetRowSpan(card, 2);
                Panel.SetZIndex(card, -1);
                content.Children.Insert(0, card);
            }
        }

        /// <summary>
        /// "Update achievements" and "Accounts and settings" take the place of the refresh controls of the add-on,
        /// which do the same with fewer words.
        /// </summary>
        private static void MoveButtons(UserControl overview)
        {
            var refresh = (overview.FindName("RefreshModeSelectionButton") as FrameworkElement)?.Parent as Panel;
            if (buttons == null || !(refresh?.Parent is Panel header))
            {
                return;
            }

            refresh.Visibility = Visibility.Collapsed;
            if (buttons.Parent == header)
            {
                return;
            }

            (buttons.Parent as Panel)?.Children.Remove(buttons);
            buttons.HorizontalAlignment = HorizontalAlignment.Left;
            buttons.VerticalAlignment = VerticalAlignment.Center;
            buttons.Margin = new Thickness(12, 0, 0, 0);
            header.Children.Add(buttons);

            overview.Unloaded += (_, __) => overview.Dispatcher.BeginInvoke(new Action(() =>
            {
                // A page that leaves for good, like when the add-on builds it again, gives the buttons back.
                if (!overview.IsLoaded && buttons.Parent == header && buttonsHome?.IsLoaded == true)
                {
                    header.Children.Remove(buttons);
                    buttons.HorizontalAlignment = HorizontalAlignment.Right;
                    buttons.VerticalAlignment = VerticalAlignment.Top;
                    buttons.Margin = HomeMargin;
                    buttonsHome.Children.Add(buttons);
                }
            }), DispatcherPriority.Background);
        }

        /// <summary>Where the buttons go while the add-on shows its first steps.</summary>
        public static Thickness HomeMargin { get; set; }

        private static readonly DependencyProperty WatchedProperty =
            DependencyProperty.RegisterAttached("Watched", typeof(bool), typeof(AchievementsPageLook), new PropertyMetadata(false));

        /// <summary>
        /// The add-on lays out its columns and reads its display settings when a list loads and when it gets
        /// new rows, like after choosing a game, so the columns are tidied after each of those.
        /// </summary>
        private static void WatchColumns(FrameworkElement list, string name)
        {
            var pending = false;
            void Schedule()
            {
                if (pending)
                {
                    return;
                }

                pending = true;
                list.Dispatcher.BeginInvoke(new Action(() =>
                {
                    pending = false;
                    TidyColumns(list, name);
                }), DispatcherPriority.ContextIdle);
            }

            void Watch()
            {
                Schedule();
                foreach (var grid in ElementTreeHelper.FindVisualChildren<DataGrid>(list))
                {
                    if (!(bool)grid.GetValue(WatchedProperty))
                    {
                        grid.SetValue(WatchedProperty, true);
                        grid.LoadingRow += (_, __) => Schedule();
                        grid.SizeChanged += (_, e) =>
                        {
                            if (e.WidthChanged)
                            {
                                Schedule();
                            }
                        };
                    }
                }
            }

            Watch();
            if (!(bool)list.GetValue(WatchedProperty))
            {
                // A list that waits hidden, like the one of the chosen game, builds its table when it shows.
                list.SetValue(WatchedProperty, true);
                list.IsVisibleChanged += (_, __) =>
                {
                    if (list.IsVisible)
                    {
                        list.Dispatcher.BeginInvoke(new Action(Watch), DispatcherPriority.Loaded);
                    }
                };
            }
        }

        /// <summary>
        /// No column of captures while there are none to show, a narrow column for the icon of each game,
        /// only the bar under the name of a game, and room for the names of the achievements.
        /// </summary>
        private static void TidyColumns(FrameworkElement list, string name)
        {
            try
            {
                var keyProperty = list.GetType().Assembly.GetType("PlayniteAchievements.Views.Helpers.ColumnVisibilityHelper")?.
                    GetField("ColumnKeyProperty", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as DependencyProperty;
                if (keyProperty == null)
                {
                    return;
                }

                var games = name == "GameSummariesGridControl";
                if (games)
                {
                    // The counts by rarity are on the right as soon as the game is chosen.
                    SetProperty(list, "ShowRarityBadgesBelowProgress", false);
                }

                // A list can have more than one table, like the one by categories of a game.
                foreach (var grid in ElementTreeHelper.FindVisualChildren<DataGrid>(list))
                {
                    TidyColumns(grid, keyProperty, name, games);
                }
            }
            catch (Exception exc) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(exc, "Failed to tidy the columns of the achievements page.");
            }
        }

        /// <summary>
        /// The dates of the add-on, in the language of Atrio. The add-on writes them the way Windows does
        /// ("10/10/2026 7:24 PM"), on purpose; only what this page shows changes, not how anything reads a date.
        /// </summary>
        private sealed class LocalDates : IMultiValueConverter
        {
            public readonly IMultiValueConverter Original;

            public LocalDates(IMultiValueConverter original)
            {
                Original = original;
            }

            public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            {
                var mode = values?.Length > 1 ? values[1]?.ToString() : null;
                if (!(values?.Length > 0 && values[0] is DateTime local) || local == DateTime.MinValue || mode == "Relative")
                {
                    return Original.Convert(values, targetType, parameter, culture);
                }

                if (parameter as string == "Exact")
                {
                    // The tooltip with the whole date, for when the cell shows less.
                    return mode == "DateOnly" ? Playnite.Converters.AchievementDateConverter.Format(local, true) : null;
                }

                return Playnite.Converters.AchievementDateConverter.Format(local, mode != "DateOnly");
            }

            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            {
                return Original.ConvertBack(value, targetTypes, parameter, culture);
            }
        }

        private static LocalDates localDates;

        /// <summary>The cells of a table that show a date of the add-on take it written by <see cref="LocalDates"/>.</summary>
        private static void LocalizeDates(DataGrid grid)
        {
            foreach (var text in ElementTreeHelper.FindVisualChildren<TextBlock>(grid))
            {
                Rebind(text, TextBlock.TextProperty);
            }

            foreach (var cell in ElementTreeHelper.FindVisualChildren<DataGridCell>(grid))
            {
                Rebind(cell, FrameworkElement.ToolTipProperty);
            }
        }

        private static void Rebind(DependencyObject target, DependencyProperty property)
        {
            var binding = BindingOperations.GetMultiBinding(target, property);
            if (!(binding?.Converter is IMultiValueConverter converter) || converter is LocalDates || converter.GetType().Name != "DateDisplayModeConverter")
            {
                return;
            }

            if (localDates?.Original != converter)
            {
                localDates = new LocalDates(converter);
            }

            var local = new MultiBinding { Converter = localDates, ConverterParameter = binding.ConverterParameter, Mode = BindingMode.OneWay };
            foreach (var part in binding.Bindings)
            {
                local.Bindings.Add(part);
            }

            BindingOperations.SetBinding(target, property, local);
        }

        /// <summary>
        /// The add-on keeps the minimum width it found on each column the first time, and puts it back each time
        /// it lays the columns out. The minimum of the names goes there too, so that the add-on itself makes room
        /// for it by narrowing the other columns, instead of the table running past the edge of its card.
        /// </summary>
        private static bool SetConfiguredMinWidth(Assembly addon, DataGridColumn column, double width)
        {
            var helper = addon.GetType("PlayniteAchievements.Views.Helpers.ColumnWidthNormalization");
            var table = helper?.GetField("ConfiguredMinWidths", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            var storedType = helper?.GetNestedType("ConfiguredColumnMinWidth", BindingFlags.NonPublic);
            var value = storedType?.GetProperty("Value");
            if (table == null || value == null)
            {
                return false;
            }

            var arguments = new object[] { column, null };
            if (!(bool)table.GetType().GetMethod("TryGetValue").Invoke(table, arguments))
            {
                arguments[1] = Activator.CreateInstance(storedType, true);
                table.GetType().GetMethod("Add").Invoke(table, new[] { column, arguments[1] });
            }

            if (value.GetValue(arguments[1]) is double current && Math.Abs(current - width) < 0.5)
            {
                return false;
            }

            value.SetValue(arguments[1], width);
            return true;
        }

        /// <summary>Asks the control that owns the table to lay its columns out again, to the width of its card.</summary>
        private static void LayOutAgain(DataGrid grid)
        {
            for (DependencyObject up = grid; up != null; up = VisualTreeHelper.GetParent(up))
            {
                if (!(up is UserControl control))
                {
                    continue;
                }

                var field = control.GetType().GetField("_columnPersistence", BindingFlags.NonPublic | BindingFlags.Instance);
                var service = field?.GetValue(control);
                if (service != null)
                {
                    service.GetType().GetMethod("NormalizeToContainer")?.Invoke(service, new object[] { false });
                    return;
                }
            }
        }

        private static void TidyColumns(DataGrid grid, DependencyProperty keyProperty, string name, bool games)
        {
            LocalizeDates(grid);
            var layOutAgain = false;
            foreach (var column in grid.Columns)
            {
                var key = column.GetValue(keyProperty) as string;
                if (games && key == "Captures" && !HasCaptures(grid.ItemsSource))
                {
                    column.Visibility = Visibility.Collapsed;
                }
                else if (key == "Achievement" || key == "GameSummaryName")
                {
                    // The names have room, as long as the table is wide enough for the other columns too.
                    // The list of games fits as it is, and laying it out again would widen the column of the icons.
                    var wanted = GetNameMinWidth(grid.ActualWidth, key == "Achievement" ? AchievementColumnMinWidth : GameColumnMinWidth);
                    if (!games && SetConfiguredMinWidth(keyProperty.OwnerType.Assembly, column, wanted))
                    {
                        layOutAgain = true;
                    }

                    if (column.MinWidth != wanted)
                    {
                        column.MinWidth = wanted;
                    }
                }
                else if (games && key == "Cover" && !(bool)column.GetValue(WatchedProperty) && grid.IsVisible && grid.Items.Count > 0)
                {
                    // Once: from then on the width is the player's, who may drag it.
                    column.Width = new DataGridLength(GameRowHeight + 12);
                    column.SetValue(WatchedProperty, true);
                }
            }

            if (layOutAgain && grid.IsVisible)
            {
                LayOutAgain(grid);
            }
        }

        private static bool HasCaptures(System.Collections.IEnumerable items)
        {
            return items?.Cast<object>().Any(a => a?.GetType().GetProperty("HasCaptures")?.GetValue(a) as bool? == true) == true;
        }

        private static void SetProperty(DependencyObject control, string name, object value)
        {
            var field = control.GetType().GetField(name + "Property", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (field?.GetValue(null) is DependencyProperty property)
            {
                control.SetValue(property, value);
            }
            else
            {
                control.GetType().GetProperty(name)?.SetValue(control, value);
            }
        }
    }
}
