using Playnite;
using Playnite.Atrio;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.Settings;
using Playnite.Commands;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Playnite.Converters;

namespace Playnite.DesktopApp.ViewModels
{
    public class GamePageLink
    {
        public string Name { get; set; }
        public string Url { get; set; }
    }

    /// <summary>One small picture of the captures card: a screenshot, or the frame of a clip.</summary>
    public class CaptureTile : ObservableObject
    {
        public const int Shown = 6;

        public GameCapture Capture { get; set; }
        public string ToolTip { get; set; }
        public bool IsVideo => Capture?.IsVideo == true;

        private System.Windows.Media.Imaging.BitmapSource thumbnail;
        public System.Windows.Media.Imaging.BitmapSource Thumbnail
        {
            get => thumbnail;
            set => SetValue(ref thumbnail, value);
        }
    }

    public class SteamFriendsGroup
    {
        public string Title { get; set; }
        public bool ShowNames { get; set; }
        public List<SteamFriend> Friends { get; set; }
        public string MoreText { get; set; }
    }

    public class GameActivityEntry
    {
        public DateTime Date { get; set; }
        public string DateText { get; set; }
        public string Label { get; set; }
        public string Title { get; set; }
        public string Text { get; set; }
        public string ImageUrl { get; set; }
        public string Url { get; set; }

        public static GameActivityEntry Create(DateTime date, string label, string title, string text = null, string imageUrl = null, string url = null)
        {
            return new GameActivityEntry
            {
                Date = date,
                DateText = date.ToString("d MMMM yyyy", Localization.ApplicationLanguageCultureInfo),
                Label = label,
                Title = title,
                Text = text,
                ImageUrl = imageUrl,
                Url = url
            };
        }
    }

    public class GameDetailsViewModel : ObservableObject, IDisposable
    {
        private readonly System.Threading.CancellationTokenSource contentToken = new System.Threading.CancellationTokenSource();

        // Going through the list with the keyboard shouldn't ask Steam for every game on the way.
        private const int KeyboardPause = 100;

        // Long enough for Steam to send a picture, and short enough not to show the wrong game for long.
        private const int HeroPatience = 500;

        private static readonly ILogger logger = LogManager.GetLogger();
        private IResourceProvider resources;
        private IDialogsFactory dialogs;
        private DesktopGamesEditor editor;
        private PlayniteSettings settings;

        /// <summary>Year, developer and genres in one line, shown right above the title.</summary>
        public string MetaLine
        {
            get
            {
                if (Game == null)
                {
                    return string.Empty;
                }

                var parts = new List<string>();
                if (Game.ReleaseYear != null)
                {
                    parts.Add(Game.ReleaseYear.ToString());
                }

                var developers = Game.Game.Developers?.Take(1).Select(a => a.Name).ToList();
                if (developers.HasItems())
                {
                    parts.Add(developers[0]);
                }

                var genres = Game.Game.Genres?.Take(2).Select(a => a.Name).ToList();
                if (genres.HasItems())
                {
                    parts.Add(string.Join(", ", genres));
                }

                return string.Join("  ·  ", parts);
            }
        }

        public bool IsRunning
        {
            get
            {
                return Game != null && Game.IsOpen;
            }
        }

        public bool IsInstalling
        {
            get
            {
                return Game != null && Game.IsInstalling;
            }
        }

        public bool IsUninstalling
        {
            get
            {
                return Game != null && Game.IsUninstalling;
            }
        }

        public bool IsLaunching
        {
            get
            {
                return Game != null && Game.IsLaunching;
            }
        }

        public bool IsInstalled
        {
            get
            {
                return Game != null && Game.IsInstalled;
            }
        }

        public bool IsPlayAvailable
        {
            get
            {
                return Game != null && Game.IsInstalled && !IsRunning && !IsInstalling && !IsUninstalling && !IsLaunching;
            }
        }

        public bool IsContextAvailable
        {
            get
            {
                return Game != null && (IsRunning || IsInstalling || IsUninstalling || IsLaunching || !IsInstalled);
            }
        }

        public bool IsInstallAvailable
        {
            get
            {
                return Game != null && !Game.IsInstalled && !IsRunning && !IsInstalling && !IsUninstalling && !IsLaunching && Game.PluginId != Guid.Empty;
            }
        }

        private string GetInstallingText()
        {
            var copy = Game.Copies?.Select(a => a.Game).FirstOrDefault(a => a.IsInstalling) ?? Game.Game;
            var percent = DirectInstallProgress.Get(copy.Id);
            var installing = resources.GetString("LOCSetupRunning");
            return DirectInstallProgress.GetNotice(copy.Id) ?? (percent > 0 ? $"{installing} {percent} %" : installing);
        }

        private void DirectInstallProgress_Changed(object sender, Guid game)
        {
            OnPropertyChanged(nameof(ContextActionDescription));
        }

        /// <summary>The copy with programs running right now, whoever opened it. Null when nothing is detected.</summary>
        private Game GetOpenCopy()
        {
            return Game == null ? null : OpenGames.GetOpenCopy(Game.Copies.Select(a => a.Game));
        }

        public bool CanStop => Game?.IsInstalling != true && Game?.IsUninstalling != true && GetOpenCopy() != null;

        public string ContextActionDescription
        {
            get
            {
                if (CanStop)
                {
                    return resources.GetString("LOCAtrioStopGame");
                }
                else if (Game?.IsRunning == true)
                {
                    return resources.GetString("LOCGameRunning");
                }
                else if (Game?.IsLaunching == true)
                {
                    return resources.GetString("LOCGameLaunching");
                }
                else if (Game?.IsInstalling == true)
                {
                    return GetInstallingText();
                }
                else if (Game?.IsUninstalling == true)
                {
                    return resources.GetString("LOCUninstalling");
                }
                else if (Game?.IsInstalled == false)
                {
                    return resources.GetString("LOCInstallGame");
                }
                else if (Game?.IsInstalled == true)
                {
                    return resources.GetString("LOCPlayGame");
                }

                return "<ErrorState>";
            }
        }

