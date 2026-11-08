using Playnite.Atrio;
using Playnite.Commands;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

namespace Playnite.FullscreenApp.ViewModels
{
    /// <summary>
    /// The start of the mode for the couch: the first tab, before the filters, with rows of games like
    /// the start of the desktop. Moving over a game shows its art; A opens its page.
    /// </summary>
    public partial class FullscreenAppViewModel
    {
        private bool homeActive;
        private bool restoringFilter;
        private DispatcherTimer homeTimer;

        private bool themeHasHome;

        /// <summary>
        /// The theme has the rows of the start. Themes of others don't, and keep their list as always:
        /// without this the start would hide nothing and the list would stop following the selection.
        /// </summary>
        public bool ThemeHasHome
        {
            get => themeHasHome;
            set
            {
                themeHasHome = value;
                OnPropertyChanged();
                if (value && GameUnifier.Current?.Settings.FullscreenStartsAtHome != false)
                {
                    HomeActive = true;
                }
                else if (!value)
                {
                    HomeActive = false;
                }
            }
        }

        /// <summary>The rows of the start are showing, instead of the list of a filter.</summary>
        public bool HomeActive
        {
            get => homeActive;
            set
            {
                if (homeActive == value || (value && !ThemeHasHome))
                {
                    return;
                }

                homeActive = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowcaseActive));
                if (value)
                {
                    RefreshHome();
                }
                else if (!GameDetailsVisible)
                {
                    // The list of the filter takes the focus that the start had.
                    GameListFocused = false;
                    GameListFocused = true;
                }

                var settings = GameUnifier.Current?.Settings;
                if (settings != null && settings.FullscreenStartsAtHome != value)
                {
                    settings.FullscreenStartsAtHome = value;
                    settings.Save();
                }
            }
        }

        private List<HomeRow> homeRows = new List<HomeRow>();
        public List<HomeRow> HomeRows
        {
            get => homeRows;
            private set
            {
                homeRows = value;
                OnPropertyChanged();
            }
        }

        private RelayCommand<object> showHomeCommand;
        public RelayCommand<object> ShowHomeCommand => showHomeCommand ?? (showHomeCommand = new RelayCommand<object>(_ => HomeActive = true));

        private RelayCommand<FilterPreset> selectFilterTabCommand;

        /// <summary>A tab of a filter: it leaves the start even when the filter was already the active one.</summary>
        public RelayCommand<FilterPreset> SelectFilterTabCommand => selectFilterTabCommand ?? (selectFilterTabCommand = new RelayCommand<FilterPreset>(a =>
        {
            HomeActive = false;
            ApplyFilterPresetCommand.Execute(a);
        }));

        private RelayCommand<GamesCollectionViewEntry> openHomeGameCommand;

        /// <summary>The page of a game of the start, as A opens it from the list.</summary>
        public RelayCommand<GamesCollectionViewEntry> OpenHomeGameCommand => openHomeGameCommand ?? (openHomeGameCommand = new RelayCommand<GamesCollectionViewEntry>(a =>
        {
            if (a == null)
            {
                return;
            }

            SelectedGame = a;
            if (!GameDetailsVisible)
            {
                ToggleGameDetailsCommand.Execute(null);
            }
        }));

        /// <summary>Leaves the start for the first filter, from the button of the next tab.</summary>
        private bool LeaveHomeForFirstFilter(List<FilterPreset> presets)
        {
            if (!HomeActive)
            {
                return false;
            }

            HomeActive = false;
            if (ActiveFilterPreset != presets[0])
            {
                ActiveFilterPreset = presets[0];
            }

            return true;
        }

        /// <summary>Goes back to the start from the first filter, with the button of the tab before.</summary>
        private bool ReturnToHome(List<FilterPreset> presets)
        {
            if (HomeActive)
            {
                return true;
            }

            if (ActiveFilterPreset == null || presets.IndexOf(ActiveFilterPreset) <= 0)
            {
                HomeActive = true;
                return true;
            }

            return false;
        }

        public async void RefreshHome()
        {
            var library = GamesView?.Items?.ToList() ?? new List<GamesCollectionViewEntry>();
            var rows = HomeRowBuilder.Build(library, Database, GameUnifier.Current?.Settings);
            HomeRows = rows;
            var progress = await AchievementProgress.GetAsync(Extensions);
            if (HomeRows == rows)
            {
                HomeRows = HomeRowBuilder.AddAlmostDone(rows, library, progress);
            }
        }

        /// <summary>Playing moves the rows; anything else that changes in a game would only make them blink.</summary>
        private void WatchLibraryForHome()
        {
            if (Database?.Games == null || homeTimer != null)
            {
                return;
            }

            homeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            homeTimer.Tick += (_, __) =>
            {
                homeTimer.Stop();
                if (HomeActive && !GameDetailsVisible)
                {
                    RefreshHome();
                }
            };

            void later()
            {
                homeTimer.Stop();
                homeTimer.Start();
            }

            Database.Games.ItemCollectionChanged += (_, __) => later();
            Database.Games.ItemUpdated += (_, e) =>
            {
                if (e.UpdatedItems.Any(a => a.OldData.LastActivity != a.NewData.LastActivity))
                {
                    later();
                }
            };
        }
    }
}
