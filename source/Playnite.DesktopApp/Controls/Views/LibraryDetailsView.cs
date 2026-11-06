using Playnite.Behaviors;
using Playnite.Common;
using Playnite.Controls;
using Playnite.Converters;
using Playnite.DesktopApp.ViewModels;
using Playnite.SDK;
using Playnite.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace Playnite.DesktopApp.Controls.Views
{
    public class LibraryDetailsView : BaseGamesView
    {
        static LibraryDetailsView()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(LibraryDetailsView), new FrameworkPropertyMetadata(typeof(LibraryDetailsView)));
        }

        public LibraryDetailsView() : base(DesktopView.Details)
        {
        }

        public LibraryDetailsView(DesktopAppViewModel mainModel) : base (DesktopView.Details, mainModel)
        {
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            if (ListGames != null)
            {
                ScrollViewerBehaviours.SetCustomScrollEnabled(ListGames, true);
                BindingTools.SetBinding(ListGames,
                    ScrollViewerBehaviours.SensitivityProperty,
                    mainModel.AppSettings,
                    $"{nameof(PlayniteSettings.DetailsViewListScrollOptions)}.{nameof(ScrollBehaviorOptions.Sensitivity)}");
                BindingTools.SetBinding(ListGames,
                    ScrollViewerBehaviours.SpeedProperty,
                    mainModel.AppSettings,
                    $"{nameof(PlayniteSettings.DetailsViewListScrollOptions)}.{nameof(ScrollBehaviorOptions.Speed)}",
                    converter: new TicksToTimeSpanConverter());
                BindingTools.SetBinding(ListGames,
                    ScrollViewerBehaviours.SmoothScrollEnabledProperty,
                    mainModel.AppSettings,
                    $"{nameof(PlayniteSettings.DetailsViewListScrollOptions)}.{nameof(ScrollBehaviorOptions.SmoothEnabled)}");

                VirtualizingPanel.SetCacheLengthUnit(ListGames, VirtualizationCacheLengthUnit.Item);
                VirtualizingPanel.SetCacheLength(ListGames, new VirtualizationCacheLength(5));
                VirtualizingPanel.SetScrollUnit(ListGames, ScrollUnit.Pixel);
                VirtualizingPanel.SetIsVirtualizingWhenGrouping(ListGames, true);
                VirtualizingPanel.SetVirtualizationMode(ListGames, VirtualizationMode.Recycling);

                // Any game picked from the list takes over the rest of the library, even the one already selected.
                ListGames.PreviewMouseLeftButtonUp += (_, e) =>
                {
                    if (IsClickOnGame(e))
                    {
                        mainModel.Pane = LibraryPane.Game;
                    }
                };

                // Only with the keyboard: filters and syncs also move the selection, and those pick nothing.
                ListGames.SelectionChanged += (_, e) =>
                {
                    if (e.AddedItems.Count > 0 && ListGames.IsKeyboardFocusWithin && Keyboard.Modifiers == ModifierKeys.None &&
                        InputManager.Current.MostRecentInputDevice is KeyboardDevice)
                    {
                        mainModel.Pane = LibraryPane.Game;
                    }
                };
            }
        }
    }
}