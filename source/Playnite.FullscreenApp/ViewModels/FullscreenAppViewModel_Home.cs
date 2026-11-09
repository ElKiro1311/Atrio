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

        /// <summary>The rows show the collections instead of the start: the tab right after the start.</summary>
        private bool collectionsKind;

        private GameCollection openCollection;

        /// <summary>The collection whose games the list shows, from the tile at the end of its row.</summary>
        public GameCollection OpenCollection
        {
            get => openCollection;
            private set
            {
                if (openCollection == value)
                {
                    return;
                }

                openCollection = value;
                if (GamesView != null)
                {
                    GamesView.Collection = value;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(CollectionsTabActive));
                OnPropertyChanged(nameof(CollectionsTabName));
            }
        }

        private bool controllerOnly;

        /// <summary>The tab of the games played with a controller: the whole library, only with those.</summary>
        public bool ControllerOnly
        {
            get => controllerOnly;
            private set
            {
                if (controllerOnly == value)
                {
                    return;
                }

                controllerOnly = value;
                if (GamesView != null)
                {
                    GamesView.ControllerOnly = value;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(ControllerTabActive));
            }
        }

        public bool ControllerTabActive => !HomeActive && controllerOnly;

        private RelayCommand<object> showControllerCommand;

        /// <summary>The tab right after the collections: every game that Steam or PCGamingWiki say is played with a controller.</summary>
        public RelayCommand<object> ShowControllerCommand => showControllerCommand ?? (showControllerCommand = new RelayCommand<object>(_ => ShowControllerGames()));

        private void ShowControllerGames()
        {
            // The filter of the whole library goes under it, like for a collection; choosing it leaves this tab, so it goes first.
            var whole = SortedFilterFullscreenPresets?.FirstOrDefault(DefaultFilterPresets.IsWholeLibrary) ??
                Database?.FilterPresets?.FirstOrDefault(DefaultFilterPresets.IsWholeLibrary);
            if (whole != null)
            {
                if (ActiveFilterPreset != whole)
                {
                    ActiveFilterPreset = whole;
                }
            }
            else
            {
                AppSettings.Fullscreen.FilterSettings.ClearFilters();
            }

            HomeActive = false;
            OpenCollection = null;
            ControllerOnly = true;

            // Pressed again while it already shows, the tab unticks itself: it's told again that it's the one.
            OnPropertyChanged(nameof(ControllerTabActive));
        }

        /// <summary>The tab of the start is checked: the rows of the start are showing.</summary>
        public bool StartTabActive => HomeActive && !collectionsKind;

        /// <summary>The tab of the collections is checked: their rows, or the games of one of them.</summary>
        public bool CollectionsTabActive => (HomeActive && collectionsKind) || (!HomeActive && OpenCollection != null);

        /// <summary>"Collections", or "Collections · Favorites" while the games of one are showing.</summary>
        public string CollectionsTabName => OpenCollection == null ?
            ResourceProvider.GetString("LOCAtrioCouchCollections") :
            string.Format(ResourceProvider.GetString("LOCAtrioCouchCollectionTab"), OpenCollection.Name);

        private void SetCollectionsKind(bool value)
        {
            collectionsKind = value;
            OnPropertyChanged(nameof(StartTabActive));
            OnPropertyChanged(nameof(CollectionsTabActive));
        }

        private RelayCommand<object> showCollectionsCommand;

        /// <summary>The tab of the collections: one row for each.</summary>
        public RelayCommand<object> ShowCollectionsCommand => showCollectionsCommand ?? (showCollectionsCommand = new RelayCommand<object>(_ => ShowCollections()));

        private void ShowCollections()
        {
            OpenCollection = null;
            ControllerOnly = false;
            SetCollectionsKind(true);
            if (HomeActive)
            {
                RefreshHome();
            }
            else
            {
                HomeActive = true;
            }
        }

        private RelayCommand<HomeTile> openHomeTileCommand;

        /// <summary>A tile of the rows: the page of its game, or the whole collection for the last tile of a collection.</summary>
        public RelayCommand<HomeTile> OpenHomeTileCommand => openHomeTileCommand ?? (openHomeTileCommand = new RelayCommand<HomeTile>(a =>
        {
            if (a?.Collection != null)
            {
                OpenCollectionGames(a.Collection);
            }
            else if (a?.Game != null)
            {
                OpenHomeGameCommand.Execute(a.Game);
            }
        }));

        /// <summary>
        /// The list shows every game of the collection, with the filter of the whole library under it: from the tabs,
        /// or the one hidden from them. Without any, the filters are emptied, so none hides games of the collection.
        /// </summary>
        private void OpenCollectionGames(GameCollection collection)
        {
            var whole = SortedFilterFullscreenPresets?.FirstOrDefault(DefaultFilterPresets.IsWholeLibrary) ??
                Database?.FilterPresets?.FirstOrDefault(DefaultFilterPresets.IsWholeLibrary);
            if (whole != null)
            {
                if (ActiveFilterPreset != whole)
                {
                    ActiveFilterPreset = whole;
                }
            }
            else
            {
                AppSettings.Fullscreen.FilterSettings.ClearFilters();
            }

            HomeActive = false;
            ControllerOnly = false;
            OpenCollection = collection;
        }

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
                OnPropertyChanged(nameof(StartTabActive));
                OnPropertyChanged(nameof(CollectionsTabActive));
                OnPropertyChanged(nameof(ControllerTabActive));
                if (value)
                {
                    OpenCollection = null;
                    ControllerOnly = false;
                    RefreshHome();
                }
                else
                {
                    HomeEmptyText = null;
                }

                if (!value && !GameDetailsVisible)
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

        private string homeEmptyText;

        /// <summary>What the rows say when there's nothing to show, like no collections yet.</summary>
        public string HomeEmptyText
        {
            get => homeEmptyText;
            private set
            {
                homeEmptyText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HomeIsEmpty));
            }
        }

        public bool HomeIsEmpty => !string.IsNullOrEmpty(homeEmptyText);

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
        public RelayCommand<object> ShowHomeCommand => showHomeCommand ?? (showHomeCommand = new RelayCommand<object>(_ => ShowStart()));

        private void ShowStart()
        {
            OpenCollection = null;
            ControllerOnly = false;
            var wasCollections = collectionsKind;
            SetCollectionsKind(false);
            if (HomeActive)
            {
                if (wasCollections)
                {
                    RefreshHome();
                }
            }
            else
            {
                HomeActive = true;
            }
        }

        private RelayCommand<FilterPreset> selectFilterTabCommand;

        /// <summary>A tab of a filter: it leaves the start even when the filter was already the active one.</summary>
        public RelayCommand<FilterPreset> SelectFilterTabCommand => selectFilterTabCommand ?? (selectFilterTabCommand = new RelayCommand<FilterPreset>(a =>
        {
            HomeActive = false;
            OpenCollection = null;
            ControllerOnly = false;
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

        /// <summary>
        /// The button of the next tab: from the start to the collections, from the collections (or the games of
        /// one) to the games with a controller, and from there to the first filter.
        /// </summary>
        private bool LeaveHomeForFirstFilter(List<FilterPreset> presets)
        {
            if (HomeActive && !collectionsKind && ThemeHasHome)
            {
                ShowCollections();
                return true;
            }

            if (ThemeHasHome && ((HomeActive && collectionsKind) || OpenCollection != null))
            {
                ShowControllerGames();
                return true;
            }

            if (!HomeActive && OpenCollection == null && !ControllerOnly)
            {
                return false;
            }

            HomeActive = false;
            OpenCollection = null;
            ControllerOnly = false;
            if (ActiveFilterPreset != presets[0])
            {
                ActiveFilterPreset = presets[0];
            }
            else
            {
                GamesView?.CollectionView?.Refresh();
            }

            return true;
        }

        /// <summary>
        /// The button of the tab before: from the first filter to the games with a controller, from there or from the
        /// games of a collection back to the collections, and from the collections to the start.
        /// </summary>
        private bool ReturnToHome(List<FilterPreset> presets)
        {
            if (HomeActive)
            {
                if (collectionsKind)
                {
                    ShowStart();
                }

                return true;
            }

            if (ControllerOnly || OpenCollection != null)
            {
                if (ThemeHasHome)
                {
                    ShowCollections();
                }
                else
                {
                    HomeActive = true;
                }

                return true;
            }

            if (ActiveFilterPreset == null || presets.IndexOf(ActiveFilterPreset) <= 0)
            {
                if (ThemeHasHome)
                {
                    ShowControllerGames();
                }
                else
                {
                    HomeActive = true;
                }

                return true;
            }

            return false;
        }

        public async void RefreshHome()
        {
            var library = GamesView?.Items?.ToList() ?? new List<GamesCollectionViewEntry>();
            if (collectionsKind)
            {
                HomeRows = HomeRowBuilder.BuildCollections(library, Database, GameUnifier.Current?.Settings);
                HomeEmptyText = HomeRows.Count == 0 ? ResourceProvider.GetString("LOCAtrioCouchNoCollections") : null;
                return;
            }

            HomeEmptyText = null;
            var rows = HomeRowBuilder.Build(library, Database, GameUnifier.Current?.Settings);
            HomeRows = rows;
            var progress = await AchievementProgress.GetAsync(Extensions);
            if (HomeRows == rows && !collectionsKind)
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
