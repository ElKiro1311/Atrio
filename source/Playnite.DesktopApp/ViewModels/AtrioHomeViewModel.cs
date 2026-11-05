using Playnite.Atrio;
using Playnite.Common;
using Playnite.Converters;
using Playnite.SDK;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;

namespace Playnite.DesktopApp.ViewModels
{
    public class HomeShelf
    {
        public string Title { get; set; }
        public List<GamesCollectionViewEntry> Items { get; set; }
    }

    public class HomeRankedGame
    {
        public int Rank { get; set; }
        public GamesCollectionViewEntry Game { get; set; }
    }

    public class HomeStoreTile
    {
        public Guid PluginId { get; set; }
        public string Name { get; set; }
        public object Icon { get; set; }
        public string CountText { get; set; }
        public Brush CardBrush { get; set; }
        public Brush ColorBrush { get; set; }
    }

    public class HomeFeaturedGame : ObservableObject
    {
        public GamesCollectionViewEntry Game { get; set; }
        public string Label { get; set; }
        public string Details { get; set; }
        public string ActionText { get; set; }

        private bool isSelected;
        public bool IsSelected
        {
            get => isSelected;
            set => SetValue(ref isSelected, value);
        }
    }

    public class HomeViewModel : ObservableObject
    {
        private const int ShelfSize = 20;
        private const int FeaturedSize = 5;
        private const int RankedSize = 10;
        private static readonly TimeSpan rotationInterval = TimeSpan.FromSeconds(9);
        private static readonly PlayTimeToStringConverter playtimeConverter = new PlayTimeToStringConverter();

        private readonly DesktopAppViewModel main;
        private readonly DispatcherTimer rotationTimer;

        private List<HomeFeaturedGame> featured;
        public List<HomeFeaturedGame> Featured
        {
            get => featured;
            set => SetValue(ref featured, value);
        }

        private HomeFeaturedGame hero;
        public HomeFeaturedGame Hero
        {
            get => hero;
            set
            {
                if (hero != null)
                {
                    hero.IsSelected = false;
                }

                SetValue(ref hero, value);
                if (hero != null)
                {
                    hero.IsSelected = true;
                }

                main.UpdateAmbient();
            }
        }

        private bool pauseRotation;
        public bool PauseRotation
        {
            get => pauseRotation;
            set => SetValue(ref pauseRotation, value);
        }

        private string greeting;
        public string Greeting
        {
            get => greeting;
            set => SetValue(ref greeting, value);
        }

        private string summary;
        public string Summary
        {
            get => summary;
            set => SetValue(ref summary, value);
        }

        private bool isEmpty;
        public bool IsEmpty
        {
            get => isEmpty;
            set => SetValue(ref isEmpty, value);
        }

        private List<GamesCollectionViewEntry> recent;
        public List<GamesCollectionViewEntry> Recent
        {
            get => recent;
            set => SetValue(ref recent, value);
        }

        private HomeShelf ready;
        public HomeShelf Ready
        {
            get => ready;
            set => SetValue(ref ready, value);
        }

        private List<HomeStoreTile> stores;
        public List<HomeStoreTile> Stores
        {
            get => stores;
            set => SetValue(ref stores, value);
        }

        private List<HomeRankedGame> ranked;
        public List<HomeRankedGame> Ranked
        {
            get => ranked;
            set => SetValue(ref ranked, value);
        }

        private List<HomeShelf> shelves;
        public List<HomeShelf> Shelves
        {
            get => shelves;
            set => SetValue(ref shelves, value);
        }

        public RelayCommand<GamesCollectionViewEntry> OpenGameCommand { get; }
        public RelayCommand<GamesCollectionViewEntry> StartGameCommand { get; }
        public RelayCommand<HomeFeaturedGame> SelectFeaturedCommand { get; }
        public RelayCommand<HomeStoreTile> OpenStoreCommand { get; }
        public RelayCommand<object> OpenLibraryCommand { get; }
        public RelayCommand<object> OpenStoresCommand { get; }

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
            SelectFeaturedCommand = new RelayCommand<HomeFeaturedGame>(a =>
            {
                Hero = a;
                rotationTimer.Stop();
                rotationTimer.Start();
            });
            OpenStoreCommand = new RelayCommand<HomeStoreTile>(OpenStore);
            OpenLibraryCommand = new RelayCommand<object>(_ => main.SwitchToLibraryView());
            OpenStoresCommand = new RelayCommand<object>(_ => main.SwitchToStoresView());

