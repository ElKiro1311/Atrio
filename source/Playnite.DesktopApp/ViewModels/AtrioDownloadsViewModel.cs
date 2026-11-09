using Playnite.Atrio;
using Playnite.Commands;
using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace Playnite.DesktopApp.ViewModels
{
    /// <summary>A game that is being installed or uninstalled right now.</summary>
    public class ActiveDownload
    {
        public Game Game { get; set; }
        public GamesCollectionViewEntry Entry { get; set; }
        public string StoreName { get; set; }
        public string StateText { get; set; }
        public bool HasPercent { get; set; }
        public double Percent { get; set; }
    }

    /// <summary>One installed game in the list of a disk.</summary>
    public class StorageRow : ObservableObject
    {
        public Game Game { get; set; }
        public GamesCollectionViewEntry Entry { get; set; }
        public string StoreName { get; set; }
        public string LastPlayedText { get; set; }
        public string Folder { get; set; }

        /// <summary>Atrio removes it itself; without it, the store opens its own window to finish.</summary>
        public bool IsDirect { get; set; }

        private long size;
        public long Size
        {
            get => size;
            set
            {
                SetValue(ref size, value);
                OnPropertyChanged(nameof(SizeText));
            }
        }

        public string SizeText => size > 0 ? DirectUninstall.FormatSize(size) : ResourceProvider.GetString("LOCAtrioStorageMeasuring");

        private bool isSelected;
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                SetValue(ref isSelected, value);
                SelectionChanged?.Invoke();
            }
        }

        public Action SelectionChanged { get; set; }
    }

    /// <summary>One disk: how full it is and its games, the biggest first.</summary>
    public class StorageDiskCard
    {
        public string Title { get; set; }
        public string FreeText { get; set; }
        public GridLength GamesLength { get; set; }
        public GridLength OtherLength { get; set; }
        public GridLength FreeLength { get; set; }
        public string GamesLegend { get; set; }
        public string OtherLegend { get; set; }
        public string FreeLegend { get; set; }
        public string ToolTip { get; set; }
        public List<StorageRow> Rows { get; set; }
        public bool HasRows => Rows?.Count > 0;
    }

    /// <summary>
    /// The page of the downloads: what is installing now, from any store, and the room of each disk with the
    /// installed games by size, to free space without opening any store.
    /// </summary>
    public class DownloadsViewModel : ObservableObject
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly DesktopAppViewModel main;
        private readonly DispatcherTimer refreshTimer;
        private bool isOpen;
        private bool measuring;
        private bool removing;

        private List<ActiveDownload> active = new List<ActiveDownload>();
        public List<ActiveDownload> Active
        {
            get => active;
            set
            {
                SetValue(ref active, value);
                OnPropertyChanged(nameof(HasActive));
            }
        }

        public bool HasActive => active?.Count > 0;

        private List<StorageDiskCard> disks = new List<StorageDiskCard>();
        public List<StorageDiskCard> Disks
        {
            get => disks;
            set => SetValue(ref disks, value);
        }

        private string selectionText;
        public string SelectionText
        {
            get => selectionText;
            set => SetValue(ref selectionText, value);
        }

        private bool hasSelection;
        public bool HasSelection
        {
            get => hasSelection;
            set => SetValue(ref hasSelection, value);
        }

        public RelayCommand<object> RefreshCommand { get; }
        public RelayCommand<object> UninstallSelectedCommand { get; }
        public RelayCommand<object> ClearSelectionCommand { get; }
        public RelayCommand<StorageRow> UninstallCommand { get; }
        public RelayCommand<StorageRow> OpenFolderCommand { get; }
        public RelayCommand<Game> OpenGameCommand { get; }

        public DownloadsViewModel(DesktopAppViewModel main)
        {
            this.main = main;
            RefreshCommand = new RelayCommand<object>(_ => Refresh());
            UninstallSelectedCommand = new RelayCommand<object>(_ => UninstallSelected(), _ => HasSelection && !removing);
            ClearSelectionCommand = new RelayCommand<object>(_ => SetSelection(false));
            UninstallCommand = new RelayCommand<StorageRow>(a => Uninstall(new List<StorageRow> { a }), a => a != null && !removing);
            OpenFolderCommand = new RelayCommand<StorageRow>(a => OpenFolder(a));
            OpenGameCommand = new RelayCommand<Game>(a =>
            {
                if (a != null)
                {
                    main.OpenGame(a.Id);
                }
            });

            // Installs report every few seconds: the page waits for a pause before it rebuilds.
            refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            refreshTimer.Tick += (_, __) =>
            {
                refreshTimer.Stop();
                if (isOpen && !main.IsDisposing)
                {
                    Refresh();
                }
            };
        }

        public void OnShown()
        {
            isOpen = true;
            if (main.Database?.Games != null)
            {
                main.Database.Games.ItemUpdated -= Games_ItemUpdated;
                main.Database.Games.ItemUpdated += Games_ItemUpdated;
            }

            DirectInstallProgress.Changed -= DirectInstallProgress_Changed;
            DirectInstallProgress.Changed += DirectInstallProgress_Changed;
            Refresh();
        }

        public void OnHidden()
        {
            isOpen = false;
            refreshTimer.Stop();
            if (main.Database?.Games != null)
            {
                main.Database.Games.ItemUpdated -= Games_ItemUpdated;
            }

            DirectInstallProgress.Changed -= DirectInstallProgress_Changed;
        }

        private void RefreshSoon()
        {
            refreshTimer.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!refreshTimer.IsEnabled)
                {
                    refreshTimer.Start();
                }
            }));
        }

        private void Games_ItemUpdated(object sender, ItemUpdatedEventArgs<Game> e)
        {
            if (e.UpdatedItems.Any(a =>
                a.OldData.IsInstalling != a.NewData.IsInstalling ||
                a.OldData.IsUninstalling != a.NewData.IsUninstalling ||
                a.OldData.IsInstalled != a.NewData.IsInstalled ||
                a.OldData.InstallSize != a.NewData.InstallSize))
            {
                RefreshSoon();
            }
        }

        private void DirectInstallProgress_Changed(object sender, Guid e)
        {
            RefreshSoon();
        }

        private string GetStoreName(Game game)
        {
            return game.Source?.Name ?? main.Extensions?.GetLibraryPlugin(game.PluginId)?.Name ?? ResourceProvider.GetString("LOCAtrioStorageNoStore");
        }

        private Dictionary<Guid, GamesCollectionViewEntry> GetEntries()
        {
            var result = new Dictionary<Guid, GamesCollectionViewEntry>();
            foreach (var entry in main.GamesView?.Items?.ToList() ?? new List<GamesCollectionViewEntry>())
            {
                result[entry.Id] = entry;
            }

            return result;
        }

        public static string GetLastPlayedText(DateTime? lastActivity, DateTime now)
        {
            if (lastActivity == null)
            {
                return ResourceProvider.GetString("LOCAtrioStorageNeverPlayed");
            }

            var days = (now.Date - lastActivity.Value.Date).TotalDays;
            if (days <= 0)
            {
                return ResourceProvider.GetString("LOCAtrioStoragePlayedToday");
            }

            if (days < 2)
            {
                return ResourceProvider.GetString("LOCAtrioStoragePlayedYesterday");
            }

            if (days < 60)
            {
                return string.Format(ResourceProvider.GetString("LOCAtrioStoragePlayedDays"), (int)days);
            }

            var months = (int)Math.Round(days / 30.4);
            if (months < 24)
            {
                return string.Format(ResourceProvider.GetString("LOCAtrioStoragePlayedMonths"), months);
            }

            return string.Format(ResourceProvider.GetString("LOCAtrioStoragePlayedYears"), (int)(days / 365.25));
        }

        public void Refresh()
        {
            if (main.Database?.Games == null)
            {
                return;
            }

            var entries = GetEntries();
            GamesCollectionViewEntry findEntry(Guid id)
            {
                if (entries.TryGetValue(id, out var entry))
                {
                    return entry;
                }

                var primary = GameUnifier.Current?.GetGroup(id)?.Primary?.Id;
                return primary != null && entries.TryGetValue(primary.Value, out entry) ? entry : null;
            }

            var games = main.Database.Games.ToList();
            Active = games.
                Where(a => a.IsInstalling || a.IsUninstalling).
                OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).
                Select(a =>
                {
                    var percent = DirectInstallProgress.Get(a.Id);
                    var notice = DirectInstallProgress.GetNotice(a.Id);
                    string state;
                    if (a.IsUninstalling)
                    {
                        state = ResourceProvider.GetString("LOCAtrioDownloadsUninstalling");
                    }
                    else if (notice != null)
                    {
                        state = notice;
                    }
                    else
                    {
                        state = percent > 0 ?
                            string.Format(ResourceProvider.GetString("LOCAtrioDownloadsInstallingPercent"), percent) :
                            ResourceProvider.GetString("LOCAtrioDownloadsInstalling");
                    }

                    return new ActiveDownload
                    {
                        Game = a,
                        Entry = findEntry(a.Id),
                        StoreName = GetStoreName(a),
                        StateText = state,
                        HasPercent = !a.IsUninstalling && percent != null,
                        Percent = Math.Max(0, Math.Min(100, percent ?? 0))
                    };
                }).
                ToList();

            var selected = new HashSet<Guid>(Disks.SelectMany(a => a.Rows ?? new List<StorageRow>()).Where(a => a.IsSelected).Select(a => a.Game.Id));
            var installed = games.Where(a => a.IsInstalled && !a.IsUninstalling && !a.InstallDirectory.IsNullOrWhiteSpace() && !a.Roms.HasItems()).ToList();
            var folders = new Dictionary<Guid, string>();
            foreach (var game in installed)
            {
                try
                {
                    folders[game.Id] = game.ExpandVariables(game.InstallDirectory, true);
                }
                catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Debug(e, $"Failed to read the folder of {game.Name}.");
                }
            }

            var storageGames = installed.
                Where(a => folders.ContainsKey(a.Id)).
                Select(a => new StorageGame { GameId = a.Id, Name = a.Name, Folder = folders[a.Id], Size = (long)Math.Min(a.InstallSize ?? 0, long.MaxValue) }).
                ToList();
            var byId = installed.ToDictionary(a => a.Id);
            var culture = Localization.ApplicationLanguageCultureInfo;
            var now = DateTime.Now;
            var cards = new List<StorageDiskCard>();
            foreach (var disk in Storage.Build(storageGames, Storage.ReadDisks()))
            {
                var used = Math.Max(1, disk.Total);
                var label = disk.Label.IsNullOrWhiteSpace() ? disk.Root.TrimEnd('\\') : $"{disk.Root.TrimEnd('\\')}  {disk.Label}";
                cards.Add(new StorageDiskCard
                {
                    Title = label,
                    FreeText = string.Format(
                        ResourceProvider.GetString("LOCAtrioStorageFreeOf"),
                        DirectUninstall.FormatSize(disk.Free),
                        DirectUninstall.FormatSize(disk.Total)),
                    GamesLength = new GridLength(disk.GamesSize / (double)used, GridUnitType.Star),
                    OtherLength = new GridLength(disk.OtherSize / (double)used, GridUnitType.Star),
                    FreeLength = new GridLength(disk.Free / (double)used, GridUnitType.Star),
                    GamesLegend = string.Format(ResourceProvider.GetString("LOCAtrioStorageGamesLegend"), DirectUninstall.FormatSize(disk.GamesSize)),
                    OtherLegend = string.Format(ResourceProvider.GetString("LOCAtrioStorageOtherLegend"), DirectUninstall.FormatSize(disk.OtherSize)),
                    FreeLegend = string.Format(ResourceProvider.GetString("LOCAtrioStorageFreeLegend"), DirectUninstall.FormatSize(disk.Free)),
                    ToolTip = string.Format(
                        ResourceProvider.GetString("LOCAtrioStorageDiskTip"),
                        label,
                        DirectUninstall.FormatSize(disk.GamesSize),
                        DirectUninstall.FormatSize(disk.OtherSize),
                        DirectUninstall.FormatSize(disk.Free)),
                    Rows = disk.Games.Select(a =>
                    {
                        var game = byId[a.GameId];
                        var row = new StorageRow
                        {
                            Game = game,
                            Entry = findEntry(game.Id),
                            StoreName = GetStoreName(game),
                            LastPlayedText = GetLastPlayedText(game.LastActivity, now),
                            Folder = a.Folder,
                            IsDirect = DirectUninstall.IsDirect(game),
                            Size = a.Size
                        };
                        row.IsSelected = selected.Contains(game.Id);
                        row.SelectionChanged = UpdateSelection;
                        return row;
                    }).ToList()
                });
            }

            Disks = cards;
            UpdateSelection();
            MeasureMissing();
        }

        /// <summary>Games without a size yet are measured once, in the background, one after the other.</summary>
        private async void MeasureMissing()
        {
            if (measuring)
            {
                return;
            }

            var missing = Disks.SelectMany(a => a.Rows).Where(a => a.Size <= 0).Select(a => a.Game).ToList();
            if (missing.Count == 0)
            {
                return;
            }

            measuring = true;
            try
            {
                foreach (var game in missing)
                {
                    if (!isOpen || main.IsDisposing)
                    {
                        break;
                    }

                    await Task.Run(() =>
                    {
                        try
                        {
                            main.GamesEditor.UpdateGameSize(game, true, true, false);
                        }
                        catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                        {
                            logger.Warn(e, $"Failed to measure {game.Name}.");
                        }
                    });
                }
            }
            finally
            {
                measuring = false;
            }
        }

        private void UpdateSelection()
        {
            var rows = Disks.SelectMany(a => a.Rows ?? new List<StorageRow>()).Where(a => a.IsSelected).ToList();
            HasSelection = rows.Count > 0;
            SelectionText = rows.Count == 0 ? null : string.Format(
                ResourceProvider.GetString(rows.Count == 1 ? "LOCAtrioStorageSelectedOne" : "LOCAtrioStorageSelected"),
                rows.Count,
                DirectUninstall.FormatSize(rows.Sum(a => Math.Max(0, a.Size))));
        }

        private void SetSelection(bool value)
        {
            foreach (var row in Disks.SelectMany(a => a.Rows ?? new List<StorageRow>()))
            {
                row.IsSelected = value;
            }
        }

        private void OpenFolder(StorageRow row)
        {
            if (row?.Folder.IsNullOrEmpty() == false)
            {
                main.GamesEditor.OpenGameLocation(row.Game);
            }
        }

        private void UninstallSelected()
        {
            Uninstall(Disks.SelectMany(a => a.Rows ?? new List<StorageRow>()).Where(a => a.IsSelected).ToList());
        }

        /// <summary>The question lists every game with its size and folder: nothing is deleted that the person didn't read.</summary>
        public static string GetQuestion(IEnumerable<StorageRow> rows)
        {
            var list = rows.ToList();
            var text = new StringBuilder();
            text.AppendLine(string.Format(
                ResourceProvider.GetString(list.Count == 1 ? "LOCAtrioStorageAskOne" : "LOCAtrioStorageAsk"),
                list.Count));
            text.AppendLine();
            foreach (var row in list)
            {
                text.AppendLine($"• {row.Game.Name} — {row.SizeText} — {row.Folder}");
            }

            text.AppendLine();
            text.Append(string.Format(ResourceProvider.GetString("LOCAtrioStorageAskTotal"), DirectUninstall.FormatSize(list.Sum(a => Math.Max(0, a.Size)))));
            var throughStore = list.Where(a => !a.IsDirect).Select(a => a.Game.Name).ToList();
            if (throughStore.Count > 0)
            {
                text.AppendLine();
                text.Append(string.Format(ResourceProvider.GetString("LOCAtrioStorageAskStore"), string.Join(", ", throughStore)));
            }

            return text.ToString();
        }

        /// <summary>One question for all, then one game after the other, each when the one before has finished.</summary>
        private async void Uninstall(List<StorageRow> rows)
        {
            rows = rows?.Where(a => a?.Game != null && a.Game.IsInstalled && !a.Game.IsUninstalling).ToList();
            if (rows == null || rows.Count == 0 || removing)
            {
                return;
            }

            if (rows.Any(a => a.Game.IsRunning || a.Game.IsLaunching || OpenGames.IsOpen(a.Game.Id)))
            {
                main.Dialogs.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCAtrioStorageOpenGame"), string.Join(", ", rows.Where(a => a.Game.IsRunning || a.Game.IsLaunching || OpenGames.IsOpen(a.Game.Id)).Select(a => a.Game.Name))),
                    ResourceProvider.GetString("LOCAtrioUninstallTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (main.Dialogs.ShowMessage(GetQuestion(rows), ResourceProvider.GetString("LOCAtrioUninstallTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            removing = true;
            try
            {
                foreach (var row in rows)
                {
                    if (main.IsDisposing)
                    {
                        break;
                    }

                    var game = main.Database.Games.Get(row.Game.Id);
                    if (game == null || !game.IsInstalled || game.IsUninstalling)
                    {
                        continue;
                    }

                    logger.Info($"Storage page: uninstalling {game.Name} ({row.SizeText}).");
                    main.GamesEditor.UnInstallGame(game, true);

                    // The next one waits until this one is done, or has handed over to its store.
                    var waited = DateTime.Now;
                    await Task.Delay(1500);
                    while (!main.IsDisposing && DateTime.Now - waited < TimeSpan.FromMinutes(30))
                    {
                        var current = main.Database.Games.Get(game.Id);
                        if (current == null || !current.IsUninstalling)
                        {
                            break;
                        }

                        await Task.Delay(1000);
                    }
                }
            }
            finally
            {
                removing = false;
                Refresh();
            }
        }
    }
}
