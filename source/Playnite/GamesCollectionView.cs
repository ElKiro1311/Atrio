using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;
using Playnite.Database;
using Playnite.SDK.Models;
using Playnite.SDK;
using Playnite.Settings;
using Playnite.Plugins;
using Playnite.SDK.Plugins;
using Playnite.Atrio;

namespace Playnite
{
    public abstract class BaseCollectionView : ObservableObject, IDisposable
    {
        public static ILogger Logger = LogManager.GetLogger();
        private ExtensionFactory extensions;
        private FilterSettings filterSettings;
        private readonly PlayniteSettings settings;

        public IGameDatabaseMain Database { get; private set; }
        public RangeObservableCollection<GamesCollectionViewEntry> Items { get; private set; }
        public bool IgnoreViewConfigChanges { get; set; } = false;
        public GameUnifier Unifier { get; }

        private ListCollectionView collectionView;
        public ListCollectionView CollectionView
        {
            get => collectionView;
            private set
            {
                collectionView = value;
                OnPropertyChanged();
            }
        }

        public BaseCollectionView(IGameDatabaseMain database, ExtensionFactory extensions, FilterSettings filterSettings, PlayniteSettings settings)
        {
            Database = database;
            this.extensions = extensions;
            this.filterSettings = filterSettings;
            this.settings = settings;
            Items = new RangeObservableCollection<GamesCollectionViewEntry>();
            filterSettings.FilterChanged += FilterSettings_FilterChanged;
            CollectionView = (ListCollectionView)CollectionViewSource.GetDefaultView(Items);
            CollectionView.Filter = Filter;

            Unifier = GameUnifier.Current ?? (GameUnifier.Current = new GameUnifier(AtrioSettings.Load()));
            Unifier.Attach(() => Database.Games, PlayniteApplication.Current?.SyncContext);
            Unifier.PluginResolver = GetLibraryPlugin;
            Unifier.GroupsChanged += Unifier_GroupsChanged;
            Database.Games.ItemCollectionChanged += Games_ItemCollectionChanged;
            Database.Games.ItemUpdated += Games_ItemUpdated;
            OpenGames.Changed += OpenGames_Changed;
            SteamControllerSupport.Changed += ControllerSupport_Changed;
            PcGamingWiki.Changed += ControllerSupport_Changed;
            SteamUpdates.WaitingChanged += SteamUpdates_WaitingChanged;
            if (PlayniteApplication.Current != null)
            {
                var games = Database.Games;
                var unifier = Unifier;
                OutsidePlay.Attach(
                    Database,
                    () => unifier.Settings.CountOutsidePlaytime,
                    () => PlayniteApplication.Current?.GamesEditor?.RunningGames.Keys.ToList() ?? new List<Guid>());
                SaveBackups.Attach(Database, () => unifier.Settings.BackupSaves);
                LightWhilePlaying.Attach(
                    PlayniteApplication.CurrentNative?.Dispatcher,
                    () => unifier.Settings.LightWhilePlaying,
                    () => OpenGames.GetOpenGames().Count > 0 || PlayniteApplication.Current?.GamesEditor?.RunningGames.Count > 0);
                OpenGames.Attach(() => games, PlayniteApplication.Current.SyncContext, Unifier.Settings.DetectOpenGames);

                var dispatcher = PlayniteApplication.CurrentNative?.Dispatcher;
                void onWindow(Action action)
                {
                    if (dispatcher == null || dispatcher.CheckAccess())
                    {
                        action();
                    }
                    else
                    {
                        dispatcher.Invoke(action);
                    }
                }

                var library = Database;
                SteamControllerSupport.Attach(() => games.ToList());
                SteamUpdates.Watch(() => OpenGames.GetOpenGames().Count > 0 || PlayniteApplication.Current?.GamesEditor?.RunningGames.Count > 0);
                Wishlist.Attach(() => unifier.Settings.WishlistAlerts);
                Ludusavi.SharedFolder = () =>
                {
                    var shared = unifier.Settings.SyncFolder;
                    return unifier.Settings.SyncSaves && !shared.IsNullOrWhiteSpace() && System.IO.Directory.Exists(shared) ? PcSync.GetSavesFolder(shared) : null;
                };
                PcSync.Attach(() => library, () => unifier.Settings.SyncFolder, onWindow);

                // One install after the other, never starting one while a game is open.
                InstallQueue.Attach(
                    id => games.Get(id),
                    game => dispatcher?.BeginInvoke(new Action(() => PlayniteApplication.Current?.GamesEditor?.InstallGame(game))),
                    () => OpenGames.GetOpenGames().Count == 0 && PlayniteApplication.Current?.GamesEditor?.RunningGames.Count == 0);
            }
        }

        private void Games_ItemCollectionChanged(object sender, ItemCollectionChangedEventArgs<Game> e)
        {
            Unifier.ScheduleRebuild();
            OpenGames.LibraryChanged();
        }

        private void Games_ItemUpdated(object sender, ItemUpdatedEventArgs<Game> e)
        {
            Unifier.ScheduleRebuild();
            if (e.UpdatedItems.Any(a => a.OldData.IsInstalled != a.NewData.IsInstalled ||
                a.OldData.InstallDirectory != a.NewData.InstallDirectory))
            {
                OpenGames.LibraryChanged();
            }
        }

        private void OpenGames_Changed(object sender, OpenGamesChangedEventArgs e)
        {
            if (Items == null)
            {
                return;
            }

            // The list shows one entry per unified game: any of its copies counts.
            var ids = new HashSet<Guid>(e.All);
            foreach (var id in e.All)
            {
                var group = Unifier.GetGroup(id);
                if (group?.Copies != null)
                {
                    ids.UnionWith(group.Copies.Select(a => a.Id));
                }
            }

            foreach (var item in Items)
            {
                if (ids.Contains(item.Id))
                {
                    item.NotifyOpenChanged();
                }
            }
        }