            rotationTimer = new DispatcherTimer { Interval = rotationInterval };
            rotationTimer.Tick += (_, __) => ShowNextFeatured();
        }

        private void ShowNextFeatured()
        {
            if (PauseRotation || Featured == null || Featured.Count < 2)
            {
                return;
            }

            var index = Featured.IndexOf(Hero);
            Hero = Featured[(index + 1) % Featured.Count];
        }

        public void StartRotation()
        {
            rotationTimer.Start();
        }

        public void StopRotation()
        {
            rotationTimer.Stop();
        }

        public void OpenGame(GamesCollectionViewEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            main.SwitchToLibraryView();
            main.SelectGame(entry.Id);
            if (main.AppSettings.ViewSettings.GamesViewType == DesktopView.Grid)
            {
                main.AppSettings.GridViewSideBarVisible = true;
            }
        }

        private void OpenStore(HomeStoreTile tile)
        {
            if (tile == null)
            {
                return;
            }

            main.AppSettings.FilterSettings.Library = new IdItemFilterItemProperties(tile.PluginId);
            main.SwitchToLibraryView();
        }

        private static string GetGreetingKey()
        {
            var hour = DateTime.Now.Hour;
            return hour < 6 ? "LOCAtrioGreetingNight" : hour < 13 ? "LOCAtrioGreetingMorning" : hour < 20 ? "LOCAtrioGreetingAfternoon" : "LOCAtrioGreetingNight";
        }

        private HomeFeaturedGame CreateFeatured(GamesCollectionViewEntry entry, string labelKey)
        {
            var details = new List<string>();
            var total = GameUnifier.Current?.GetGroup(entry.Id)?.TotalPlaytime ?? entry.Playtime;
            if (total > 0)
            {
                details.Add(playtimeConverter.Convert(total, typeof(string), null, null)?.ToString());
            }

            details.Add(string.Join(" · ", entry.StoreBadges.Select(a => a.StoreName)));
            return new HomeFeaturedGame
            {
                Game = entry,
                Label = ResourceProvider.GetString(labelKey),
                Details = string.Join("   ·   ", details.Where(a => !a.IsNullOrEmpty())),
                ActionText = ResourceProvider.GetString(entry.IsInstalled ? "LOCPlayGame" : "LOCInstallGame")
            };
        }

