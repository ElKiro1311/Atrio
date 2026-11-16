using Playnite.Atrio;
using Playnite.Commands;
using Playnite.Common;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.DesktopApp.ViewModels
{
    /// <summary>A game that costs money and is free now, or soon, as the start shows it.</summary>
    public class FreeGameCard
    {
        public string Title { get; set; }
        public string ImageUrl { get; set; }
        public string Url { get; set; }

        /// <summary>"Free until 12 Nov" or "Free from 12 to 19 Nov".</summary>
        public string WhenText { get; set; }
        public bool IsNow { get; set; }

        /// <summary>It's played for nothing for a few days, but it isn't kept.</summary>
        public bool IsTrial { get; set; }

        /// <summary>"To keep", "Free to try" or "Coming".</summary>
        public string KindText { get; set; }

        /// <summary>What it says that kind means, for the tooltip.</summary>
        public string KindHint { get; set; }

        /// <summary>What it costs when it isn't free. A gift shows it crossed out; a game to try, as its price.</summary>
        public string PriceText { get; set; }
        public string StoreName { get; set; }
        public object StoreIcon { get; set; }

        /// <summary>The library already has it: that copy, or a game by that name from any store.</summary>
        public bool InLibrary { get; set; }
    }

    /// <summary>A headline of GG.deals about games given away.</summary>
    public class FreeNewsCard
    {
        public string Title { get; set; }
        public string Url { get; set; }
        public string ImageUrl { get; set; }

        /// <summary>"Today", "Yesterday", "3 days ago".</summary>
        public string WhenText { get; set; }

        /// <summary>"Gift", "Free to try" or "Summary".</summary>
        public string KindText { get; set; }
    }

    /// <summary>One of the rarest achievements of the person.</summary>
    public class RareCard
    {
        public GamesCollectionViewEntry Entry { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Icon { get; set; }
        public string GameName { get; set; }
        public string PercentText { get; set; }
    }

    public partial class HomeViewModel
    {
        public const int RareCount = 8;

        private List<FreeGameCard> freeGames = new List<FreeGameCard>();
        public List<FreeGameCard> FreeGames
        {
            get => freeGames;
            set => SetValue(ref freeGames, value);
        }

        private List<FreeNewsCard> freeNews = new List<FreeNewsCard>();
        public List<FreeNewsCard> FreeNews
        {
            get => freeNews;
            set => SetValue(ref freeNews, value);
        }

        private List<RareCard> rare = new List<RareCard>();
        public List<RareCard> Rare
        {
            get => rare;
            set => SetValue(ref rare, value);
        }

        public RelayCommand<FreeGameCard> OpenFreeGameCommand => new RelayCommand<FreeGameCard>(a =>
        {
            if (a?.Url.IsNullOrEmpty() == false)
            {
                GlobalCommands.NavigateUrl(a.Url);
            }
        });

        public RelayCommand<FreeNewsCard> OpenFreeNewsCommand => new RelayCommand<FreeNewsCard>(a =>
        {
            if (a?.Url.IsNullOrEmpty() == false)
            {
                GlobalCommands.NavigateUrl(a.Url);
            }
        });

        /// <summary>The pages where the person can check it all: the free promotions of SteamDB, the gifts of GG.deals.</summary>
        public RelayCommand OpenSteamDbFreeCommand => new RelayCommand(() => GlobalCommands.NavigateUrl(Playnite.Atrio.FreeGames.SteamDbPageUrl));

        public RelayCommand OpenGgDealsFreeCommand => new RelayCommand(() => GlobalCommands.NavigateUrl(Playnite.Atrio.FreeGames.NewsPageUrl));

        public RelayCommand<RareCard> OpenRareCommand => new RelayCommand<RareCard>(a => OpenGame(a?.Entry));

        public static string GetFreeWhen(FreeGame game, CultureInfo culture)
        {
            var trial = game.Kind == FreeGameKind.Trial;
            if (game.End == null)
            {
                return ResourceProvider.GetString("LOCAtrioFreeNow");
            }

            var end = game.End.Value.ToLocalTime();
            if (game.IsNow)
            {
                return string.Format(ResourceProvider.GetString(trial ? "LOCAtrioFreePlayUntil" : "LOCAtrioFreeUntil"),
                    end.ToString("d MMM", culture), end.ToString("HH:mm", culture));
            }

            return string.Format(ResourceProvider.GetString(trial ? "LOCAtrioFreePlayFrom" : "LOCAtrioFreeFrom"),
                game.Start.ToLocalTime().ToString("d MMM", culture), end.ToString("d MMM", culture));
        }

        /// <summary>"Today", "Yesterday" or "3 days ago", by the days of the calendar of the computer.</summary>
        public static string GetNewsWhen(DateTime publishedUtc, DateTime nowUtc)
        {
            var days = (nowUtc.ToLocalTime().Date - publishedUtc.ToLocalTime().Date).Days;
            if (days <= 0)
            {
                return ResourceProvider.GetString("LOCToday");
            }

            return days == 1 ? ResourceProvider.GetString("LOCYesterday") : string.Format(ResourceProvider.GetString("LOCAtrioFreeNewsDaysAgo"), days);
        }

        private static readonly Dictionary<string, Tuple<Guid, string>> freeStores = new Dictionary<string, Tuple<Guid, string>>
        {
            { FreeGame.Epic, Tuple.Create(Guid.Parse("00000002-DBD1-46C6-B5D0-B1BA559D10E4"), "Epic Games") },
            { FreeGame.Steam, Tuple.Create(SteamContent.SteamPluginId, "Steam") },
            { FreeGame.Gog, Tuple.Create(PcGamingWiki.GogPluginId, "GOG") }
        };

        private static string PlainName(string name) => GameCaptures.Normalize((name ?? string.Empty).Replace("&", " and ")).Replace(" ", string.Empty);

        private List<FreeGameCard> ToCards(List<FreeGame> games)
        {
            var culture = Localization.ApplicationLanguageCultureInfo;
            var copies = entries.SelectMany(a => a.Copies.Select(b => b.Game)).ToList();
            var owned = new HashSet<string>(copies.Select(a => PlainName(a.Name)).Where(a => !a.IsNullOrEmpty()));
            var ownedIds = new HashSet<string>(copies.Where(a => a.PluginId != Guid.Empty && !a.GameId.IsNullOrEmpty()).Select(a => a.PluginId + ":" + a.GameId));
            var noBrands = AtrioInfo.GetTestSetting("ATRIO_SIN_MARCAS") != null;
            var cards = new List<FreeGameCard>();
            foreach (var game in games)
            {
                var store = freeStores.TryGetValue(game.Store ?? FreeGame.Epic, out var known) ? known : freeStores[FreeGame.Epic];
                var plugin = main.Extensions?.LibraryPlugins?.FirstOrDefault(a => a.Id == store.Item1);
                var trial = game.Kind == FreeGameKind.Trial;
                var card = new FreeGameCard
                {
                    Title = game.Title,
                    ImageUrl = game.ImageUrl,
                    Url = game.Url,
                    IsNow = game.IsNow,
                    IsTrial = trial,
                    WhenText = GetFreeWhen(game, culture),
                    KindText = ResourceProvider.GetString(!game.IsNow ? "LOCAtrioFreeSoon" : trial ? "LOCAtrioFreeTrial" : "LOCAtrioFreeKeep"),
                    KindHint = ResourceProvider.GetString(trial ? "LOCAtrioFreeTrialHint" : "LOCAtrioFreeKeepHint"),
                    PriceText = game.PriceText.IsNullOrWhiteSpace() ? null :
                        trial ? string.Format(ResourceProvider.GetString("LOCAtrioFreeTrialPrice"), game.PriceText) : game.PriceText,
                    StoreName = store.Item2,
                    StoreIcon = noBrands ? EmulatorStores.GetIcon(store.Item2) :
                        plugin?.LibraryIcon == null ? null : ImageSourceManager.GetImage(plugin.LibraryIcon, true),
                    InLibrary = (!game.StoreId.IsNullOrEmpty() && ownedIds.Contains(store.Item1 + ":" + game.StoreId)) || owned.Contains(PlainName(game.Title))
                };

                // To try a game that's already in the library is of no use.
                if (trial && card.InLibrary)
                {
                    continue;
                }

                cards.Add(card);
            }

            // What the person already has goes after the rest of its group.
            return cards.Select((a, index) => new { a, index }).
                OrderByDescending(a => a.a.IsNow).
                ThenBy(a => a.a.IsTrial).
                ThenBy(a => a.a.InLibrary).
                ThenBy(a => a.index).
                Select(a => a.a).
                ToList();
        }

        private List<FreeNewsCard> ToNewsCards(List<FreeGameNews> news)
        {
            var now = DateTime.UtcNow;
            return news.Select(a => new FreeNewsCard
            {
                Title = a.Title,
                Url = a.Url,
                ImageUrl = a.ImageUrl,
                WhenText = GetNewsWhen(a.Published, now) + " · GG.deals",
                KindText = ResourceProvider.GetString(a.Kind == FreeGameKind.Keep ? "LOCAtrioFreeNewsGift" : a.Kind == FreeGameKind.Trial ? "LOCAtrioFreeTrial" : "LOCAtrioFreeNewsSummary")
            }).ToList();
        }

        /// <summary>What the person chose to see, with their list of wishes of Steam for the free weekends.</summary>
        private static FreeGamesOptions GetFreeOptions()
        {
            var settings = GameUnifier.Current?.Settings;
            var options = new FreeGamesOptions
            {
                Trials = settings?.FreeGameTrials != false,
                News = settings?.FreeGameNews != false
            };

            if (options.Trials)
            {
                try
                {
                    options.SteamWishes = Wishlist.GetItems().Where(a => a.Store == WishlistItem.Steam).Select(a => a.StoreId).ToList();
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    LogManager.GetLogger().Debug(e, "Failed to read the list of wishes for the free games.");
                }
            }

            return options;
        }

        private void ShowFree(FreeGamesList list)
        {
            FreeGames = ToCards(list.Games);
            FreeNews = ToNewsCards(list.News);
        }

        /// <summary>Each load counts: an older one that ends later doesn't put back what the settings took off since.</summary>
        private int freeLoad;

        /// <summary>What was saved shows at once; the stores are asked again after a few hours.</summary>
        private async void LoadFreeGames()
        {
            var load = ++freeLoad;
            if (GameUnifier.Current?.Settings.ShowFreeGames == false || AtrioInfo.GetTestSetting("ATRIO_SIN_RED") != null)
            {
                FreeGames = new List<FreeGameCard>();
                FreeNews = new List<FreeNewsCard>();
                return;
            }

            var options = GetFreeOptions();
            ShowFree(Playnite.Atrio.FreeGames.GetSaved(DateTime.UtcNow, options));
            try
            {
                var list = await Playnite.Atrio.FreeGames.GetAsync(options, CancellationToken.None);
                if (load == freeLoad && GameUnifier.Current?.Settings.ShowFreeGames != false)
                {
                    ShowFree(list);
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                LogManager.GetLogger().Debug(e, "Failed to read the free games.");
            }
        }

        private List<RareAchievement> rareFound;
        private DateTime rareTime;

        /// <summary>Read again at most every ten minutes: it opens the achievements of every game with something unlocked.</summary>
        private async void LoadRareAchievements()
        {
            if (rareFound == null || DateTime.Now - rareTime > TimeSpan.FromMinutes(10))
            {
                rareTime = DateTime.Now;
                rareFound = await RareAchievements.GetAsync(main.Extensions, RareCount);
            }

            var found = rareFound;
            var byId = new Dictionary<Guid, GamesCollectionViewEntry>();
            foreach (var entry in entries)
            {
                foreach (var copy in entry.Copies)
                {
                    byId[copy.Game.Id] = entry;
                }
            }

            var culture = Localization.ApplicationLanguageCultureInfo;
            Rare = found.Where(a => byId.ContainsKey(a.GameId)).Select(a => new RareCard
            {
                Entry = byId[a.GameId],
                Name = a.Name,
                Description = a.Description,
                Icon = a.Icon,
                GameName = byId[a.GameId].DisplayName,
                PercentText = string.Format(ResourceProvider.GetString("LOCAtrioRarePercent"), a.Percent.ToString(a.Percent < 1 ? "0.0#" : "0.#", culture))
            }).ToList();
        }
    }
}