        private void Unifier_GroupsChanged(object sender, EventArgs e)
        {
            if (Items == null)
            {
                return;
            }

            foreach (var item in Items)
            {
                item.InvalidateCopies();
            }

            UnifiedGroupsRefreshing?.Invoke(this, EventArgs.Empty);
            CollectionView.Refresh();
            UnifiedGroupsRefreshed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Raised right before the list of visible games changes because of unification.</summary>
        public event EventHandler UnifiedGroupsRefreshing;

        /// <summary>Raised after the list of visible games changed because of unification.</summary>
        public event EventHandler UnifiedGroupsRefreshed;

        public virtual void Dispose()
        {
            filterSettings.FilterChanged -= FilterSettings_FilterChanged;
            Unifier.GroupsChanged -= Unifier_GroupsChanged;
            Database.Games.ItemCollectionChanged -= Games_ItemCollectionChanged;
            Database.Games.ItemUpdated -= Games_ItemUpdated;
            OpenGames.Changed -= OpenGames_Changed;
            SteamControllerSupport.Changed -= ControllerSupport_Changed;
            PcGamingWiki.Changed -= ControllerSupport_Changed;
            SteamUpdates.WaitingChanged -= SteamUpdates_WaitingChanged;
            Items = null;
        }

        private Guid? emulatorFilter;

        /// <summary>Shows only the games of one emulator, the way the store filter does for a store.</summary>
        public Guid? EmulatorFilter
        {
            get => emulatorFilter;
            set
            {
                if (emulatorFilter != value)
                {
                    emulatorFilter = value;
                    CollectionView.Refresh();
                }
            }
        }

        private bool controllerOnly;

        /// <summary>
        /// Shows only the games played all through with a controller, as the store of Steam or PCGamingWiki say:
        /// the tab of the mode for the couch.
        /// </summary>
        public bool ControllerOnly
        {
            get => controllerOnly;
            set
            {
                if (controllerOnly != value)
                {
                    controllerOnly = value;
                    CollectionView.Refresh();
                }
            }
        }

        /// <summary>Only the open games can show it, so only they are told.</summary>
        private void SteamUpdates_WaitingChanged(object sender, EventArgs e)
        {
            PlayniteApplication.CurrentNative?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                foreach (var item in Items?.Where(a => a.IsOpen).ToList() ?? new List<GamesCollectionViewEntry>())
                {
                    item.NotifyUpdatePendingChanged();
                }
            }));
        }

        private void ControllerSupport_Changed(object sender, EventArgs e)
        {
            if (controllerOnly)
            {
                PlayniteApplication.CurrentNative?.Dispatcher?.BeginInvoke(new Action(() => CollectionView?.Refresh()));
            }
        }

        private GameCollection collection;

        /// <summary>Shows only the games of a collection, with the other filters still applying on top.</summary>
        public GameCollection Collection
        {
            get => collection;
            set
            {
                if (collection != value)
                {
                    collection = value;
                    CollectionView.Refresh();
                }
            }
        }

        private bool MatchesFilters(Game game)
        {
            return Database.GetGameMatchesFilter(game, filterSettings, settings.FuzzyMatchingInNameFilter, collection?.ShowsHiddenGames == true);
        }

        private bool Filter(object item)
        {
            if (!(item is GamesCollectionViewEntry entry))
            {
                return false;
            }

            if (emulatorFilter != null && EmulatorStores.GetEmulatorId(entry.Game) != emulatorFilter)
            {
                return false;
            }

            if (Unifier.Settings.UnifyDuplicates)
            {
                Unifier.EnsureBuilt();
                var group = Unifier.GetGroup(entry.Id);
                if (group != null)
                {
                    if (group.Primary.Id != entry.Id)
                    {
                        return false;
                    }

                    if (collection?.Contains(group.Copies) == false)
                    {
                        return false;
                    }

                    if (controllerOnly && !SteamControllerSupport.IsFullyPlayable(group.Copies, entry.Game.Name))
                    {
                        return false;
                    }

                    // The game has to show up in the filters of every store that sells it.
                    foreach (var copy in group.Copies)
                    {
                        if (MatchesFilters(copy))
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            if (controllerOnly && !SteamControllerSupport.IsFullyPlayable(new[] { entry.Game }, entry.Game.Name))
            {
                return false;
            }

            return collection?.Contains(new[] { entry.Game }) != false && MatchesFilters(entry.Game);
        }

        private void FilterSettings_FilterChanged(object sender, FilterChangedEventArgs e)
        {
            if (IgnoreViewConfigChanges)
            {
                return;
            }

            Logger.Debug("Refreshing collection view filter.");
            CollectionView.Refresh();
        }

        public LibraryPlugin GetLibraryPlugin(Game game)
        {
            if (game.PluginId != Guid.Empty &&
                extensions.Plugins.TryGetValue(game.PluginId, out var plugin) &&
                plugin.Plugin is LibraryPlugin libPlugin)
            {
                return libPlugin;
            }

            return null;
        }

        public abstract void RefreshView();

        public void NotifyItemPropertyChanges(params string[] changedProperties)
        {
            if (!Items.HasItems())
            {
                return;
            }

            Logger.Trace("NotifyItemPropertyChanges: ");
            changedProperties.ForEach(prop => Logger.Trace(prop));
            foreach (var item in Items)
            {
                changedProperties.ForEach(prop => item.OnPropertyChanged(prop));
            }
        }
    }
}