        public Visibility SourceLibraryVisibility
        {
            get => (settings.DetailsVisibility.Library & Game.LibraryPlugin != null) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility PlayTimeVisibility
        {
            get => (settings.DetailsVisibility.PlayTime) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility InstallSizeVisibility
        {
            get => (settings.DetailsVisibility.InstallSize && game.InstallSize != null) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility InstallDirectoryVisibility
        {
            get => (settings.DetailsVisibility.InstallDirectory && game.IsInstalled && !string.IsNullOrWhiteSpace(game.InstallDirectory)) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility LastPlayedVisibility
        {
            get => (settings.DetailsVisibility.LastPlayed) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility AddedVisibility
        {
            get => (settings.DetailsVisibility.Added && game.Added != null) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility RecentActivityVisibility
        {
            get => (settings.DetailsVisibility.RecentActivity && (game.LastActivity != null || game.Added != null)) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility CompletionStatusVisibility
        {
            get => (settings.DetailsVisibility.CompletionStatus && game.CompletionStatus.Id != Guid.Empty) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility PlatformVisibility
        {
            get => (settings.DetailsVisibility.Platform && game.Platforms.HasItems()) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility GenreVisibility
        {
            get => (settings.DetailsVisibility.Genres && game.GenreIds.HasItems()) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility DeveloperVisibility
        {
            get => (settings.DetailsVisibility.Developers && game.DeveloperIds.HasItems()) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility PublisherVisibility
        {
            get => (settings.DetailsVisibility.Publishers && game.PublisherIds.HasItems()) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility ReleaseDateVisibility
        {
            get => (settings.DetailsVisibility.ReleaseDate && game.ReleaseDate != null) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility CategoryVisibility
        {
            get => (settings.DetailsVisibility.Categories && game.CategoryIds.HasItems()) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility TagVisibility
        {
            get => (settings.DetailsVisibility.Tags && game.TagIds.HasItems()) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility FeatureVisibility
        {
            get => (settings.DetailsVisibility.Features && game.FeatureIds.HasItems()) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility LinkVisibility
        {
            get => (settings.DetailsVisibility.Links && game.Links.HasItems()) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility DescriptionVisibility
        {
            get => (settings.DetailsVisibility.Description && !game.Description.IsNullOrEmpty()) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility NotesVisibility
        {
            get => (settings.DetailsVisibility.Notes && !game.Notes.IsNullOrEmpty()) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility CoverVisibility
        {
            get => (settings.DetailsVisibility.CoverImage) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility BackgroundVisibility
        {
            get => (settings.DetailsVisibility.BackgroundImage) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility IconVisibility
        {
            get => (settings.DetailsVisibility.Icon) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility AgeRatingVisibility
        {
            get => (settings.DetailsVisibility.AgeRating && game.AgeRatings.HasItems()) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility SeriesVisibility
        {
            get => (settings.DetailsVisibility.Series && game.Series.HasItems()) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility SourceVisibility
        {
            get => (settings.DetailsVisibility.Source && game.Source.Id != Guid.Empty) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility RegionVisibility
        {
            get => (settings.DetailsVisibility.Region && game.Regions.HasItems()) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility VersionVisibility
        {
            get => (settings.DetailsVisibility.Version && !game.Version.IsNullOrEmpty()) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility CommunityScoreVisibility
        {
            get => (settings.DetailsVisibility.CommunityScore && game.CommunityScore != null) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility CriticScoreVisibility
        {
            get => (settings.DetailsVisibility.CriticScore && game.CriticScore != null) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility UserScoreVisibility
        {
            get => (settings.DetailsVisibility.UserScore && game.UserScore != null) ? Visibility.Visible : Visibility.Collapsed;
        }

        public Visibility NameVisibility
        {
            get => settings.DetailsVisibility.Name ? Visibility.Visible : Visibility.Collapsed;
        }

        private GamesCollectionViewEntry game;
        public GamesCollectionViewEntry Game
        {
            get => game;
            set
            {
                game = value;
                OnPropertyChanged();
            }
        }

        public RelayCommand<Guid> SetLibraryFilterCommand { get; }
        public RelayCommand<DatabaseObject> SetPlatformFilterCommand { get; }
        public RelayCommand<DatabaseObject> SetPublisherFilterCommand { get; }
        public RelayCommand<DatabaseObject> SetDeveloperFilterCommand { get; }
        public RelayCommand<DatabaseObject> SetGenreFilterCommand { get; }
        public RelayCommand<ReleaseDate?> SetReleaseDateFilterCommand { get; }
        public RelayCommand<DatabaseObject> SetCategoryFilterCommand { get; }
        public RelayCommand<DatabaseObject> SetTagFilterCommand { get; }
        public RelayCommand<DatabaseObject> SetFeatureFilterCommand { get; }
        public RelayCommand<DatabaseObject> SetAgeRatingFilterCommand { get; }
        public RelayCommand<DatabaseObject> SetSeriesFilterCommand { get; }
        public RelayCommand<DatabaseObject> SetSourceFilterCommand { get; }
        public RelayCommand<DatabaseObject> SetRegionFilterCommand { get; }
        public RelayCommand<DatabaseObject> SetCompletionStatusFilterCommand { get; }
        public RelayCommand<Game> OpenGameLocationCommand { get; }
        public RelayCommand<string> SetVersionFilterCommand { get; }
        public RelayCommand<Link> OpenLinkCommand { get; }
        public RelayCommand<DatabaseObject> PlayCommand { get; }
        public RelayCommand<object> InstallCommand { get; }
        public RelayCommand<object> CheckSetupCommand { get; }
        public RelayCommand<object> CheckExecutionCommand { get; }
        public RelayCommand<object> EditGameCommand { get; }
        public RelayCommand<object> ContextActionCommand { get; }
        public bool DetailsExpanded
        {
            get => settings.GameDetailsExpanded;
            set
            {
                settings.GameDetailsExpanded = value;
                OnPropertyChanged();
            }
        }

        private bool showAchievements;
        public bool ShowAchievements
        {
            get => showAchievements;
            set
            {
                showAchievements = value;
                OnPropertyChanged();
            }
        }

        private int achievementsShown = AchievementCard.FirstCount;

        /// <summary>How many achievement icons the page shows. "Show more" makes it grow, and it starts again with each game.</summary>
        public int AchievementsShown
        {
            get => achievementsShown;
            private set
            {
                achievementsShown = value;
                OnPropertyChanged();
            }
        }

        /// <summary>Which achievements the card shows: all, the unlocked or the locked ones. The choice stays from one game to the next.</summary>
        public int AchievementFilter
        {
            get => AchievementCard.Filter;
            set
            {
                if (AchievementCard.Filter == value)
                {
                    return;
                }

                AchievementCard.Filter = value;
                AchievementsShown = AchievementCard.FirstCount;
                OnPropertyChanged();
            }
        }

        /// <summary>Whether the rarest achievements go first on the card.</summary>
        public bool AchievementRarestFirst
        {
            get => AchievementCard.RarestFirst;
            set
            {
                AchievementCard.RarestFirst = value;
                OnPropertyChanged();
            }
        }

        /// <summary>Whether the game is on Steam, so that its achievement guides are one click away.</summary>
        public bool HasSteamGuides => AchievementCard.GetSteamGuidesUrl(steamAppId) != null;

        private string heroImage;
        public string HeroImage
        {
            get => heroImage;
            set
            {
                if (heroImage != value)
                {
                    heroImage = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool heroOnItsWay;

        /// <summary>
        /// The picture is being asked for. The page keeps the one it has meanwhile: one change
        /// straight into the new picture looks better than going through an empty page.
        /// </summary>
        public bool HeroOnItsWay
        {
            get => heroOnItsWay;
            private set
            {
                if (heroOnItsWay != value)
                {
                    heroOnItsWay = value;
                    OnPropertyChanged();
                }
            }
        }

        private string logoImage;
        public string LogoImage
        {
            get => logoImage;
            set
            {
                if (logoImage != value)
                {
                    logoImage = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasLogo));
                }
            }
        }

        public bool HasLogo => !logoImage.IsNullOrEmpty();

        private List<GameActivityEntry> activity;
        public List<GameActivityEntry> Activity
        {
            get => activity;
            set
            {
                activity = value;
                OnPropertyChanged();
            }
        }

        private string recentPlaytimeText;
        public string RecentPlaytimeText
        {
            get => recentPlaytimeText;
            set
            {
                recentPlaytimeText = value;
                OnPropertyChanged();
            }
        }

        private List<GamePageLink> pageLinks;
        public List<GamePageLink> PageLinks
        {
            get => pageLinks;
            private set
            {
                pageLinks = value;
                OnPropertyChanged();
            }
        }

        private string playedText;
        public string PlayedText
        {
            get => playedText;
            private set
            {
                playedText = value;
                OnPropertyChanged();
            }
        }

        public string ReviewUrl { get; private set; }

        /// <summary>The screenshots and clips of the game on this computer, the newest first.</summary>
        private List<GameCapture> captures = new List<GameCapture>();

        private List<CaptureTile> captureTiles;
        public List<CaptureTile> CaptureTiles
        {
            get => captureTiles;
            private set
            {
                captureTiles = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasCaptures));
            }
        }

        public bool HasCaptures => CaptureTiles.HasItems();

        private string capturesTitle;
        public string CapturesTitle
        {
            get => capturesTitle;
            private set
            {
                capturesTitle = value;
                OnPropertyChanged();
            }
        }

        private RelayCommand<CaptureTile> openCaptureCommand;
        public RelayCommand<CaptureTile> OpenCaptureCommand =>
            openCaptureCommand ?? (openCaptureCommand = new RelayCommand<CaptureTile>(a => OpenCaptures(a == null ? 0 : captures.IndexOf(a.Capture))));

        private RelayCommand<object> showAllCapturesCommand;
        public RelayCommand<object> ShowAllCapturesCommand =>
            showAllCapturesCommand ?? (showAllCapturesCommand = new RelayCommand<object>(_ => OpenCaptures(0)));

        private void OpenCaptures(int index)
        {
            if (captures.Count == 0)
            {
                return;
            }

            Windows.CaptureViewerWindow.Open(game.DisplayName, captures, Math.Max(0, index));
        }

        /// <summary>The captures come after the page is already showing, and each small picture when it's ready.</summary>
        private async void LoadCaptures()
        {
            var token = contentToken.Token;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(KeyboardPause * 3), token);
                var names = game.Copies.Select(a => a.Game.Name).Where(a => !a.IsNullOrWhiteSpace()).Distinct().ToList();
                var appIds = game.Copies.
                    Where(a => a.Game.PluginId == SteamContent.SteamPluginId).
                    Select(a => a.Game.GameId).
                    ToList();
                var copies = new HashSet<Guid>(game.Copies.Select(a => a.Game.Id));
                var library = editor.Database?.Games;
                var found = await Task.Run(() =>
                {
                    var others = library?.Where(a => !copies.Contains(a.Id)).Select(a => a.Name).ToList() ?? new List<string>();
                    return GameCaptures.Find(names, appIds, CapturePlaces.Read(), others);
                }, token);
                token.ThrowIfCancellationRequested();
                captures = found;
                if (found.Count == 0)
                {
                    CaptureTiles = null;
                    return;
                }

                var culture = Localization.ApplicationLanguageCultureInfo;
                var tiles = found.Take(CaptureTile.Shown).Select(a => new CaptureTile
                {
                    Capture = a,
                    ToolTip = $"{a.Source} · {a.Date.ToString("f", culture)}"
                }).ToList();
                CapturesTitle = string.Format(resources.GetString("LOCAtrioCapturesTitle"), found.Count);
                CaptureTiles = tiles;
                foreach (var tile in tiles)
                {
                    tile.Thumbnail = await Task.Run(() => CaptureThumbnails.Get(tile.Capture, 320), token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Debug(e, $"Failed to read the captures of {game.Name}.");
            }
        }

        /// <summary>A game of Steam can show the trailers of its page in the store.</summary>
        public bool HasTrailers => steamAppId != null && DesktopAppViewModel.UsesLibraryPanes;

        private RelayCommand<object> showTrailersCommand;
        public RelayCommand<object> ShowTrailersCommand => showTrailersCommand ?? (showTrailersCommand = new RelayCommand<object>(_ =>
        {
            if (steamAppId != null)
            {
                Windows.TrailerWindow.Open(game.DisplayName, steamAppId);
            }
        }));

        /// <summary>The card of the saves shows while the copies are turned on.</summary>
        public bool ShowSavesCard => SaveBackups.IsEnabled && DesktopAppViewModel.UsesLibraryPanes;

        /// <summary>The buttons of the card wait: a copy of this game is being made, or a restore is under way.</summary>
        public bool SavesBusy => SavesCopying || game?.Copies.Any(a => SaveBackups.IsRestoring(a.Game.Id)) == true;

        private bool SavesCopying => SaveBackups.Busy != Guid.Empty && game?.Copies.Any(a => a.Game.Id == SaveBackups.Busy) == true;

        /// <summary>The copy whose saves are copied: the installed one, or the one Atrio would start.</summary>
        private Game SavesGame => game?.Copies.Select(a => a.Game).FirstOrDefault(a => a.IsInstalled) ?? game?.Game;

        public string SavesText
        {
            get
            {
                if (SavesCopying)
                {
                    return resources.GetString("LOCAtrioSavesBusy");
                }

                var info = game?.Copies.Select(a => SaveBackups.GetInfo(a.Game.Id)).Where(a => a != null).OrderByDescending(a => a.LastBackup ?? DateTime.MinValue).FirstOrDefault();
                if (info == null)
                {
                    return resources.GetString("LOCAtrioSavesNone");
                }

                if (info.LastBackup != null)
                {
                    return string.Format(
                        resources.GetString("LOCAtrioSavesLast"),
                        info.LastBackup.Value.ToString("f", Localization.ApplicationLanguageCultureInfo),
                        DirectUninstall.FormatSize(Math.Max(1, info.Bytes)));
                }

                return resources.GetString(string.IsNullOrEmpty(info.Title) ? "LOCAtrioSavesUnknown" : "LOCAtrioSavesNotFound");
            }
        }

        private RelayCommand<object> backupSavesCommand;
        public RelayCommand<object> BackupSavesCommand => backupSavesCommand ?? (backupSavesCommand = new RelayCommand<object>(async _ =>
        {
            var target = SavesGame;
            if (target != null)
            {
                await SaveBackups.BackupAsync(target, System.Threading.CancellationToken.None);
            }
        }, _ => !SavesBusy));

        private RelayCommand<object> restoreSavesCommand;
        public RelayCommand<object> RestoreSavesCommand => restoreSavesCommand ?? (restoreSavesCommand = new RelayCommand<object>(_ => RestoreSaves(), _ => !SavesBusy));

        private RelayCommand<object> openSavesFolderCommand;
        public RelayCommand<object> OpenSavesFolderCommand => openSavesFolderCommand ?? (openSavesFolderCommand = new RelayCommand<object>(_ =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(Ludusavi.BackupFolder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Ludusavi.BackupFolder) { UseShellExecute = true });
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, "Failed to open the folder of the copies of the saves.");
            }
        }));

        /// <summary>Puts back the last copy, after saying which one and that the current saves get a copy first.</summary>
        private async void RestoreSaves()
        {
            var target = SavesGame;
            if (target == null)
            {
                return;
            }

            if (game.Copies.Any(a => a.Game.IsRunning || a.Game.IsLaunching || OpenGames.IsOpen(a.Game.Id)))
            {
                dialogs.ShowMessage(string.Format(resources.GetString("LOCAtrioSavesCloseFirst"), game.DisplayName), resources.GetString("LOCAtrioSavesTitle"));
                return;
            }

            // One restore at a time: the buttons wait from the question until the end.
            if (!SaveBackups.TryStartRestore(target.Id))
            {
                return;
            }

            var name = game.DisplayName;
            var title = resources.GetString("LOCAtrioSavesTitle");
            try
            {
                var latest = await SaveBackups.GetLatestAsync(target, System.Threading.CancellationToken.None);
                if (latest == null)
                {
                    dialogs.ShowMessage(string.Format(resources.GetString("LOCAtrioSavesNoBackup"), name), title);
                    return;
                }

                var when = latest.When.ToString("f", Localization.ApplicationLanguageCultureInfo);
                if (dialogs.ShowMessage(
                        string.Format(resources.GetString("LOCAtrioSavesRestoreAsk"), name, when),
                        title,
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    return;
                }

                // Exactly the copy of the question, even if another came while it was open.
                var result = await SaveBackups.RestoreAsync(target, latest, System.Threading.CancellationToken.None);
                string text;
                switch (result)
                {
                    case SaveRestoreResult.Done:
                        text = string.Format(resources.GetString("LOCAtrioSavesRestoreDone"), name, when);
                        break;
                    case SaveRestoreResult.NoSafetyCopy:
                        text = string.Format(resources.GetString("LOCAtrioSavesRestoreNoSafety"), name);
                        break;
                    case SaveRestoreResult.Nothing:
                        text = string.Format(resources.GetString("LOCAtrioSavesRestoreNothing"), name);
                        break;
                    default:
                        text = string.Format(resources.GetString("LOCAtrioSavesRestoreFailed"), name, when);
                        break;
                }

                dialogs.ShowMessage(text, title);
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, $"Failed to restore the saves of {name}.");
                dialogs.ShowMessage(string.Format(resources.GetString("LOCAtrioSavesRestoreNothing"), name), title);
            }
            finally
            {
                SaveBackups.EndRestore(target.Id);
            }
        }

        private void SaveBackups_Changed(object sender, Guid e)
        {
            if (game?.Copies.Any(a => a.Game.Id == e) == true)
            {
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    OnPropertyChanged(nameof(SavesText));
                    OnPropertyChanged(nameof(SavesBusy));
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                }));
            }
        }

        private List<SteamFriendsGroup> friendGroups;
        public List<SteamFriendsGroup> FriendGroups
        {
            get => friendGroups;
            private set
            {
                friendGroups = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasFriendGroups));
                OnPropertyChanged(nameof(HasFriendsCard));
            }
        }

        private string friendsMessage;
        public string FriendsMessage
        {
            get => friendsMessage;
            private set
            {
                friendsMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasFriendsMessage));
                OnPropertyChanged(nameof(HasFriendsCard));
            }
        }

        private string friendsActionText;
        public string FriendsActionText
        {
            get => friendsActionText;
            private set
            {
                friendsActionText = value;
                OnPropertyChanged();
            }
        }

        public bool HasFriendGroups => FriendGroups.HasItems();
        public bool HasFriendsMessage => !FriendsMessage.IsNullOrEmpty();
        public bool HasFriendsCard => HasFriendGroups || HasFriendsMessage;
        public string AllFriendsUrl => $"https://steamcommunity.com/my/friendsthatplay/{steamAppId}";
        public RelayCommand<object> FriendsActionCommand { get; }

        private List<GameActivityEntry> newsActivity = new List<GameActivityEntry>();
        private string steamAppId;
        private string friendsActionUrl;

        public RelayCommand<string> OpenPageCommand { get; }
        public RelayCommand<object> ShowAchievementsCommand { get; }
        public RelayCommand<object> HideAchievementsCommand { get; }

        /// <summary>The parameter is how many achievements the game has, or the achievements themselves, of which the card counts the ones it shows.</summary>
        public RelayCommand<object> MoreAchievementsCommand { get; }

        /// <summary>The parameter is the name of the achievement.</summary>
        public RelayCommand<string> SearchAchievementGuideCommand { get; }

        /// <summary>The parameter is the achievement: its page on RetroAchievements, or the achievement guides of the game on Steam.</summary>
        public RelayCommand<object> OpenAchievementGuideCommand { get; }
        public RelayCommand<GameCopyEntry> StartCopyCommand { get; }
        public RelayCommand<GameCopyEntry> PreferCopyCommand { get; }
        public RelayCommand<GameCopyEntry> SplitCopyCommand { get; }

        public GameDetailsViewModel(GamesCollectionViewEntry game, PlayniteSettings settings)
        {
            this.resources = new ResourceProvider();
            this.settings = settings;
            Game = game;
        }

        public GameDetailsViewModel(GamesCollectionViewEntry game, PlayniteSettings settings, DesktopGamesEditor editor, IDialogsFactory dialogs, IResourceProvider resources)
        {
            OpenLinkCommand = new RelayCommand<Link>((a) =>
            {
                try
                {
                    GlobalCommands.NavigateUrl(Game.Game.ExpandVariables(a.Url));
                }
                catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Error(e, "Failed to open url.");
                }
            });

            SetLibraryFilterCommand = new RelayCommand<Guid>((a) => SetLibraryFilter(a));
            SetPlatformFilterCommand = new RelayCommand<DatabaseObject>((a) => SetFilter(a, GameField.Platforms));
            SetPublisherFilterCommand = new RelayCommand<DatabaseObject>((a) => SetFilter(a, GameField.Publishers));
            SetDeveloperFilterCommand = new RelayCommand<DatabaseObject>((a) => SetFilter(a, GameField.Developers));
            SetGenreFilterCommand = new RelayCommand<DatabaseObject>((a) => SetFilter(a, GameField.Genres));
            SetReleaseDateFilterCommand = new RelayCommand<ReleaseDate?>((a) => SetReleaseDateFilter(a));
            SetCategoryFilterCommand = new RelayCommand<DatabaseObject>((a) => SetFilter(a, GameField.Categories));
            SetTagFilterCommand = new RelayCommand<DatabaseObject>((a) => SetFilter(a, GameField.Tags));
            SetFeatureFilterCommand = new RelayCommand<DatabaseObject>((a) => SetFilter(a, GameField.Features));
            SetAgeRatingFilterCommand = new RelayCommand<DatabaseObject>((a) => SetFilter(a, GameField.AgeRatings));
            SetSeriesFilterCommand = new RelayCommand<DatabaseObject>((a) => SetFilter(a, GameField.Series));
            SetSourceFilterCommand = new RelayCommand<DatabaseObject>((a) => SetFilter(a, GameField.Source));
            SetRegionFilterCommand = new RelayCommand<DatabaseObject>((a) => SetFilter(a, GameField.Regions));
            SetVersionFilterCommand = new RelayCommand<string>((filter) => SetVersionFilter(filter));
            SetCompletionStatusFilterCommand = new RelayCommand<DatabaseObject>((a) => SetFilter(a, GameField.CompletionStatus));
            PlayCommand = new RelayCommand<DatabaseObject>((a) => Play());
            OpenPageCommand = new RelayCommand<string>(a =>
            {
                if (!a.IsNullOrEmpty())
                {
                    GlobalCommands.NavigateUrl(a);
                }
            });
            FriendsActionCommand = new RelayCommand<object>(_ => DesktopApplication.Current?.MainModel?.OpenInBrowser(friendsActionUrl));
            ShowAchievementsCommand = new RelayCommand<object>(_ => ShowAchievements = true);
            HideAchievementsCommand = new RelayCommand<object>(_ => ShowAchievements = false);
            MoreAchievementsCommand = new RelayCommand<object>(total =>
                AchievementsShown = AchievementCard.Next(AchievementsShown, total as int? ?? AchievementCard.Count(total, AchievementFilter)));
            OpenAchievementGuideCommand = new RelayCommand<object>(achievement =>
            {
                try
                {
                    var url = AchievementCard.GetDirectGuideUrl(achievement, steamAppId);
                    if (url != null)
                    {
                        GlobalCommands.NavigateUrl(url);
                    }
                }
                catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Error(e, "Failed to open the guide of an achievement.");
                }
            });
            SearchAchievementGuideCommand = new RelayCommand<string>(name =>
            {
                try
                {
                    GlobalCommands.NavigateUrl(AchievementCard.GetGuideUrl(Game?.Name, name, resources.GetString("LOCAtrioAchievementGuideWords")));
                }
                catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Error(e, "Failed to search for an achievement guide.");
                }
            });
            StartCopyCommand = new RelayCommand<GameCopyEntry>(StartCopy);
            PreferCopyCommand = new RelayCommand<GameCopyEntry>(a =>
            {
                if (a != null)
                {
                    GameUnifier.Current.SetPreferredCopy(a.Game);
                }
            });
            SplitCopyCommand = new RelayCommand<GameCopyEntry>(a =>
            {
                if (a != null)
                {
                    GameUnifier.Current.SplitCopy(a.Game);
                }
            });
            InstallCommand = new RelayCommand<object>((a) => Install());
            CheckSetupCommand = new RelayCommand<object>((a) => CheckSetup());
            CheckExecutionCommand = new RelayCommand<object>((a) => CheckExecution());
            EditGameCommand = new RelayCommand<object>((a) => EditGame());
            ContextActionCommand = new RelayCommand<object>((a) =>
            {
                if (Game?.IsInstalling == true || Game?.IsUninstalling == true)
                {
                    CheckSetup();
                }
                else if (CanStop)
                {
                    StopGame();
                }
                else if (Game?.IsRunning == true || Game?.IsLaunching == true)
                {
                    CheckExecution();
                }
                else if (Game?.IsInstalled == false)
                {
                    Install();
                }
                else if (Game?.IsInstalled == true)
                {
                    Play();
                }
            });

            OpenGameLocationCommand = new RelayCommand<Game>((a) =>
            {
                editor.OpenGameLocation(a);
            });

            this.resources = resources;
            this.dialogs = dialogs;
            this.editor = editor;
            this.settings = settings;
            Game = game;
            PreparePageContent();
            if (game != null)
            {
                Game.PropertyChanged += Game_PropertyChanged;
            }

            settings.PropertyChanged += Settings_PropertyChanged;
            DirectInstallProgress.Changed += DirectInstallProgress_Changed;
            OpenGames.Changed += OpenGames_Changed;
            SaveBackups.Changed += SaveBackups_Changed;
        }

        private void OpenGames_Changed(object sender, OpenGamesChangedEventArgs e)
        {
            if (Game != null && Game.Copies.Any(a => e.All.Contains(a.Game.Id)))
            {
                NotifyStateChange();
            }
        }

        private async void StopGame()
        {
            var copy = GetOpenCopy();
            if (copy != null)
            {
                await OpenGames.StopAsync(copy, Game.DisplayName, dialogs);
            }
        }

        private static readonly Tuple<string, string>[] steamPages = new[]
        {
            Tuple.Create("LOCAtrioPageStore", "https://store.steampowered.com/app/{0}"),
            Tuple.Create("LOCAtrioPageDlc", "https://store.steampowered.com/dlc/{0}"),
            Tuple.Create("LOCAtrioPageCommunity", "https://steamcommunity.com/app/{0}"),
            Tuple.Create("LOCAtrioPageDiscussions", "https://steamcommunity.com/app/{0}/discussions/"),
            Tuple.Create("LOCAtrioPageGuides", "https://steamcommunity.com/app/{0}/guides/"),
            Tuple.Create("LOCAtrioPageMarket", "https://steamcommunity.com/market/search?appid={0}"),
            Tuple.Create("LOCAtrioPageSupport", "https://help.steampowered.com/wizard/HelpWithGame/?appid={0}")
        };

        private void PreparePageContent()
        {
            var appId = SteamContent.GetAppId(game.Copies.Select(a => a.Game));
            UpdatePageLinks(appId);
            ReviewUrl = appId == null ? null : $"https://store.steampowered.com/recommended/recommendgame/{appId}";
            UpdatePlayedContent();
            steamAppId = appId;
            OnPropertyChanged(nameof(HasSteamGuides));
            OnPropertyChanged(nameof(HasTrailers));
            if (DesktopAppViewModel.UsesLibraryPanes)
            {
                LoadCaptures();
            }
            if (appId != null && DesktopAppViewModel.UsesLibraryPanes)
            {
                // What is already saved comes with the page: the picture changes straight into the
                // new one, instead of going away and coming back when the rest arrives.
                HeroImage = SteamContent.GetSavedArt(appId, SteamContent.WideHeroFile) ?? SteamContent.GetSavedArt(appId, SteamContent.HeroFile);
                HeroOnItsWay = HeroImage == null;
                LogoImage = SteamContent.GetSavedLogo(appId);
                LoadSteamContent(appId);
                var friends = SteamFriends.GetKnown(appId);
                if (friends != null)
                {
                    ShowFriends(friends);
                }
                else
                {
                    ReloadFriends();
                }
            }
        }

        /// <summary>The question goes to Steam with the session of the browser of Atrio.</summary>
        public async void ReloadFriends()
        {
            if (steamAppId == null)
            {
                return;
            }

            var token = contentToken.Token;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(KeyboardPause), token);
                var result = await SteamFriends.GetAsync(steamAppId, token);
                token.ThrowIfCancellationRequested();
                ShowFriends(result);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Debug(e, "Failed to read the friends that play a game.");
            }
        }

        public void ShowFriends(SteamFriendsResult result)
        {
            FriendGroups = new[]
            {
                CreateFriendsGroup(result.InGame, "LOCAtrioFriendsInGameOne", "LOCAtrioFriendsInGame", true),
                CreateFriendsGroup(result.PlayedRecently, "LOCAtrioFriendsRecentOne", "LOCAtrioFriendsRecent", true),
                CreateFriendsGroup(result.PlayedBefore, "LOCAtrioFriendsBeforeOne", "LOCAtrioFriendsBefore", false),
                CreateFriendsGroup(result.Owners, "LOCAtrioFriendsOwnOne", "LOCAtrioFriendsOwn", false)
            }.Where(a => a != null).ToList();

            FriendsMessage = result.State == SteamFriendsState.SignedOut ? resources.GetString("LOCAtrioFriendsSignIn") :
                result.State == SteamFriendsState.Locked ? resources.GetString("LOCAtrioFriendsLocked") : null;
            FriendsActionText = resources.GetString(result.State == SteamFriendsState.SignedOut ? "LOCAtrioFriendsSignInButton" : "LOCAtrioFriendsLockedButton");
            friendsActionUrl = result.State == SteamFriendsState.SignedOut ? "https://steamcommunity.com/login/home/" : "https://store.steampowered.com/";
        }

        private SteamFriendsGroup CreateFriendsGroup(List<SteamFriend> friends, string oneKey, string manyKey, bool showNames)
        {
            if (!friends.HasItems())
            {
                return null;
            }

            var converter = new PlayTimeToStringConverter();
            string playtime(int minutes) => converter.Convert((ulong)minutes * 60, typeof(string), null, null)?.ToString();
            foreach (var friend in friends)
            {
                friend.Name = friend.Name ?? friend.SteamId;
                friend.DetailText = friend.RecentMinutes > 0 ? string.Format(resources.GetString("LOCAtrioPlaytimeRecent"), playtime(friend.RecentMinutes)) :
                    friend.TotalMinutes > 0 ? string.Format(resources.GetString("LOCAtrioPlaytimeTotal"), playtime(friend.TotalMinutes)) :
                    null;
            }

            var limit = showNames ? SteamFriends.NamedLimit : SteamFriends.PictureLimit;
            return new SteamFriendsGroup
            {
                Title = friends.Count == 1 ? resources.GetString(oneKey) : string.Format(resources.GetString(manyKey), friends.Count),
                ShowNames = showNames,
                Friends = friends.Take(limit).ToList(),
                MoreText = friends.Count > limit ? $"+{friends.Count - limit}" : null
            };
        }

        private void UpdatePageLinks(string appId)
        {
            PageLinks = appId != null ?
                steamPages.Select(a => new GamePageLink { Name = resources.GetString(a.Item1), Url = string.Format(a.Item2, appId) }).ToList() :
                game.Game.Links?.Select(a => new GamePageLink { Name = a.Name, Url = game.Game.ExpandVariables(a.Url) }).ToList();

        }

        /// <summary>Everything on the page that a play session changes.</summary>
        private void UpdatePlayedContent()
        {
            var total = GameUnifier.Current?.GetGroup(game.Id)?.TotalPlaytime ?? game.Playtime;
            PlayedText = total > 0 ?
                string.Format(resources.GetString("LOCAtrioPlayedFor"), new PlayTimeToStringConverter().Convert(total, typeof(string), null, null)) :
                null;
            Activity = GetLocalActivity().Concat(newsActivity).OrderByDescending(a => a.Date).ToList();
        }

        private List<GameActivityEntry> GetLocalActivity()
        {
            var list = new List<GameActivityEntry>();
            if (game.Game.LastActivity != null)
            {
                list.Add(GameActivityEntry.Create(game.Game.LastActivity.Value, resources.GetString("LOCAtrioActivityGame"), resources.GetString("LOCAtrioActivityPlayed")));
            }

            if (game.Game.Added != null)
            {
                list.Add(GameActivityEntry.Create(game.Game.Added.Value, resources.GetString("LOCAtrioActivityGame"), resources.GetString("LOCAtrioActivityAdded")));
            }

            return list;
        }

        /// <summary>The picture, the logo and the news of Steam arrive after the page is already showing.</summary>
        private async void LoadSteamContent(string appId)
        {
            var token = contentToken.Token;
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(KeyboardPause), token);
                var hero = SteamContent.GetHeroAsync(appId, token);
                var logo = SteamContent.GetLogoAsync(appId, token);
                var latestNews = SteamContent.GetNewsAsync(appId, 5, token);
                var minutes = await Task.Run(() => SteamContent.GetRecentMinutes(appId), token);
                if (minutes > 0)
                {
                    var text = new PlayTimeToStringConverter().Convert((ulong)minutes.Value * 60, typeof(string), null, null);
                    RecentPlaytimeText = string.Format(resources.GetString("LOCAtrioPlaytimeRecent"), text);
                }

                await Task.WhenAny(hero, Task.Delay(TimeSpan.FromMilliseconds(HeroPatience), token));
                HeroOnItsWay = false;
                HeroImage = await hero;
                LogoImage = await logo;
                var news = await latestNews;
                token.ThrowIfCancellationRequested();
                newsActivity = news.Select(a => GameActivityEntry.Create(a.Date, resources.GetString("LOCAtrioActivityNews"), a.Title, a.Text, a.ImageUrl, a.Url)).ToList();
                UpdatePlayedContent();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                // Without internet the page stays with what the library knows.
                HeroOnItsWay = false;
                logger.Debug(e, $"Failed to load the Steam content of {appId}.");
            }
        }

        public void Dispose()
        {
            DirectInstallProgress.Changed -= DirectInstallProgress_Changed;
            OpenGames.Changed -= OpenGames_Changed;
            SaveBackups.Changed -= SaveBackups_Changed;
            contentToken.Cancel();
            if (game != null)
            {
                Game.PropertyChanged -= Game_PropertyChanged;
            }

            settings.PropertyChanged -= Settings_PropertyChanged;
        }

        private void Settings_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            NotifyVisibilityChange();
        }

        private void Game_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Playnite.SDK.Models.Game.Playtime) || e.PropertyName == nameof(Playnite.SDK.Models.Game.LastActivity))
            {
                UpdatePlayedContent();
            }
            else if (e.PropertyName == nameof(Playnite.SDK.Models.Game.Links))
            {
                UpdatePageLinks(SteamContent.GetAppId(game.Copies.Select(a => a.Game)));
            }

            NotifyStateChange();
        }

        private void NotifyStateChange()
        {
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(CanStop));
            OnPropertyChanged(nameof(IsInstalling));
            OnPropertyChanged(nameof(IsUninstalling));
            OnPropertyChanged(nameof(IsLaunching));
            OnPropertyChanged(nameof(IsPlayAvailable));
            OnPropertyChanged(nameof(IsContextAvailable));
            OnPropertyChanged(nameof(IsInstallAvailable));
            OnPropertyChanged(nameof(ContextActionDescription));
            NotifyVisibilityChange();
        }

        private void NotifyVisibilityChange()
        {
            foreach (var prop in GetType().GetProperties().Where(a => a.Name.EndsWith("Visibility")))
            {
                OnPropertyChanged(prop.Name);
            }
        }

        public void SetFilter(DatabaseObject value, GameField filterField)
        {
            var filter = new IdItemFilterItemProperties() { Ids = new List<Guid> { value.Id } };
            switch (filterField)
            {
                case GameField.Platforms:
                    settings.FilterSettings.Platform = filter;
                    break;
                case GameField.Genres:
                    settings.FilterSettings.Genre = filter;
                    break;
                case GameField.Developers:
                    settings.FilterSettings.Developer = filter;
                    break;
                case GameField.Publishers:
                    settings.FilterSettings.Publisher = filter;
                    break;
                case GameField.Categories:
                    settings.FilterSettings.Category = filter;
                    break;
                case GameField.Tags:
                    settings.FilterSettings.Tag = filter;
                    break;
                case GameField.AgeRatings:
                    settings.FilterSettings.AgeRating = filter;
                    break;
                case GameField.Series:
                    settings.FilterSettings.Series = filter;
                    break;
                case GameField.Regions:
                    settings.FilterSettings.Region = filter;
                    break;
                case GameField.Source:
                    settings.FilterSettings.Source = filter;
                    break;
                case GameField.Features:
                    settings.FilterSettings.Feature = filter;
                    break;
                case GameField.CompletionStatus:
                    settings.FilterSettings.CompletionStatuses = filter;
                    break;
                default:
                    break;
            }

            ShowFilteredGames();
        }

        /// <summary>What a link of the page leads to is the games it was asking for.</summary>
        private void ShowFilteredGames()
        {
            settings.FilterPanelVisible = true;
            DesktopApplication.Current?.MainModel?.ShowLibraryList();
        }

        public void SetReleaseDateFilter(ReleaseDate? date)
        {
            if (date != null)
            {
                settings.FilterSettings.ReleaseYear = new StringFilterItemProperties(date.Value.Year.ToString());
                ShowFilteredGames();
            }
        }

        public void SetVersionFilter(string version)
        {
            if (!version.IsNullOrEmpty())
            {
                settings.FilterSettings.Version = version;
                ShowFilteredGames();
            }
        }

        public void SetLibraryFilter(Guid LibraryId)
        {
            var filter = new IdItemFilterItemProperties() { Ids = new List<Guid> { LibraryId } };
            settings.FilterSettings.Library = filter;
            ShowFilteredGames();
        }

        public void Play()
        {
            DesktopApplication.Current.MainModel.StartGame(game.Game, true);
        }

        public void Install()
        {
            DesktopApplication.Current.MainModel.InstallGame(game.Game);
        }

        private void StartCopy(GameCopyEntry copy)
        {
            if (copy == null)
            {
                return;
            }

            if (copy.IsInstalled)
            {
                editor.PlayGame(copy.Game, true);
            }
            else
            {
                editor.InstallGame(copy.Game);
            }
        }

        public void EditGame()
        {
            editor.EditGame(game.Game);
        }

        public void CheckSetup()
        {
            if (dialogs.ShowMessage(
                resources.GetString("LOCCancelMonitoringSetupAsk"),
                resources.GetString("LOCCancelMonitoringAskTitle"),
                MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                editor.CancelGameMonitoring(game.Game);
            }
        }

        public void CheckExecution()
        {
            if (dialogs.ShowMessage(
                resources.GetString("LOCCancelMonitoringExecutionAsk"),
                resources.GetString("LOCCancelMonitoringAskTitle"),
                MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                editor.CancelGameMonitoring(game.Game);
            }
        }
    }
}

