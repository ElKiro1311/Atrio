using Playnite.Atrio;
using Playnite.Commands;
using Playnite.Common;
using Playnite.Converters;
using Playnite.DesktopApp.Atrio;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Playnite.DesktopApp.ViewModels
{
    public class HomeCard
    {
        public GamesCollectionViewEntry Game { get; set; }

        /// <summary>Shown above the first card of each period, like the shelves of a library.</summary>
        public string GroupLabel { get; set; }
        public bool IsWide { get; set; }
        public bool IsNew { get; set; }
        public string Caption { get; set; }
        public string RecentText { get; set; }
        public string TotalText { get; set; }

        /// <summary>A short note at the foot of the cover, like how many achievements are left.</summary>
        public string BadgeText { get; set; }
    }

    public class HomeNewsCard
    {
        public string GroupLabel { get; set; }
        public string Title { get; set; }
        public string GameName { get; set; }
        public string ImageUrl { get; set; }
        public string Url { get; set; }
    }

    public class HomeShelf
    {
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public List<HomeCard> Cards { get; set; }

        /// <summary>Null for the shelves that aren't a collection.</summary>
        public GameCollection Collection { get; set; }
        public bool HasCollection => Collection != null;
    }

    public class HomeViewModel : ObservableObject
    {
        private const int ShelfSize = 24;
        private const int AllGamesPreview = 42;
        private const int NewsGames = 8;
        private const int NewsSize = 10;
        private static readonly TimeSpan newGameAge = TimeSpan.FromDays(14);
        private static readonly PlayTimeToStringConverter playtimeConverter = new PlayTimeToStringConverter();

        private readonly DesktopAppViewModel main;
        private readonly DispatcherTimer refreshTimer;
        private CancellationTokenSource newsToken;
        private List<GamesCollectionViewEntry> entries = new List<GamesCollectionViewEntry>();

        private bool isEmpty;
        public bool IsEmpty
        {
            get => isEmpty;
            set => SetValue(ref isEmpty, value);
        }

        /// <summary>The game that gives its colors to the window while this page is showing.</summary>
        public GamesCollectionViewEntry AmbientGame { get; private set; }

        private List<HomeNewsCard> news;
        public List<HomeNewsCard> News
        {
            get => news;
            set => SetValue(ref news, value);
        }

        private List<HomeShelf> shelves;
        public List<HomeShelf> Shelves
        {
            get => shelves;
            set => SetValue(ref shelves, value);
        }

        private List<GamesCollectionViewEntry> allGames;
        public List<GamesCollectionViewEntry> AllGames
        {
            get => allGames;
            set => SetValue(ref allGames, value);
        }

        private string allGamesTitle;
        public string AllGamesTitle
        {
            get => allGamesTitle;
            set => SetValue(ref allGamesTitle, value);
        }

        private int allGamesSort;
        public int AllGamesSort
        {
            get => allGamesSort;
            set
            {
                SetValue(ref allGamesSort, value);
                UpdateAllGames();
            }
        }

        public RelayCommand<GamesCollectionViewEntry> OpenGameCommand { get; }
        public RelayCommand<GamesCollectionViewEntry> StartGameCommand { get; }
        public RelayCommand<HomeNewsCard> OpenNewsCommand { get; }
        public RelayCommand<object> OpenStoresCommand { get; }
        public RelayCommand<HomeShelf> OpenShelfCommand { get; }

        public HomeViewModel(DesktopAppViewModel main)
        {
            this.main = main;
            OpenGameCommand = new RelayCommand<GamesCollectionViewEntry>(OpenGame);
            StartGameCommand = new RelayCommand<GamesCollectionViewEntry>(a =>
            {
                if (a != null)
                {
                    main.StartGame(a.Game, true);
                }
            });
            OpenNewsCommand = new RelayCommand<HomeNewsCard>(a =>
            {
                if (a?.Url.IsNullOrEmpty() == false)
                {
                    GlobalCommands.NavigateUrl(a.Url);
                }
            });
            OpenStoresCommand = new RelayCommand<object>(_ => main.SwitchToStoresView());
            OpenShelfCommand = new RelayCommand<HomeShelf>(a => main.CollectionsModel.Open(a?.Collection));

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
            // Playing is what moves the shelves: anything else that changes in a game would only make them blink.
            if (e.UpdatedItems.Any(a => a.OldData.LastActivity != a.NewData.LastActivity || a.OldData.Playtime != a.NewData.Playtime))
            {
                RefreshSoon();
            }
        }

        public void StartWatching()
        {
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
            refreshTimer.Stop();
            newsToken?.Cancel();
            if (main.Database?.Games != null)
            {
                main.Database.Games.ItemCollectionChanged -= Games_ItemCollectionChanged;
                main.Database.Games.ItemUpdated -= Games_ItemUpdated;
            }
        }

        public void OpenGame(GamesCollectionViewEntry entry)
        {
            if (entry != null)
            {
                main.OpenGame(entry.Id);
            }
        }

        private static string GetPlaytime(ulong seconds)
        {
            return playtimeConverter.Convert(seconds, typeof(string), null, null)?.ToString();
        }

        /// <summary>When something happened, in the words of a shelf: today, this week, October, 2024...</summary>
        public static string GetPeriodLabel(DateTime date, DateTime today)
        {
            var days = (today.Date - date.Date).TotalDays;
            if (days <= 0)
            {
                return ResourceProvider.GetString("LOCAtrioPeriodToday");
            }

            if (days == 1)
            {
                return ResourceProvider.GetString("LOCAtrioPeriodYesterday");
            }

            if (days < 7)
            {
                return ResourceProvider.GetString("LOCAtrioPeriodWeek");
            }

            if (date.Year != today.Year)
            {
                return date.Year.ToString(CultureInfo.InvariantCulture);
            }

            if (date.Month == today.Month)
            {
                return ResourceProvider.GetString("LOCAtrioPeriodMonth");
            }

            var culture = Localization.ApplicationLanguageCultureInfo;
            var month = date.ToString("MMMM", culture);
            return culture.TextInfo.ToTitleCase(month);
        }

        private HomeCard CreateRecentCard(GamesCollectionViewEntry entry, string label, bool wide)
        {
            var copies = entry.Copies.Select(a => a.Game).ToList();
            var appId = SteamContent.GetAppId(copies);
            var minutes = appId == null ? null : SteamContent.GetRecentMinutes(appId);
            var total = GameUnifier.Current?.GetGroup(entry.Id)?.TotalPlaytime ?? entry.Playtime;
            return new HomeCard
            {
                Game = entry,
                GroupLabel = label,
                IsWide = wide,
                RecentText = minutes > 0 ?
                    string.Format(ResourceProvider.GetString("LOCAtrioPlaytimeRecent"), GetPlaytime((ulong)minutes.Value * 60)) :
                    null,
                TotalText = total > 0 ?
                    string.Format(ResourceProvider.GetString("LOCAtrioPlaytimeTotal"), GetPlaytime(total)) :
                    null
            };
        }

        private HomeShelf CreateRecentShelf(List<GamesCollectionViewEntry> played)
        {
            var today = DateTime.Today;
            var cards = new List<HomeCard>();
            string lastLabel = null;
            foreach (var entry in played.Take(ShelfSize))
            {
                var label = GetPeriodLabel(entry.LastActivity.Value, today);
                cards.Add(CreateRecentCard(entry, label == lastLabel ? " " : label, cards.Count == 0));
                lastLabel = label;
            }

            return cards.Count == 0 ? null : new HomeShelf { Title = ResourceProvider.GetString("LOCAtrioHomeRecent"), Cards = cards };
        }

        private HomeShelf CreatePlayNextShelf()
        {
            var unplayed = entries.Where(a => a.Playtime == 0 && a.LastActivity == null).
                OrderByDescending(a => a.IsInstalled).
                ThenByDescending(a => a.CommunityScore ?? 0).
                ThenByDescending(a => a.Added ?? DateTime.MinValue).
                Take(ShelfSize).
                ToList();
            if (unplayed.Count == 0)
            {
                return null;
            }

            return new HomeShelf
            {
                Title = ResourceProvider.GetString("LOCAtrioHomePlayNext"),
                Subtitle = ResourceProvider.GetString("LOCAtrioHomePlayNextHint"),
                Cards = unplayed.Select((a, index) => new HomeCard
                {
                    Game = a,
                    GroupLabel = " ",
                    IsWide = index == 0,
                    Caption = index == 0 ? ResourceProvider.GetString(a.IsInstalled ? "LOCAtrioHomeReadyToPlay" : "LOCAtrioHomeInYourLibrary") : null
                }).ToList()
            };
        }

        private HomeShelf CreateAddedShelf()
        {
            var limit = DateTime.Now - newGameAge;
            var added = entries.Where(a => a.Added != null).OrderByDescending(a => a.Added).Take(ShelfSize).ToList();
            if (added.Count == 0)
            {
                return null;
            }

            return new HomeShelf
            {
                Title = ResourceProvider.GetString("LOCAtrioHomeAdded"),
                Cards = added.Select(a => new HomeCard { Game = a, GroupLabel = " ", IsNew = a.Added > limit }).ToList()
            };
        }

        /// <summary>
        /// The games with most of their achievements unlocked, the closest to 100 % first. Each copy of a game
        /// counts for the game the page shows, and the one with more progress wins.
        /// </summary>
        internal static HomeShelf CreateAlmostDoneShelf(List<GamesCollectionViewEntry> entries, IEnumerable<AchievementProgress.Entry> progress)
        {
            var byCopy = new Dictionary<Guid, GamesCollectionViewEntry>();
            var byId = new Dictionary<Guid, GamesCollectionViewEntry>();
            foreach (var entry in entries)
            {
                byId[entry.Id] = entry;
                byCopy[entry.Id] = entry;
                foreach (var copy in entry.Copies ?? new List<GameCopyEntry>())
                {
                    if (copy?.Game != null && !byCopy.ContainsKey(copy.Game.Id))
                    {
                        byCopy[copy.Game.Id] = entry;
                    }
                }
            }

            var best = new Dictionary<Guid, AchievementProgress.Entry>();
            foreach (var item in progress ?? Enumerable.Empty<AchievementProgress.Entry>())
            {
                if (byCopy.TryGetValue(item.GameId, out var entry) &&
                    (!best.TryGetValue(entry.Id, out var other) || AlmostDoneShelf.GetPercent(item.Unlocked, item.Total) > AlmostDoneShelf.GetPercent(other.Unlocked, other.Total)))
                {
                    best[entry.Id] = item;
                }
            }

            var games = best.
                Where(a => AlmostDoneShelf.Qualifies(a.Value.Unlocked, a.Value.Total)).
                Select(a => new { Entry = byId[a.Key], Progress = a.Value }).
                OrderByDescending(a => AlmostDoneShelf.GetPercent(a.Progress.Unlocked, a.Progress.Total)).
                ThenBy(a => a.Progress.Total - a.Progress.Unlocked).
                ThenByDescending(a => a.Entry.LastActivity ?? DateTime.MinValue).
                Take(ShelfSize).
                ToList();
            if (games.Count == 0)
            {
                return null;
            }

            return new HomeShelf
            {
                Title = ResourceProvider.GetString("LOCAtrioHomeAlmostDone"),
                Subtitle = ResourceProvider.GetString("LOCAtrioHomeAlmostDoneHint"),
                Cards = games.Select((a, index) =>
                {
                    var text = AlmostDoneShelf.GetText(a.Progress.Unlocked, a.Progress.Total);
                    return new HomeCard
                    {
                        Game = a.Entry,
                        GroupLabel = " ",
                        IsWide = index == 0,
                        Caption = index == 0 ? text : null,
                        BadgeText = index == 0 ? null : text
                    };
                }).ToList()
            };
        }

        /// <summary>The almost finished games come once the add-on of the achievements has its numbers ready.</summary>
        private async void AddAlmostDoneShelf(List<HomeShelf> list)
        {
            var progress = await AchievementProgress.GetAsync(main);
            var shelf = CreateAlmostDoneShelf(entries, progress);
            if (shelf == null || main.IsDisposing || Shelves != list)
            {
                return;
            }

            // After the recent games and the shelves the player pinned, before the suggestions.
            var recent = ResourceProvider.GetString("LOCAtrioHomeRecent");
            var index = Math.Max(list.FindLastIndex(a => a.HasCollection), list.FindIndex(a => a.Title == recent)) + 1;
            var updated = new List<HomeShelf>(list);
            updated.Insert(index, shelf);
            Shelves = updated;
        }

        private HomeShelf CreateCollectionShelf(GameCollection collection)
        {
            var games = main.CollectionsModel.GetMembers(collection);
            if (games.Count == 0)
            {
                return null;
            }

            return new HomeShelf
            {
                Title = collection.Name,
                Subtitle = CollectionsViewModel.GetCountText(games.Count),
                Collection = collection,
                Cards = games.Take(ShelfSize).Select(a => new HomeCard { Game = a, GroupLabel = " " }).ToList()
            };
        }

        private void UpdateAllGames()
        {
            IEnumerable<GamesCollectionViewEntry> sorted;
            switch (allGamesSort)
            {
                case 1:
                    sorted = entries.OrderByDescending(a => a.Playtime).ThenBy(a => a.Name);
                    break;
                case 2:
                    sorted = entries.OrderByDescending(a => a.LastActivity ?? DateTime.MinValue).ThenBy(a => a.Name);
                    break;
                case 3:
                    sorted = entries.OrderByDescending(a => a.Added ?? DateTime.MinValue).ThenBy(a => a.Name);
                    break;
                default:
                    sorted = entries.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase);
                    break;
            }

            AllGames = sorted.Take(AllGamesPreview).ToList();
            AllGamesTitle = string.Format(
                ResourceProvider.GetString("LOCAtrioHomeAllGames"),
                entries.Count.ToString("N0", Localization.ApplicationLanguageCultureInfo));
        }

        private async void LoadNews(List<GamesCollectionViewEntry> played)
        {
            newsToken?.Cancel();
            var source = new CancellationTokenSource();
            newsToken = source;
            var games = played.
                Select(a => new { a.Name, AppId = SteamContent.GetAppId(a.Copies.Select(b => b.Game)) }).
                Where(a => a.AppId != null).
                Take(NewsGames).
                ToList();

            var found = new List<Tuple<string, string, SteamNewsItem>>();
            await Task.WhenAll(games.Select(async game =>
            {
                try
                {
                    var items = await SteamContent.GetNewsAsync(game.AppId, 2, source.Token).ConfigureAwait(false);
                    lock (found)
                    {
                        found.AddRange(items.Select(a => Tuple.Create(game.Name, game.AppId, a)));
                    }
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    // Without internet or with the service down the page simply has no news.
                    LogManager.GetLogger().Debug(e, $"Failed to read the news of {game.Name}.");
                }
            }));

            if (source.IsCancellationRequested)
            {
                return;
            }

            var today = DateTime.Today;
            var cards = new List<HomeNewsCard>();
            string lastLabel = null;
            foreach (var item in found.OrderByDescending(a => a.Item3.Date).Take(NewsSize))
            {
                var label = GetPeriodLabel(item.Item3.Date, today);
                cards.Add(new HomeNewsCard
                {
                    GroupLabel = label == lastLabel ? " " : label,
                    Title = item.Item3.Title,
                    GameName = item.Item1,
                    Url = item.Item3.Url,
                    ImageUrl = item.Item3.ImageUrl ?? SteamContent.GetArtUrl(item.Item2, SteamContent.HeaderFile)
                });
                lastLabel = label;
            }

            News = cards;
        }

        public void Refresh()
        {
            if (main.GamesView?.Items == null)
            {
                return;
            }

            var unifier = GameUnifier.Current;
            var seen = new HashSet<Guid>();
            entries = main.GamesView.Items.
                Where(a => !a.Hidden && unifier?.IsSecondaryCopy(a.Id) != true && seen.Add(a.Id)).
                ToList();

            var played = entries.Where(a => a.LastActivity != null).OrderByDescending(a => a.LastActivity).ToList();
            IsEmpty = entries.Count == 0;
            AmbientGame = played.FirstOrDefault(a => !a.BackgroundImage.IsNullOrEmpty()) ?? played.FirstOrDefault();

            var list = new List<HomeShelf>();
            void add(HomeShelf shelf)
            {
                if (shelf != null)
                {
                    list.Add(shelf);
                }
            }

            add(CreateRecentShelf(played));
            main.CollectionsModel.GetHomeCollections().ForEach(a => add(CreateCollectionShelf(a)));
            add(CreatePlayNextShelf());
            add(CreateAddedShelf());
            Shelves = list;
            AddAlmostDoneShelf(list);
            UpdateAllGames();
            LoadNews(played);
            main.UpdateAmbient();
        }
    }
}
