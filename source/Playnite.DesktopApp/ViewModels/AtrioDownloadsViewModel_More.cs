using Playnite.Atrio;
using Playnite.Commands;
using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Playnite.DesktopApp.ViewModels
{
    /// <summary>A game waiting in the install queue.</summary>
    public class QueueRow
    {
        public Game Game { get; set; }
        public GamesCollectionViewEntry Entry { get; set; }
        public string Name { get; set; }
        public string StoreName { get; set; }
        public string StateText { get; set; }
        public bool IsCurrent { get; set; }
    }

    /// <summary>A game of Steam with an update waiting.</summary>
    public class UpdateRow
    {
        public string AppId { get; set; }
        public string Name { get; set; }
        public Game Game { get; set; }
        public GamesCollectionViewEntry Entry { get; set; }
        public string StateText { get; set; }
    }

    /// <summary>A folder that stayed behind after a game went away.</summary>
    public class LeftoverRow : ObservableObject
    {
        public LeftoverFolder Folder { get; set; }
        public string Name { get; set; }
        public string KindText { get; set; }
        public string Path => Folder.Path;

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
        public bool Measured
        {
            get => measured;
            set
            {
                SetValue(ref measured, value);
                OnPropertyChanged(nameof(SizeText));
            }
        }

        public string SizeText => measured ? DirectUninstall.FormatSize(size) : ResourceProvider.GetString("LOCAtrioStorageMeasuring");

        private string lastUsedText;
        public string LastUsedText
        {
            get => lastUsedText;
            set => SetValue(ref lastUsedText, value);
        }

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

    public partial class DownloadsViewModel
    {
        /// <summary>The filter of the games that aren't played anymore.</summary>
        public static readonly TimeSpan ForgottenAfter = TimeSpan.FromDays(182);

        public static bool IsForgotten(Game game, DateTime since)
        {
            return (game.LastActivity ?? game.Added ?? DateTime.MinValue) < since;
        }

        private bool onlyForgotten;

        /// <summary>The lists of the disks only show what isn't opened since half a year.</summary>
        public bool OnlyForgotten
        {
            get => onlyForgotten;
            set
            {
                SetValue(ref onlyForgotten, value);
                Refresh();
            }
        }

        #region The install queue

        private List<QueueRow> queue = new List<QueueRow>();
        public List<QueueRow> Queue
        {
            get => queue;
            set
            {
                SetValue(ref queue, value);
                OnPropertyChanged(nameof(HasQueue));
            }
        }

        public bool HasQueue => queue?.Count > 0;

        private string queueText;
        public string QueueText
        {
            get => queueText;
            set => SetValue(ref queueText, value);
        }

        /// <summary>"03:00" and so on, for the hour of the night the queue can wait for.</summary>
        public List<string> Hours { get; } = Enumerable.Range(0, 24).Select(a => $"{a:00}:00").ToList();

        public bool StartLater
        {
            get => InstallQueue.StartAt != null;
            set
            {
                InstallQueue.SetStart(value ? InstallQueue.StartHour : (int?)null);
                OnPropertyChanged();
            }
        }

        public int StartHour
        {
            get => InstallQueue.StartHour;
            set
            {
                if (InstallQueue.StartAt != null)
                {
                    InstallQueue.SetStart(value);
                }
                else
                {
                    // Kept for when the box is ticked.
                    InstallQueue.SetStart(value);
                    InstallQueue.SetStart(null);
                }

                OnPropertyChanged();
            }
        }

        public RelayCommand<QueueRow> QueueUpCommand => new RelayCommand<QueueRow>(a => InstallQueue.Move(a?.Game?.Id ?? Guid.Empty, -1));
        public RelayCommand<QueueRow> QueueDownCommand => new RelayCommand<QueueRow>(a => InstallQueue.Move(a?.Game?.Id ?? Guid.Empty, 1));
        public RelayCommand<QueueRow> QueueRemoveCommand => new RelayCommand<QueueRow>(a =>
        {
            if (a?.Game != null)
            {
                InstallQueue.Remove(a.Game.Id);
            }
        });

        public RelayCommand<object> QueueClearCommand => new RelayCommand<object>(_ => InstallQueue.Clear());

        private void InstallQueue_Changed(object sender, EventArgs e)
        {
            Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
            {
                if (isOpen)
                {
                    RefreshQueue();
                }
            }));
        }

        private void RefreshQueue()
        {
            var findEntry = GetEntryFinder();
            var current = InstallQueue.Current;
            var startAt = InstallQueue.StartAt;
            var gameOpen = OpenGames.GetOpenGames().Count > 0 || main.GamesEditor?.RunningGames.Count > 0;
            var rows = new List<QueueRow>();
            foreach (var item in InstallQueue.GetItems())
            {
                var game = main.Database.Games.Get(item.GameId);
                string state;
                if (item.GameId == current)
                {
                    var percent = DirectInstallProgress.Get(item.GameId);
                    state = percent > 0 ?
                        string.Format(ResourceProvider.GetString("LOCAtrioDownloadsInstallingPercent"), percent) :
                        ResourceProvider.GetString("LOCAtrioDownloadsInstalling");
                }
                else if (rows.Count == 0 && startAt != null)
                {
                    state = string.Format(ResourceProvider.GetString("LOCAtrioQueueWaitsHour"), startAt.Value.ToLocalTime().ToString("HH:mm"));
                }
                else if (rows.Count == 0 && gameOpen)
                {
                    state = ResourceProvider.GetString("LOCAtrioQueueWaitsGame");
                }
                else
                {
                    state = rows.Count == 0 ? ResourceProvider.GetString("LOCAtrioQueueNext") : string.Format(ResourceProvider.GetString("LOCAtrioQueuePlace"), rows.Count + 1);
                }

                rows.Add(new QueueRow
                {
                    Game = game,
                    Entry = game == null ? null : findEntry(game.Id),
                    Name = game?.Name ?? item.Name,
                    StoreName = game == null ? string.Empty : GetStoreName(game),
                    StateText = state,
                    IsCurrent = item.GameId == current
                });
            }

            Queue = rows;
            QueueText = rows.Count == 0 ? null : string.Format(ResourceProvider.GetString(rows.Count == 1 ? "LOCAtrioQueueCountOne" : "LOCAtrioQueueCount"), rows.Count);
            OnPropertyChanged(nameof(StartLater));
            OnPropertyChanged(nameof(StartHour));
        }

        #endregion

        #region The updates of Steam

        private List<UpdateRow> updates = new List<UpdateRow>();
        public List<UpdateRow> Updates
        {
            get => updates;
            set
            {
                SetValue(ref updates, value);
                OnPropertyChanged(nameof(HasUpdates));
            }
        }

        public bool HasUpdates => updates?.Count > 0;

        public RelayCommand<object> OpenSteamDownloadsCommand => new RelayCommand<object>(_ => SteamUpdates.OpenDownloads());

        private void RefreshUpdates()
        {
            var findEntry = GetEntryFinder();
            var culture = Localization.ApplicationLanguageCultureInfo;
            var steamGames = main.Database.Games.Where(a => a.PluginId == SteamContent.SteamPluginId).GroupBy(a => a.GameId).ToDictionary(a => a.Key, a => a.First());
            Updates = SteamUpdates.GetPending().Select(a =>
            {
                steamGames.TryGetValue(a.AppId, out var game);
                string state;
                if (a.IsUpdating)
                {
                    state = string.Format(ResourceProvider.GetString("LOCAtrioUpdatesRunning"), a.Percent);
                }
                else if (game != null && (OpenGames.IsOpen(game.Id) || game.IsRunning))
                {
                    state = ResourceProvider.GetString("LOCAtrioUpdatesAfterClosing");
                }
                else if (a.Scheduled != null)
                {
                    state = string.Format(ResourceProvider.GetString("LOCAtrioUpdatesScheduled"), a.Scheduled.Value.ToString("d MMM HH:mm", culture));
                }
                else
                {
                    state = ResourceProvider.GetString("LOCAtrioUpdatesWaiting");
                }

                if (a.BytesLeft > 0)
                {
                    state += " · " + DirectUninstall.FormatSize(a.BytesLeft);
                }

                return new UpdateRow
                {
                    AppId = a.AppId,
                    Name = game?.Name ?? a.Name ?? a.AppId,
                    Game = game,
                    Entry = game == null ? null : findEntry(game.Id),
                    StateText = state
                };
            }).ToList();
        }

        #endregion

        #region What stayed behind

        private CancellationTokenSource leftoverMeasure;
        // Each folder measured, with the date it had then: a change of the folder measures it again.
        private static readonly Dictionary<string, Tuple<Playnite.Atrio.Leftovers.Measure, DateTime?>> measuredLeftovers =
            new Dictionary<string, Tuple<Playnite.Atrio.Leftovers.Measure, DateTime?>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Something was installed or uninstalled: the list of leftovers is made again with the page.</summary>
        private bool leftoversDirty;

        private List<LeftoverRow> leftovers = new List<LeftoverRow>();
        public List<LeftoverRow> Leftovers
        {
            get => leftovers;
            set
            {
                SetValue(ref leftovers, value);
                OnPropertyChanged(nameof(HasLeftovers));
            }
        }

        public bool HasLeftovers => leftovers?.Count > 0;

        private string leftoverText;
        public string LeftoverText
        {
            get => leftoverText;
            set => SetValue(ref leftoverText, value);
        }

        private bool hasLeftoverSelection;
        public bool HasLeftoverSelection
        {
            get => hasLeftoverSelection;
            set
            {
                SetValue(ref hasLeftoverSelection, value);
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private bool isCleaning;
        public bool IsCleaning
        {
            get => isCleaning;
            set
            {
                SetValue(ref isCleaning, value);
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public RelayCommand<object> RecycleLeftoversCommand => new RelayCommand<object>(_ => RecycleLeftovers(), _ => HasLeftoverSelection && !IsCleaning);
        public RelayCommand<LeftoverRow> OpenLeftoverCommand => new RelayCommand<LeftoverRow>(a =>
        {
            if (a?.Path != null && System.IO.Directory.Exists(a.Path))
            {
                ProcessStarter.StartProcess("explorer.exe", $"\"{a.Path}\"");
            }
        });

        public static string GetKindText(LeftoverKind kind)
        {
            switch (kind)
            {
                case LeftoverKind.SteamShaders:
                    return ResourceProvider.GetString("LOCAtrioLeftoverShaders");
                case LeftoverKind.SteamWorkshop:
                    return ResourceProvider.GetString("LOCAtrioLeftoverWorkshop");
                case LeftoverKind.SteamDownload:
                    return ResourceProvider.GetString("LOCAtrioLeftoverDownload");
                case LeftoverKind.EpicGame:
                    return ResourceProvider.GetString("LOCAtrioLeftoverEpic");
                case LeftoverKind.GogGame:
                    return ResourceProvider.GetString("LOCAtrioLeftoverGog");
                default:
                    return ResourceProvider.GetString("LOCAtrioLeftoverSteam");
            }
        }

        /// <summary>The folders that stayed behind, found in the background and then measured one by one.</summary>
        /// <summary>The folders that games installed, or being installed, say are theirs.</summary>
        private List<string> GetClaimedFolders(List<Game> games)
        {
            var claimed = new List<string>();
            foreach (var game in games.Where(a => (a.IsInstalled || a.IsInstalling || DirectInstallProgress.Get(a.Id) != null) && !a.InstallDirectory.IsNullOrWhiteSpace()))
            {
                try
                {
                    claimed.Add(game.ExpandVariables(game.InstallDirectory, true));
                }
                catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Debug(e, $"Failed to read the folder of {game.Name}.");
                }
            }

            return claimed;
        }

        private static List<string> GetSteamInstalled(List<Game> games)
        {
            return games.Where(a => a.PluginId == SteamContent.SteamPluginId && (a.IsInstalled || a.IsInstalling)).Select(a => a.GameId).ToList();
        }

        private static DateTime? FolderTime(string path)
        {
            try
            {
                return System.IO.Directory.GetLastWriteTime(path);
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private async void FindLeftovers()
        {
            leftoverMeasure?.Cancel();
            leftoverMeasure = new CancellationTokenSource();
            var token = leftoverMeasure.Token;
            var games = main.Database.Games.ToList();
            var claimed = GetClaimedFolders(games);
            var steamNames = games.Where(a => a.PluginId == SteamContent.SteamPluginId).GroupBy(a => a.GameId).ToDictionary(a => a.Key, a => a.First().Name);
            var steamInstalled = GetSteamInstalled(games);
            try
            {
                var found = await Task.Run(() => Playnite.Atrio.Leftovers.Find(Playnite.Atrio.Leftovers.ReadSources(claimed, steamInstalled)), token);

                // Another search started meanwhile: its list is the one that counts.
                if (token.IsCancellationRequested)
                {
                    return;
                }

                Leftovers = found.Select(a =>
                {
                    var name = a.AppId != null && steamNames.TryGetValue(a.AppId, out var known) ? known :
                        a.AppId != null ? string.Format(ResourceProvider.GetString("LOCAtrioLeftoverApp"), a.AppId) : a.Name;
                    return new LeftoverRow { Folder = a, Name = name, KindText = GetKindText(a.Kind), SelectionChanged = UpdateLeftoverSelection };
                }).ToList();
                UpdateLeftoverSelection();

                var now = DateTime.Now;
                foreach (var row in Leftovers.ToList())
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    // A big folder takes seconds of disk: it's measured again only when it changed.
                    var time = FolderTime(row.Path);
                    if (!measuredLeftovers.TryGetValue(row.Path, out var saved) || saved.Item2 != time)
                    {
                        var fresh = await Task.Run(() => Playnite.Atrio.Leftovers.MeasureFolder(row.Path, token), token);
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        saved = Tuple.Create(fresh, time);
                        measuredLeftovers[row.Path] = saved;
                    }

                    var measure = saved.Item1;
                    row.Size = measure.Size;
                    row.Measured = true;
                    row.LastUsedText = measure.LastChange == null ? null :
                        string.Format(ResourceProvider.GetString("LOCAtrioLeftoverChanged"), GetAgeText(measure.LastChange.Value, now));

                    UpdateLeftoverSelection();
                }

                // The biggest first, once everything is measured.
                Leftovers = Leftovers.OrderByDescending(a => a.Size).ToList();
            }
            catch (OperationCanceledException)
            {
                // Out of the page, or a new search: nothing else to do here.
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, "Failed to look for leftovers.");
            }
        }

        /// <summary>"3 days", "5 months": how long since a folder last changed.</summary>
        public static string GetAgeText(DateTime when, DateTime now)
        {
            var days = (now.Date - when.Date).TotalDays;
            if (days < 1)
            {
                return ResourceProvider.GetString("LOCAtrioAgeToday");
            }

            if (days < 60)
            {
                return string.Format(ResourceProvider.GetString(days < 2 ? "LOCAtrioAgeDay" : "LOCAtrioAgeDays"), (int)days);
            }

            var months = (int)Math.Round(days / 30.4);
            if (months < 24)
            {
                return string.Format(ResourceProvider.GetString("LOCAtrioAgeMonths"), months);
            }

            return string.Format(ResourceProvider.GetString("LOCAtrioAgeYears"), (int)(days / 365.25));
        }

        private void UpdateLeftoverSelection()
        {
            var all = Leftovers ?? new List<LeftoverRow>();
            var selected = all.Where(a => a.IsSelected).ToList();
            HasLeftoverSelection = selected.Count > 0;
            var total = all.Where(a => a.Measured).Sum(a => a.Size);
            LeftoverText = all.Count == 0 ? null : selected.Count > 0 ?
                string.Format(ResourceProvider.GetString("LOCAtrioLeftoverSelected"), selected.Count, DirectUninstall.FormatSize(selected.Sum(a => a.Size))) :
                string.Format(ResourceProvider.GetString(all.Count == 1 ? "LOCAtrioLeftoverCountOne" : "LOCAtrioLeftoverCount"), all.Count, DirectUninstall.FormatSize(total));
        }

        /// <summary>The question lists every folder with its size and its path. Cancel is the default answer.</summary>
        public static string GetLeftoverQuestion(IEnumerable<LeftoverRow> rows)
        {
            var list = rows.ToList();
            var text = new StringBuilder();
            text.AppendLine(string.Format(ResourceProvider.GetString(list.Count == 1 ? "LOCAtrioLeftoverAskOne" : "LOCAtrioLeftoverAsk"), list.Count));
            text.AppendLine();
            foreach (var row in list.Take(QuestionListed))
            {
                text.AppendLine($"• {row.Name} — {row.SizeText} — {row.Path}");
            }

            if (list.Count > QuestionListed)
            {
                text.AppendLine(string.Format(ResourceProvider.GetString("LOCAtrioStorageAskMore"), list.Count - QuestionListed));
            }

            text.AppendLine();
            text.AppendLine(string.Format(ResourceProvider.GetString("LOCAtrioStorageAskTotal"), DirectUninstall.FormatSize(list.Sum(a => a.Size))));
            text.Append(ResourceProvider.GetString("LOCAtrioLeftoverAskBin"));
            return text.ToString();
        }

        private async void RecycleLeftovers()
        {
            var rows = Leftovers.Where(a => a.IsSelected).ToList();
            if (rows.Count == 0 || IsCleaning)
            {
                return;
            }

            var yes = new MessageBoxOption(ResourceProvider.GetString("LOCAtrioLeftoverRecycle"));
            var no = new MessageBoxOption(ResourceProvider.GetString("LOCCancelLabel"), true, true);
            if (main.Dialogs.ShowMessage(GetLeftoverQuestion(rows), ResourceProvider.GetString("LOCAtrioLeftoverTitle"), MessageBoxImage.Question, new List<MessageBoxOption> { yes, no }) != yes)
            {
                return;
            }

            IsCleaning = true;
            var failed = new List<string>();
            var kept = new List<string>();
            try
            {
                // Looked for again right before: a game installed meanwhile can be using one of these folders now.
                var games = main.Database.Games.ToList();
                var claimed = GetClaimedFolders(games);
                var steamInstalled = GetSteamInstalled(games);
                var still = await Task.Run(() => new HashSet<string>(
                    Playnite.Atrio.Leftovers.Find(Playnite.Atrio.Leftovers.ReadSources(claimed, steamInstalled)).Select(a => a.Path),
                    StringComparer.OrdinalIgnoreCase));
                foreach (var row in rows)
                {
                    if (!still.Contains(row.Path))
                    {
                        logger.Info($"Storage page: {row.Path} isn't a leftover anymore, it stays.");
                        kept.Add(row.Name);
                        continue;
                    }

                    logger.Info($"Storage page: sending to the bin {row.Path} ({row.SizeText}).");
                    measuredLeftovers.Remove(row.Path);
                    if (!await Task.Run(() => Playnite.Atrio.Leftovers.SendToBin(row.Path)))
                    {
                        failed.Add(row.Name);
                    }
                }
            }
            finally
            {
                IsCleaning = false;
                FindLeftovers();
                Refresh();
            }

            var problems = new List<string>();
            if (kept.Count > 0)
            {
                problems.Add(string.Format(ResourceProvider.GetString("LOCAtrioLeftoverKept"), string.Join(", ", kept)));
            }

            if (failed.Count > 0)
            {
                problems.Add(string.Format(ResourceProvider.GetString("LOCAtrioLeftoverFailed"), string.Join(", ", failed)));
            }

            if (problems.Count > 0)
            {
                main.Dialogs.ShowMessage(string.Join(Environment.NewLine + Environment.NewLine, problems), ResourceProvider.GetString("LOCAtrioLeftoverTitle"));
            }
        }

        #endregion
    }
}
