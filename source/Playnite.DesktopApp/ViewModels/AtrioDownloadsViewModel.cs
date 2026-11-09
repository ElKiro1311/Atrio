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
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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

        private bool measured;

        /// <summary>Atrio already tried to measure it: without a size now, it can't be known.</summary>
        public bool Measured
        {
            get => measured;
            set
            {
                SetValue(ref measured, value);
                OnPropertyChanged(nameof(SizeText));
            }
        }

        public string SizeText => size > 0 ?
            DirectUninstall.FormatSize(size) :
            ResourceProvider.GetString(measured ? "LOCAtrioStorageUnknownSize" : "LOCAtrioStorageMeasuring");

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
    public partial class DownloadsViewModel : ObservableObject
    {
        /// <summary>The question lists this many games; the rest are counted, and they're all ticked on the page.</summary>
        public const int QuestionListed = 15;

        /// <summary>A game that waits longer for its store stops the rest of the list.</summary>
        public static readonly TimeSpan StorePatience = TimeSpan.FromMinutes(10);

        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly DesktopAppViewModel main;
        private readonly DispatcherTimer refreshTimer;
        private readonly HashSet<Guid> measuredGames = new HashSet<Guid>();
        private bool isOpen;
        private bool measuring;
        private bool wholePage;
        private CancellationTokenSource removal;

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

        private bool isRemoving;

        /// <summary>A list of games is being uninstalled: the page says which one and can stop it.</summary>
        public bool IsRemoving
        {
            get => isRemoving;
            set
            {
                SetValue(ref isRemoving, value);
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private string removalText;
        public string RemovalText
        {
            get => removalText;
            set => SetValue(ref removalText, value);
        }

        public RelayCommand<object> RefreshCommand { get; }
        public RelayCommand<object> UninstallSelectedCommand { get; }
        public RelayCommand<object> ClearSelectionCommand { get; }
        public RelayCommand<object> StopRemovalCommand { get; }
        public RelayCommand<StorageRow> UninstallCommand { get; }
        public RelayCommand<StorageRow> OpenFolderCommand { get; }
        public RelayCommand<Game> OpenGameCommand { get; }

        public DownloadsViewModel(DesktopAppViewModel main)
        {
            this.main = main;
            RefreshCommand = new RelayCommand<object>(_ => Refresh());
            UninstallSelectedCommand = new RelayCommand<object>(_ => UninstallSelected(), _ => HasSelection && !IsRemoving);
            ClearSelectionCommand = new RelayCommand<object>(_ => SetSelection(false));
            StopRemovalCommand = new RelayCommand<object>(_ => removal?.Cancel(), _ => IsRemoving);
            UninstallCommand = new RelayCommand<StorageRow>(a => Uninstall(new List<StorageRow> { a }), a => a != null && !IsRemoving);
            OpenFolderCommand = new RelayCommand<StorageRow>(a => OpenFolder(a));
            OpenGameCommand = new RelayCommand<Game>(a =>
            {
                if (a != null)
                {
                    main.OpenGame(a.Id);
                }
            });

            // Installs report every few seconds: the page waits for a pause, and only what changed is made again.
            refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            refreshTimer.Tick += (_, __) =>
            {
                refreshTimer.Stop();
                var everything = wholePage;
                wholePage = false;
                if (isOpen && !main.IsDisposing)
                {
                    if (everything)
                    {
                        Refresh();
                    }
                    else
                    {
                        RefreshActive();
                    }
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
            InstallQueue.Changed -= InstallQueue_Changed;
            InstallQueue.Changed += InstallQueue_Changed;
            Refresh();
            FindLeftovers();
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
            InstallQueue.Changed -= InstallQueue_Changed;
            leftoverMeasure?.Cancel();
        }

        private void RefreshSoon(bool everything)
        {
            refreshTimer.Dispatcher.BeginInvoke(new Action(() =>
            {
                wholePage |= everything;
                if (!refreshTimer.IsEnabled)
                {
                    refreshTimer.Start();
                }
            }));
        }

        private void Games_ItemUpdated(object sender, ItemUpdatedEventArgs<Game> e)
        {
            // Installed or not changes the disks; the rest only the list of what's in progress or one size. Either can
            // make a folder that stayed behind belong to a game again, or the other way round.
            if (e.UpdatedItems.Any(a => a.OldData.IsInstalled != a.NewData.IsInstalled || a.OldData.IsUninstalling != a.NewData.IsUninstalling))
            {
                leftoversDirty = true;
                RefreshSoon(true);
            }
            else if (e.UpdatedItems.Any(a => a.OldData.IsInstalling != a.NewData.IsInstalling))
            {
                leftoversDirty = true;
                RefreshSoon(true);
            }

            var sizes = e.UpdatedItems.Where(a => a.OldData.InstallSize != a.NewData.InstallSize).Select(a => a.NewData).ToList();
            if (sizes.Count > 0)
            {
                refreshTimer.Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (var game in sizes)
                    {
                        var row = Disks.SelectMany(a => a.Rows ?? new List<StorageRow>()).FirstOrDefault(a => a.Game.Id == game.Id);
                        if (row != null)
                        {
                            row.Size = (long)Math.Min(game.InstallSize ?? 0, long.MaxValue);
                        }
                    }

                    UpdateSelection();
                }));
            }
        }

        private void DirectInstallProgress_Changed(object sender, Guid e)
        {
            RefreshSoon(false);
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

        private Func<Guid, GamesCollectionViewEntry> GetEntryFinder()
        {
            var entries = GetEntries();
            return id =>
            {
                if (entries.TryGetValue(id, out var entry))
                {
                    return entry;
                }

                var primary = GameUnifier.Current?.GetGroup(id)?.Primary?.Id;
                return primary != null && entries.TryGetValue(primary.Value, out entry) ? entry : null;
            };
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

        /// <summary>Only what is installing or uninstalling now: a progress report doesn't touch the disks.</summary>
        private void RefreshActive()
        {
            if (main.Database?.Games == null)
            {
                return;
            }

            var findEntry = GetEntryFinder();
            Active = main.Database.Games.
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
        }

        public void Refresh()
        {
            if (main.Database?.Games == null)
            {
                return;
            }

            RefreshActive();
            RefreshQueue();
            RefreshUpdates();
            var findEntry = GetEntryFinder();
            var games = main.Database.Games.ToList();
            var selected = new HashSet<Guid>(Disks.SelectMany(a => a.Rows ?? new List<StorageRow>()).Where(a => a.IsSelected).Select(a => a.Game.Id));
            var forgottenSince = DateTime.Now - ForgottenAfter;
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
            var now = DateTime.Now;

            // The uninstallers that Windows knows, read once for the whole page and only if a game needs them.
            List<WindowsUninstallEntry> registered = null;
            List<WindowsUninstallEntry> readRegistered() => registered ?? (registered = WindowsUninstallEntries.Read());

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
                    // The whole disk counts in its bar; with the filter, the list only has what isn't played anymore.
                    Rows = disk.Games.Where(a => !OnlyForgotten || IsForgotten(byId[a.GameId], forgottenSince)).Select(a =>
                    {
                        var game = byId[a.GameId];
                        var row = new StorageRow
                        {
                            Game = game,
                            Entry = findEntry(game.Id),
                            StoreName = GetStoreName(game),
                            LastPlayedText = GetLastPlayedText(game.LastActivity, now),
                            Folder = a.Folder,
                            IsDirect = DirectUninstall.IsDirect(game, readRegistered),
                            Size = a.Size,
                            Measured = measuredGames.Contains(game.Id) || game.InstallSize != null
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
            if (leftoversDirty && isOpen)
            {
                leftoversDirty = false;
                FindLeftovers();
            }
        }

        /// <summary>Games without a size yet are measured once, in the background, one after the other.</summary>
        private async void MeasureMissing()
        {
            if (measuring)
            {
                return;
            }

            var missing = Disks.SelectMany(a => a.Rows).Where(a => a.Size <= 0 && !a.Measured && !measuredGames.Contains(a.Game.Id)).ToList();
            if (missing.Count == 0)
            {
                return;
            }

            measuring = true;
            var measuredAny = false;
            try
            {
                foreach (var row in missing)
                {
                    if (!isOpen || main.IsDisposing)
                    {
                        break;
                    }

                    measuredGames.Add(row.Game.Id);
                    await Task.Run(() =>
                    {
                        try
                        {
                            main.GamesEditor.UpdateGameSize(row.Game, true, true, false);
                        }
                        catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                        {
                            logger.Warn(e, $"Failed to measure {row.Game.Name}.");
                        }
                    });

                    // The size goes to the row before it stops saying "Measuring…": the change of the library comes a bit later.
                    var measured = main.Database.Games.Get(row.Game.Id)?.InstallSize ?? 0;
                    if (measured > 0)
                    {
                        row.Size = (long)Math.Min(measured, long.MaxValue);
                    }

                    row.Measured = true;
                    measuredAny = true;
                }
            }
            finally
            {
                measuring = false;
            }

            // The new sizes move the bar of the disk, what's left for the rest and the order of the list: the page is
            // made once more, without measuring again what's already measured.
            if (measuredAny && isOpen && !main.IsDisposing)
            {
                RefreshSoon(true);
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

        /// <summary>
        /// The question lists the games with their size and folder; with many, the first ones and how many more,
        /// all of them ticked on the page. Nothing is deleted that the person didn't choose.
        /// </summary>
        public static string GetQuestion(IEnumerable<StorageRow> rows)
        {
            var list = rows.ToList();
            var text = new StringBuilder();
            text.AppendLine(string.Format(
                ResourceProvider.GetString(list.Count == 1 ? "LOCAtrioStorageAskOne" : "LOCAtrioStorageAsk"),
                list.Count));
            text.AppendLine();
            foreach (var row in list.Take(QuestionListed))
            {
                text.AppendLine($"• {row.Game.Name} — {row.SizeText} — {row.Folder}");
            }

            if (list.Count > QuestionListed)
            {
                text.AppendLine(string.Format(ResourceProvider.GetString("LOCAtrioStorageAskMore"), list.Count - QuestionListed));
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

        private static bool IsOpen(Game game)
        {
            return game.IsRunning || game.IsLaunching || OpenGames.IsOpen(game.Id);
        }

        /// <summary>
        /// One question for all, with Cancel as the default answer, then one game after the other, each when the one
        /// before has finished. A game that waits too long for its store stops the list, and so does Stop.
        /// </summary>
        private async void Uninstall(List<StorageRow> rows)
        {
            rows = rows?.Where(a => a?.Game != null && a.Game.IsInstalled && !a.Game.IsUninstalling).ToList();
            if (rows == null || rows.Count == 0 || IsRemoving)
            {
                return;
            }

            var open = rows.Where(a => IsOpen(a.Game)).ToList();
            if (open.Count > 0)
            {
                main.Dialogs.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCAtrioStorageOpenGame"), string.Join(", ", open.Select(a => a.Game.Name))),
                    ResourceProvider.GetString("LOCAtrioUninstallTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var yes = new MessageBoxOption(ResourceProvider.GetString("LOCAtrioStorageUninstall"));
            var no = new MessageBoxOption(ResourceProvider.GetString("LOCCancelLabel"), true, true);
            if (main.Dialogs.ShowMessage(GetQuestion(rows), ResourceProvider.GetString("LOCAtrioUninstallTitle"), MessageBoxImage.Question, new List<MessageBoxOption> { yes, no }) != yes)
            {
                return;
            }

            removal = new CancellationTokenSource();
            var token = removal.Token;
            IsRemoving = true;
            var skipped = new List<string>();
            string stalled = null;
            try
            {
                for (var i = 0; i < rows.Count; i++)
                {
                    if (main.IsDisposing || token.IsCancellationRequested)
                    {
                        break;
                    }

                    var game = main.Database.Games.Get(rows[i].Game.Id);
                    if (game == null || !game.IsInstalled || game.IsUninstalling)
                    {
                        continue;
                    }

                    // Someone could have opened it while the ones before were going away.
                    if (IsOpen(game))
                    {
                        skipped.Add(game.Name);
                        continue;
                    }

                    RemovalText = string.Format(ResourceProvider.GetString("LOCAtrioStorageRemoving"), game.Name, i + 1, rows.Count);
                    logger.Info($"Storage page: uninstalling {game.Name} ({rows[i].SizeText}).");
                    main.GamesEditor.UnInstallGame(game, true);

                    // The next one waits until this one is done, or has handed over to its store.
                    var waited = DateTime.Now;
                    await Task.Delay(1500);
                    while (!main.IsDisposing && !token.IsCancellationRequested)
                    {
                        var current = main.Database.Games.Get(game.Id);
                        if (current == null || !current.IsUninstalling)
                        {
                            break;
                        }

                        if (DateTime.Now - waited > StorePatience)
                        {
                            stalled = game.Name;
                            break;
                        }

                        if (DateTime.Now - waited > TimeSpan.FromSeconds(20))
                        {
                            RemovalText = string.Format(ResourceProvider.GetString("LOCAtrioStorageWaitingStore"), game.Name, GetStoreName(game));
                        }

                        await Task.Delay(1000);
                    }

                    if (stalled != null)
                    {
                        break;
                    }
                }
            }
            finally
            {
                IsRemoving = false;
                RemovalText = null;
                removal = null;
                Refresh();
            }

            if (stalled != null)
            {
                main.Dialogs.ShowMessage(string.Format(ResourceProvider.GetString("LOCAtrioStorageStalled"), stalled), ResourceProvider.GetString("LOCAtrioUninstallTitle"));
            }
            else if (skipped.Count > 0)
            {
                main.Dialogs.ShowMessage(string.Format(ResourceProvider.GetString("LOCAtrioStorageSkipped"), string.Join(", ", skipped)), ResourceProvider.GetString("LOCAtrioUninstallTitle"));
            }
        }
    }
}