        public void Refresh()
        {
            if (main.GamesView?.Items == null)
            {
                return;
            }

            var unifier = GameUnifier.Current;
            var culture = Localization.ApplicationLanguageCultureInfo;
            var seen = new HashSet<Guid>();
            var entries = main.GamesView.Items.
                Where(a => !a.Hidden && unifier?.IsSecondaryCopy(a.Id) != true && seen.Add(a.Id)).
                ToList();

            var recentGames = entries.Where(a => a.LastActivity != null).OrderByDescending(a => a.LastActivity).ToList();
            var installed = entries.Where(a => a.IsInstalled).
                OrderByDescending(a => a.LastActivity ?? DateTime.MinValue).
                ThenBy(a => a.Name).ToList();

            IsEmpty = entries.Count == 0;
            Greeting = ResourceProvider.GetString(GetGreetingKey());
            Summary = string.Format(
                ResourceProvider.GetString("LOCAtrioHomeSummary"),
                entries.Count.ToString("N0", culture),
                installed.Count.ToString("N0", culture),
                main.Extensions.LibraryPlugins.Count);

            var currentHero = Hero?.Game.Id;
            var featuredGames = recentGames.Where(a => !a.BackgroundImage.IsNullOrEmpty()).Take(FeaturedSize).
                Select(a => CreateFeatured(a, "LOCAtrioHeroContinue")).ToList();
            foreach (var entry in installed.Where(a => !a.BackgroundImage.IsNullOrEmpty()))
            {
                if (featuredGames.Count >= FeaturedSize)
                {
                    break;
                }

                if (featuredGames.All(a => a.Game.Id != entry.Id))
                {
                    featuredGames.Add(CreateFeatured(entry, "LOCAtrioHeroInstalled"));
                }
            }

            foreach (var entry in entries.Where(a => !a.BackgroundImage.IsNullOrEmpty()).OrderByDescending(a => a.Added ?? DateTime.MinValue))
            {
                if (featuredGames.Count >= FeaturedSize)
                {
                    break;
                }

                if (featuredGames.All(a => a.Game.Id != entry.Id))
                {
                    featuredGames.Add(CreateFeatured(entry, "LOCAtrioHeroLibrary"));
                }
            }

            Featured = featuredGames;
            Hero = featuredGames.FirstOrDefault(a => a.Game.Id == currentHero) ?? featuredGames.FirstOrDefault();

            Recent = recentGames.Where(a => !a.BackgroundImage.IsNullOrEmpty() || !a.CoverImage.IsNullOrEmpty()).Take(10).ToList();
            Ready = CreateShelf("LOCAtrioHomeInstalled", installed);
            Ranked = entries.Where(a => a.Playtime > 0).
                OrderByDescending(a => a.Playtime).
                Take(RankedSize).
                Select((a, index) => new HomeRankedGame { Rank = index + 1, Game = a }).
                ToList();

            var list = new List<HomeShelf>();
            AddShelf(list, "LOCAtrioHomeFavorites", entries.Where(a => a.Favorite).OrderBy(a => a.Name));
            AddShelf(list, "LOCAtrioHomeMultiStore", entries.Where(a => a.HasMultipleCopies).OrderByDescending(a => a.IsInstalled).ThenByDescending(a => a.Added ?? DateTime.MinValue));
            AddShelf(list, "LOCAtrioHomeAdded", entries.Where(a => a.Added != null).OrderByDescending(a => a.Added));
            Shelves = list;

            Stores = GetStoreTiles(culture);
        }

        private List<HomeStoreTile> GetStoreTiles(System.Globalization.CultureInfo culture)
        {
            var counts = new Dictionary<Guid, int>();
            foreach (var game in main.Database.Games)
            {
                if (!game.Hidden)
                {
                    counts.TryGetValue(game.PluginId, out var count);
                    counts[game.PluginId] = count + 1;
                }
            }

            var tiles = new List<HomeStoreTile>();
            foreach (LibraryPlugin plugin in main.Extensions.LibraryPlugins.
                Where(a => counts.ContainsKey(a.Id)).
                OrderBy(a => GameUnifier.Current.GetStoreRank(a.Id, a.Name)))
            {
                tiles.Add(new HomeStoreTile
                {
                    PluginId = plugin.Id,
                    Name = plugin.Name,
                    Icon = plugin.LibraryIcon == null ? null : ImageSourceManager.GetImage(plugin.LibraryIcon, true),
                    CountText = string.Format(ResourceProvider.GetString("LOCAtrioGamesCount"), counts[plugin.Id].ToString("N0", culture)),
                    CardBrush = StoreBranding.GetCardBrush(plugin.Name),
                    ColorBrush = StoreBranding.GetBrush(plugin.Name)
                });
            }

            return tiles;
        }

        private static HomeShelf CreateShelf(string titleKey, IEnumerable<GamesCollectionViewEntry> items)
        {
            var games = items.Take(ShelfSize).ToList();
            return games.Count == 0 ? null : new HomeShelf { Title = ResourceProvider.GetString(titleKey), Items = games };
        }

        private static void AddShelf(List<HomeShelf> list, string titleKey, IEnumerable<GamesCollectionViewEntry> items)
        {
            var shelf = CreateShelf(titleKey, items);
            if (shelf != null)
            {
                list.Add(shelf);
            }
        }
    }
}
