using Playnite.Atrio;
using Playnite.Common;
using Playnite.DesktopApp.Windows;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Playnite.DesktopApp.ViewModels
{
    public class CollectionTile : ObservableObject
    {
        public GameCollection Collection { get; set; }
        public string CountText { get; set; }
        public string IconText { get; set; }
        public GamesCollectionViewEntry FrontCover { get; set; }
        public GamesCollectionViewEntry LeftCover { get; set; }
        public GamesCollectionViewEntry RightCover { get; set; }
        public bool HasFrontCover => FrontCover != null;
        public bool HasLeftCover => LeftCover != null;
        public bool HasRightCover => RightCover != null;
        public bool IsDynamic => Collection.Kind == GameCollectionKind.Dynamic;

        private bool isDropTarget;
        public bool IsDropTarget
        {
            get => isDropTarget;
            set => SetValue(ref isDropTarget, value);
        }
    }

    /// <summary>
    /// The page with every collection and the bar over the games of the one that is open. A collection is
    /// not a filter of the user: it comes and goes with its page and leaves the filters as they were.
    /// </summary>
    public class CollectionsViewModel : ObservableObject
    {
        private class LibraryGame
        {
            public GamesCollectionViewEntry Entry { get; set; }
            public IReadOnlyList<Game> Copies { get; set; }
        }

        /// <summary>Filters being chosen for a dynamic collection, in the list of every game.</summary>
        private class FilterDraft
        {
            /// <summary>Null for a new collection.</summary>
            public GameCollection Target { get; set; }
            public bool FilterPanelWasVisible { get; set; }
        }

        private const string CollectionIcon = "";
        private const string FavoritesIcon = "";
        private const string HiddenIcon = "";
        private const string UncategorizedIcon = "";

        private readonly DesktopAppViewModel main;
        private readonly DispatcherTimer refreshTimer;
        private FilterDraft draft;
        private bool watching;

        private static AtrioSettings Settings => GameUnifier.Current.Settings;
        private GameCollection Active => main.ActiveCollection;

        private List<CollectionTile> tiles;
        public List<CollectionTile> Tiles
        {
            get => tiles;
            set => SetValue(ref tiles, value);
        }

        /// <summary>Dragging and choosing filters need the list of games next to this page.</summary>
        public bool HasLibraryPanes => DesktopAppViewModel.UsesLibraryPanes;

        public bool HasActive => Active != null;
        public string ActiveName => Active?.Name;
        public bool CanChooseGames => Active?.Kind == GameCollectionKind.Manual;

        public string ActiveDetails
        {
            get
            {
                var count = GetCountText(main.GamesView?.CollectionView?.Count ?? 0);
                return Active?.Kind == GameCollectionKind.Dynamic ?
                    $"{count} · {ResourceProvider.GetString("LOCAtrioCollectionDynamicTag")}" :
                    count;
            }
        }

        public bool IsDrafting => draft != null;

        public string DraftText => draft?.Target == null ?
            ResourceProvider.GetString("LOCAtrioCollectionDraftNew") :
            string.Format(ResourceProvider.GetString("LOCAtrioCollectionDraftEdit"), draft.Target.Name);

        public bool CanSaveFilters => main.AppSettings.FilterSettings.IsActive;

        /// <summary>Any filters can become a collection, the same way a search can in Steam.</summary>
        public bool OffersSavingFilters =>
            DesktopAppViewModel.UsesLibraryPanes && main.IsAllGamesPane && draft == null && CanSaveFilters;

        public RelayCommand<object> NewCollectionCommand { get; }
        public RelayCommand<object> NewDynamicCollectionCommand { get; }
        public RelayCommand<CollectionTile> OpenCommand { get; }
        public RelayCommand<object> ReturnCommand { get; }
        public RelayCommand<object> BackCommand { get; }
        public RelayCommand<object> CloseCommand { get; }
        public RelayCommand<object> ChooseGamesCommand { get; }
        public RelayCommand<FrameworkElement> MoreCommand { get; }
        public RelayCommand<object> SaveFiltersCommand { get; }
        public RelayCommand<object> CancelDraftCommand { get; }
        public RelayCommand<object> ImportSteamCommand { get; }

        public CollectionsViewModel(DesktopAppViewModel main)
        {
            this.main = main;
            ImportSteamCommand = new RelayCommand<object>(_ =>
            {
                try
                {
                    ImportFromSteam();
                }
                catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    LogManager.GetLogger().Error(e, "Failed to import the collections of Steam.");
                    main.Dialogs.ShowErrorMessage(e.Message, ResourceProvider.GetString("LOCAtrioSteamCollectionsTitle"));
                }
            });
            NewCollectionCommand = new RelayCommand<object>(_ =>
            {
                if (CreateCollection(null) != null)
                {
                    Refresh();
                }
            });
            NewDynamicCollectionCommand = new RelayCommand<object>(_ => BeginDraft(null));
            OpenCommand = new RelayCommand<CollectionTile>(a => Open(a?.Collection));
            ReturnCommand = new RelayCommand<object>(_ => Open(Active));
            BackCommand = new RelayCommand<object>(_ => main.Pane = LibraryPane.Collections);
            CloseCommand = new RelayCommand<object>(_ => main.CloseCollection());
            ChooseGamesCommand = new RelayCommand<object>(_ => ChooseGames(Active));
            MoreCommand = new RelayCommand<FrameworkElement>(a => ShowMenu(Active, a));
            SaveFiltersCommand = new RelayCommand<object>(_ => SaveFilters());
            CancelDraftCommand = new RelayCommand<object>(_ =>
            {
                var target = draft?.Target;
                CancelDraft();
                if (target != null)
                {
                    Open(target);
                }
                else
                {
                    main.Pane = LibraryPane.Collections;
                }
            });

            // A sync adds games one by one: the page waits for them to stop arriving.
            refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            refreshTimer.Tick += (_, __) =>
            {
                refreshTimer.Stop();
                if (!main.IsDisposing)
                {
                    Refresh();
                }
            };

            main.AppSettings.FilterSettings.FilterChanged += (_, __) =>
            {
                // The list refreshes after this event: reading it right now would give the previous state.
                Application.Current?.Dispatcher.BeginInvoke(new Action(UpdateBar), DispatcherPriority.Background);
            };
        }

        public static string GetCountText(int count)
        {
            return count == 1 ?
                ResourceProvider.GetString("LOCAtrioOneGame") :
                string.Format(ResourceProvider.GetString("LOCAtrioGamesCount"), count.ToString("N0", Localization.ApplicationLanguageCultureInfo));
        }

        private static string GetIcon(GameCollection collection)
        {
            switch (collection.Kind)
            {
                case GameCollectionKind.Favorites:
                    return FavoritesIcon;
                case GameCollectionKind.Hidden:
                    return HiddenIcon;
                case GameCollectionKind.Uncategorized:
                    return UncategorizedIcon;
                default:
                    return CollectionIcon;
            }
        }

        public void UpdateBar()
        {
            OnPropertyChanged(nameof(HasActive));
            OnPropertyChanged(nameof(ActiveName));
            OnPropertyChanged(nameof(ActiveDetails));
            OnPropertyChanged(nameof(CanChooseGames));
            OnPropertyChanged(nameof(IsDrafting));
            OnPropertyChanged(nameof(DraftText));
            OnPropertyChanged(nameof(CanSaveFilters));
            OnPropertyChanged(nameof(OffersSavingFilters));
        }

        /// <summary>The list of the library follows the collection that is open.</summary>
        public void OnActiveCollectionChanged()
        {
            var view = (INotifyCollectionChanged)main.GamesView.CollectionView;
            view.CollectionChanged -= GamesView_CollectionChanged;
            if (Active != null)
            {
                view.CollectionChanged += GamesView_CollectionChanged;
            }

            UpdateBar();
        }

        private void GamesView_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(ActiveDetails));
            main.StoreFilter.UpdateCount();
        }

        /// <summary>From the start: what happens to the collection that is open can come from anywhere.</summary>
        public void WatchDatabase()
        {
            main.Database.Categories.ItemCollectionChanged += (_, __) => OnCollectionsChanged();
            main.Database.Categories.ItemUpdated += (_, __) => OnCollectionsChanged();
            main.Database.FilterPresets.ItemCollectionChanged += (_, __) => OnCollectionsChanged();
            main.Database.FilterPresets.ItemUpdated += (_, __) => OnCollectionsChanged();
        }

        private void OnCollectionsChanged()
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (main.IsDisposing)
                {
                    return;
                }

                if (watching)
                {
                    RefreshSoon();
                }

                if (Active != null)
                {
                    var current = GameCollections.Find(main.Database, Settings, Active.Id);
                    if (current == null)
                    {
                        main.Pane = LibraryPane.Collections;
                    }
                    else if (current.Name != Active.Name)
                    {
                        main.ReplaceCollection(current);
                    }
                }
            }));
        }

        private void RefreshSoon()
        {
            refreshTimer.Dispatcher.BeginInvoke(new Action(() =>
            {
                refreshTimer.Stop();
                refreshTimer.Start();
            }));
        }

        private void Games_ItemCollectionChanged(object sender, ItemCollectionChangedEventArgs<Game> e)
        {
            RefreshSoon();
        }

        private void Games_ItemUpdated(object sender, ItemUpdatedEventArgs<Game> e)
        {
            RefreshSoon();
        }

        public void StartWatching()
        {
            watching = true;
            if (main.Database?.Games != null)
            {
                main.Database.Games.ItemCollectionChanged -= Games_ItemCollectionChanged;
                main.Database.Games.ItemCollectionChanged += Games_ItemCollectionChanged;
                main.Database.Games.ItemUpdated -= Games_ItemUpdated;
                main.Database.Games.ItemUpdated += Games_ItemUpdated;
            }
        }

        public void StopWatching()
        {
            watching = false;
            refreshTimer.Stop();
            if (main.Database?.Games != null)
            {
                main.Database.Games.ItemCollectionChanged -= Games_ItemCollectionChanged;
                main.Database.Games.ItemUpdated -= Games_ItemUpdated;
            }
        }

        private List<LibraryGame> GetLibrary()
        {
            var unifier = GameUnifier.Current;
            var seen = new HashSet<Guid>();
            return main.GamesView.Items.
                Where(a => unifier?.IsSecondaryCopy(a.Id) != true && seen.Add(a.Id)).
                Select(a => new LibraryGame { Entry = a, Copies = GameCollections.GetCopies(a.Game, unifier) }).
                ToList();
        }

        private static List<GamesCollectionViewEntry> GetMembers(GameCollection collection, List<LibraryGame> library)
        {
            return library.
                Where(a => a.Entry.Hidden == collection.ShowsHiddenGames && collection.Contains(a.Copies)).
                Select(a => a.Entry).
                OrderByDescending(a => a.LastActivity ?? DateTime.MinValue).
                ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).
                ToList();
        }

        /// <summary>The games of a collection, the ones played last first.</summary>
        public List<GamesCollectionViewEntry> GetMembers(GameCollection collection)
        {
            return main.GamesView?.Items == null ? new List<GamesCollectionViewEntry>() : GetMembers(collection, GetLibrary());
        }

        public List<GameCollection> GetHomeCollections()
        {
            var all = GameCollections.GetAll(main.Database, Settings);
            return Settings.HomeCollections.Select(a => all.FirstOrDefault(b => b.Id == a)).Where(a => a != null).ToList();
        }

        public void Refresh()
        {
            if (main.GamesView?.Items == null)
            {
                return;
            }

            var library = GetLibrary();
            var list = new List<CollectionTile>();
            var hasManual = main.Database.Categories.Any();
            foreach (var collection in GameCollections.GetAll(main.Database, Settings))
            {
                var members = GetMembers(collection, library);
                if ((collection.Kind == GameCollectionKind.Hidden && members.Count == 0) ||
                    (collection.Kind == GameCollectionKind.Uncategorized && !hasManual))
                {
                    continue;
                }

                list.Add(new CollectionTile
                {
                    Collection = collection,
                    CountText = GetCountText(members.Count),
                    IconText = GetIcon(collection),
                    FrontCover = members.FirstOrDefault(),
                    LeftCover = members.Skip(1).FirstOrDefault(),
                    RightCover = members.Skip(2).FirstOrDefault()
                });
            }

            Tiles = list;
        }

        public void Open(GameCollection collection)
        {
            if (collection == null)
            {
                return;
            }

            if (DesktopAppViewModel.UsesLibraryPanes)
            {
                main.ShowCollection(collection);
            }
            else
            {
                ApplyAsFilter(collection);
            }
        }

        /// <summary>A theme without the panes of the library has no bar for a collection, but all of them show the filters.</summary>
        private void ApplyAsFilter(GameCollection collection)
        {
            main.ClearFilters();
            var filter = main.AppSettings.FilterSettings;
            switch (collection.Kind)
            {
                case GameCollectionKind.Favorites:
                    filter.Favorite = true;
                    break;
                case GameCollectionKind.Hidden:
                    filter.Hidden = true;
                    break;
                case GameCollectionKind.Manual:
                    filter.Category = new IdItemFilterItemProperties(new List<Guid> { collection.Id });
                    break;
                case GameCollectionKind.Uncategorized:
                    filter.Category = new IdItemFilterItemProperties(new List<Guid> { Guid.Empty });
                    break;
                case GameCollectionKind.Dynamic:
                    main.ApplyFilterPreset(collection.Id);
                    break;
            }

            main.SwitchToLibraryView();
        }

        private string AskName(string title, string current)
        {
            var answer = main.Dialogs.SelectString(
                ResourceProvider.GetString("LOCAtrioCollectionName"),
                ResourceProvider.GetString(title),
                current);
            var name = answer.Result ? answer.SelectedString?.Trim() : null;
            return name.IsNullOrEmpty() ? null : name;
        }

        /// <summary>Asks for the name of a new collection and puts the games in it, if any.</summary>
        public GameCollection CreateCollection(List<Game> games)
        {
            var name = AskName("LOCAtrioCollectionNew", string.Empty);
            if (name == null)
            {
                return null;
            }

            var category = main.Database.Categories.Add(name);
            if (games.HasItems())
            {
                GameCollections.AddGames(main.Database, category.Id, games, GameUnifier.Current);
            }

            return GameCollection.FromCategory(category);
        }

        public void AddGames(GameCollection collection, IEnumerable<Guid> gameIds)
        {
            var games = gameIds.Select(a => main.Database.Games[a]).Where(a => a != null).ToList();
            if (collection.Kind == GameCollectionKind.Favorites)
            {
                main.GamesEditor.SetFavoriteGames(games, true);
            }
            else if (collection.Kind == GameCollectionKind.Manual)
            {
                GameCollections.AddGames(main.Database, collection.Id, games, GameUnifier.Current);
            }

            // Right away: the game has just been seen going into it.
            Refresh();
        }

        public void ChooseGames(GameCollection collection)
        {
            if (collection?.Kind != GameCollectionKind.Manual)
            {
                return;
            }

            var unifier = GameUnifier.Current;
            var options = GetLibrary().
                Where(a => !a.Entry.Hidden).
                Select(a => new CollectionGameOption(a.Entry.Game, a.Entry.Name, GameCollections.IsMember(collection.Id, a.Entry.Game, unifier))).
                ToList();

            var model = new CollectionGamesViewModel(new CollectionGamesWindowFactory(), collection.Name, options);
            if (model.OpenView() == true)
            {
                using (main.Database.BufferedUpdate())
                {
                    GameCollections.AddGames(main.Database, collection.Id, options.Where(a => a.IsSelected).Select(a => a.Game), unifier);
                    GameCollections.RemoveGames(main.Database, collection.Id, options.Where(a => !a.IsSelected).Select(a => a.Game), unifier);
                }
            }
        }

        public void ShowMenu(GameCollection collection, FrameworkElement target)
        {
            if (collection != null && target != null)
            {
                CreateMenu(collection, target).IsOpen = true;
            }
        }

        private ContextMenu CreateMenu(GameCollection collection, FrameworkElement target)
        {
            var menu = new ContextMenu { PlacementTarget = target, Placement = target is Button ? PlacementMode.Bottom : PlacementMode.MousePoint };
            void add(string textKey, Action action, bool isChecked = false)
            {
                var item = new MenuItem { Header = ResourceProvider.GetString(textKey), IsChecked = isChecked };
                item.Click += (_, __) => action();
                menu.Items.Add(item);
            }

            if (collection.Kind == GameCollectionKind.Manual)
            {
                add("LOCAtrioCollectionChooseGames", () => ChooseGames(collection));
            }
            else if (collection.Kind == GameCollectionKind.Dynamic)
            {
                add("LOCAtrioCollectionEditFilters", () => BeginDraft(collection));
            }

            add("LOCAtrioCollectionShowOnHome", () => ToggleHomeShelf(collection), Settings.HomeCollections.Contains(collection.Id));
            if (collection.IsUserMade)
            {
                add("LOCAtrioCollectionRename", () => Rename(collection));
                menu.Items.Add(new Separator());
                add("LOCAtrioCollectionDelete", () => Remove(collection));
            }

            return menu;
        }

        public void ToggleHomeShelf(GameCollection collection)
        {
            if (!Settings.HomeCollections.Remove(collection.Id))
            {
                Settings.HomeCollections.Add(collection.Id);
            }

            Settings.Save();
            if (main.IsHomePane)
            {
                main.HomeModel.Refresh();
            }
        }

        public void Rename(GameCollection collection)
        {
            var name = AskName("LOCAtrioCollectionRename", collection.Name);
            if (name != null)
            {
                Rename(collection, name);
            }
        }

        public void Rename(GameCollection collection, string name)
        {
            if (collection.Kind == GameCollectionKind.Dynamic)
            {
                var preset = main.Database.FilterPresets[collection.Id];
                if (preset != null)
                {
                    preset.Name = name;
                    main.Database.FilterPresets.Update(preset);
                }

                return;
            }

            var taken = main.Database.Categories.Any(a => a.Id != collection.Id && string.Equals(a.Name, name, StringComparison.CurrentCultureIgnoreCase));
            var category = main.Database.Categories[collection.Id];
            if (taken)
            {
                main.Dialogs.ShowErrorMessage(string.Format(ResourceProvider.GetString("LOCAtrioCollectionNameTaken"), name), string.Empty);
            }
            else if (category != null)
            {
                category.Name = name;
                main.Database.Categories.Update(category);
            }
        }

        /// <summary>"20 collections", "6 collections and favorites" or "Only favorites".</summary>
        private static string GetAccountText(SteamCollectionAccount account)
        {
            var lists = account.Collections.Count(a => !a.IsFavorites);
            var favorites = account.Collections.Any(a => a.IsFavorites && a.AppIds.Count > 0);
            if (lists == 0)
            {
                return ResourceProvider.GetString("LOCAtrioSteamCollectionsOnlyFavorites");
            }

            return string.Format(ResourceProvider.GetString(favorites ? "LOCAtrioSteamCollectionsAccountFavorites" : "LOCAtrioSteamCollectionsAccount"), lists);
        }

        /// <summary>
        /// Brings the collections of the library of Steam: each one becomes a collection with the games of it that are
        /// in the library, and a collection with the same name gets the ones it is missing. Nothing is removed from any.
        /// The favorites of Steam can become favorites too, if the player says so. Steam itself is only read.
        /// </summary>
        public void ImportFromSteam()
        {
            var title = ResourceProvider.GetString("LOCAtrioSteamCollectionsTitle");
            var accounts = SteamCollections.Read(SteamCollections.FindSteamPath());
            if (accounts.Count == 0)
            {
                main.Dialogs.ShowMessage(ResourceProvider.GetString("LOCAtrioSteamCollectionsNone"), title);
                return;
            }

            var account = accounts[0];
            if (accounts.Count > 1)
            {
                var options = accounts.Select(a => new GenericItemOption(a.Name, GetAccountText(a))).ToList();
                var chosen = main.Dialogs.ChooseItemWithSearch(
                    options,
                    search => options.Where(a => search.IsNullOrEmpty() || a.Name.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList(),
                    null,
                    ResourceProvider.GetString("LOCAtrioSteamCollectionsChooseAccount"));
                if (chosen == null)
                {
                    return;
                }

                account = accounts[options.IndexOf(chosen)];
            }

            // The games of Steam in the library, by their id there.
            var steamGames = main.Database.Games.
                Where(a => a.PluginId == SteamContent.SteamPluginId && !a.GameId.IsNullOrEmpty()).
                GroupBy(a => a.GameId).
                ToDictionary(a => a.Key, a => a.First());
            List<Game> GamesOf(SteamCollection collection) => collection.AppIds.
                Where(steamGames.ContainsKey).
                Select(a => steamGames[a]).
                ToList();

            // A collection with none of its games in the library would only come over empty.
            var named = account.Collections.Where(a => !a.IsDynamic && !a.IsFavorites && !a.Name.IsNullOrWhiteSpace()).ToList();
            var lists = named.Where(a => GamesOf(a).Count > 0).ToList();
            var empty = named.Count - lists.Count;
            var dynamic = account.Collections.Count(a => a.IsDynamic);
            var favorites = account.Collections.FirstOrDefault(a => a.IsFavorites);
            var favoriteGames = favorites == null ? new List<Game>() : GamesOf(favorites);
            if (lists.Count == 0 && favoriteGames.Count == 0)
            {
                main.Dialogs.ShowMessage(ResourceProvider.GetString("LOCAtrioSteamCollectionsNone"), title);
                return;
            }

            var summary = new System.Text.StringBuilder();
            summary.AppendLine(string.Format(ResourceProvider.GetString("LOCAtrioSteamCollectionsAsk"), lists.Count, account.Name));
            summary.AppendLine();
            foreach (var collection in lists.Take(25))
            {
                summary.AppendLine($"• {collection.Name} ({GamesOf(collection).Count})");
            }

            if (lists.Count > 25)
            {
                summary.AppendLine("• …");
            }

            summary.AppendLine();
            summary.AppendLine(ResourceProvider.GetString("LOCAtrioSteamCollectionsMerge"));
            if (dynamic > 0)
            {
                summary.AppendLine(string.Format(ResourceProvider.GetString("LOCAtrioSteamCollectionsDynamic"), dynamic));
            }

            if (empty > 0)
            {
                summary.AppendLine(string.Format(ResourceProvider.GetString("LOCAtrioSteamCollectionsEmpty"), empty));
            }

            if (lists.Count > 0 && main.Dialogs.ShowMessage(summary.ToString().TrimEnd(), title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            var created = 0;
            var filled = 0;
            var missing = 0;
            foreach (var collection in lists)
            {
                var games = GamesOf(collection);
                missing += collection.AppIds.Count - games.Count;
                var category = main.Database.Categories.FirstOrDefault(a => string.Equals(a.Name, collection.Name, StringComparison.CurrentCultureIgnoreCase));
                if (category == null)
                {
                    category = main.Database.Categories.Add(collection.Name);
                    created++;
                }

                if (games.Count > 0)
                {
                    GameCollections.AddGames(main.Database, category.Id, games, GameUnifier.Current);
                    filled += games.Count;
                }
            }

            var newFavorites = favoriteGames.Where(a => !a.Favorite).ToList();
            if (newFavorites.Count > 0 && main.Dialogs.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCAtrioSteamCollectionsFavoritesAsk"), newFavorites.Count),
                    title,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                main.GamesEditor.SetFavoriteGames(newFavorites, true);
            }

            Refresh();
            if (lists.Count > 0)
            {
                var done = string.Format(ResourceProvider.GetString("LOCAtrioSteamCollectionsDone"), lists.Count, created, filled);
                if (missing > 0)
                {
                    done += Environment.NewLine + Environment.NewLine + string.Format(ResourceProvider.GetString("LOCAtrioSteamCollectionsMissing"), missing);
                }

                main.Dialogs.ShowMessage(done, title);
            }
        }

        public void Remove(GameCollection collection)
        {
            var answer = main.Dialogs.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCAtrioCollectionDeleteAsk"), collection.Name),
                ResourceProvider.GetString("LOCAtrioCollectionDelete"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
            {
                Delete(collection);
            }
        }

        public void Delete(GameCollection collection)
        {
            if (Active?.Id == collection.Id)
            {
                main.Pane = LibraryPane.Collections;
            }

            Settings.HomeCollections.Remove(collection.Id);
            Settings.DynamicCollections.Remove(collection.Id);
            Settings.Save();
            if (collection.Kind == GameCollectionKind.Manual)
            {
                using (main.Database.BufferedUpdate())
                {
                    main.Database.Categories.Remove(collection.Id);
                }
            }
            else if (collection.Kind == GameCollectionKind.Dynamic)
            {
                main.Database.FilterPresets.Remove(collection.Id);
            }

            Refresh();
        }

        /// <summary>Choosing the filters happens over every game, with the usual panel, and the bar keeps the way out.</summary>
        public void BeginDraft(GameCollection target)
        {
            var wasVisible = draft?.FilterPanelWasVisible ?? main.AppSettings.FilterPanelVisible;
            draft = null;
            main.ClearFilters();
            var preset = target == null ? null : main.Database.FilterPresets[target.Id];
            if (preset?.Settings != null)
            {
                main.AppSettings.FilterSettings.ApplyFilter(preset.Settings);
            }

            draft = new FilterDraft { Target = target, FilterPanelWasVisible = wasVisible };
            main.ShowLibraryList();
            main.AppSettings.FilterPanelVisible = true;
            UpdateBar();
        }

        private void EndDraft()
        {
            var finished = draft;
            draft = null;
            main.ClearFilters();
            main.AppSettings.FilterPanelVisible = finished.FilterPanelWasVisible;
        }

        public void CancelDraft()
        {
            if (draft != null)
            {
                EndDraft();
                UpdateBar();
            }
        }

        private void SaveFilters()
        {
            var settings = main.AppSettings.FilterSettings.AsPresetSettings();
            var preset = draft?.Target == null ? null : main.Database.FilterPresets[draft.Target.Id];
            if (preset != null)
            {
                preset.Settings = settings;
                main.Database.FilterPresets.Update(preset);
            }
            else
            {
                var name = AskName("LOCAtrioCollectionNewDynamic", string.Empty);
                if (name == null)
                {
                    return;
                }

                preset = CreateDynamic(name, settings);
            }

            if (draft != null)
            {
                EndDraft();
            }
            else
            {
                main.ClearFilters();
            }

            Open(GameCollection.FromPreset(preset, main.Database));
        }

        public FilterPreset CreateDynamic(string name, FilterPresetSettings settings)
        {
            var preset = new FilterPreset { Name = name, Settings = settings, ShowInFullscreeQuickSelection = true };
            main.Database.FilterPresets.Add(preset);
            Settings.DynamicCollections.Add(preset.Id);
            Settings.Save();
            return preset;
        }
    }
}
