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
    /// <summary>A game the store of Epic gives away, as the start shows it.</summary>
    public class FreeGameCard
    {
        public string Title { get; set; }
        public string ImageUrl { get; set; }
        public string Url { get; set; }

        /// <summary>"Free until 12 Nov" or "Free from 12 to 19 Nov".</summary>
        public string WhenText { get; set; }
        public bool IsNow { get; set; }

        /// <summary>The library already has a game by that name, from any store.</summary>
        public bool InLibrary { get; set; }
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

        public RelayCommand<RareCard> OpenRareCommand => new RelayCommand<RareCard>(a => OpenGame(a?.Entry));

        public static string GetFreeWhen(FreeGame game, CultureInfo culture)
        {
            var end = game.End.ToLocalTime();
            if (game.IsNow)
            {
                return string.Format(ResourceProvider.GetString("LOCAtrioFreeUntil"), end.ToString("d MMM", culture), end.ToString("HH:mm", culture));
            }

            return string.Format(ResourceProvider.GetString("LOCAtrioFreeFrom"), game.Start.ToLocalTime().ToString("d MMM", culture), end.ToString("d MMM", culture));
        }

        private List<FreeGameCard> ToCards(List<FreeGame> games)
        {
            var culture = Localization.ApplicationLanguageCultureInfo;
            var owned = new HashSet<string>(entries.SelectMany(a => a.Copies.Select(b => b.Game.Name)).Where(a => !a.IsNullOrEmpty()).
                Select(a => GameCaptures.Normalize(a.Replace("&", " and ")).Replace(" ", string.Empty)));
            return games.Select(a => new FreeGameCard
            {
                Title = a.Title,
                ImageUrl = a.ImageUrl,
                Url = a.Url,
                IsNow = a.IsNow,
                WhenText = GetFreeWhen(a, culture),
                InLibrary = owned.Contains(GameCaptures.Normalize(a.Title.Replace("&", " and ")).Replace(" ", string.Empty))
            }).ToList();
        }

        /// <summary>What was saved shows at once; the store is asked again after a few hours.</summary>
        private async void LoadFreeGames()
        {
            if (GameUnifier.Current?.Settings.ShowFreeGames == false || AtrioInfo.GetTestSetting("ATRIO_SIN_RED") != null)
            {
                FreeGames = new List<FreeGameCard>();
                return;
            }

            FreeGames = ToCards(Playnite.Atrio.FreeGames.GetSaved(DateTime.UtcNow));
            try
            {
                var games = await Playnite.Atrio.FreeGames.GetAsync(CancellationToken.None);
                if (GameUnifier.Current?.Settings.ShowFreeGames != false)
                {
                    FreeGames = ToCards(games);
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                LogManager.GetLogger().Debug(e, "Failed to read the free games of Epic.");
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
