using Playnite.Atrio;
using Playnite.Commands;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.DesktopApp.ViewModels
{
    /// <summary>One card as it shows: in color when the person has it, and how many.</summary>
    public class SteamCardTile
    {
        public SteamCard Card { get; set; }
        public string Name => Card.Name;
        public string ImageUrl => Card.ImageUrl;
        public bool IsOwned => Card.IsOwned;
        public string CountText { get; set; }
        public string ToolTip { get; set; }
    }

    /// <summary>The trading cards and the badge of a game of Steam, in its page.</summary>
    public class SteamCardsPanel : ObservableObject
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        public const int Shown = 6;

        private readonly string appId;
        private readonly string gameName;
        private readonly IResourceProvider resources;
        private readonly CancellationToken token;

        public SteamCardsResult Result { get; private set; } = new SteamCardsResult();
        public SteamBadge Badge => Result.State == SteamCardsState.Ready ? Result.Badge : null;

        public bool IsVisible => HasCards || IsPrivate;
        public bool HasCards => Badge != null;

        // The account may still say it: signing in is only proposed when there's nothing left to ask.
        private bool accountPending = SteamFriends.KnownSession != false;

        /// <summary>The game has cards and the profile doesn't show which ones the person has.</summary>
        public bool IsPrivate => Result.State == SteamCardsState.Private && !accountPending;

        /// <summary>The other set: the one of the foil cards.</summary>
        public bool IsFoil { get; private set; }

        /// <summary>Why it has nothing to show, in the window of the cards.</summary>
        public string EmptyText { get; private set; }

        public List<SteamCardTile> Tiles { get; private set; } = new List<SteamCardTile>();
        public List<SteamCardTile> FirstTiles => Tiles.Take(Shown).ToList();
        public string IconUrl => Badge?.IconUrl;
        public bool HasIcon => !IconUrl.IsNullOrEmpty();
        public string BadgeTitle { get; private set; }
        public string BadgeText { get; private set; }
        public string NextText { get; private set; }
        public string CardsText { get; private set; }
        public double Progress { get; private set; }
        public string DropsText { get; private set; }
        public bool CanCraft => Badge?.CanCraft == true;
        public string PageUrl => $"https://steamcommunity.com/my/gamecards/{appId}/" + (IsFoil ? "?border=1" : string.Empty);
        public string MarketUrl => $"https://steamcommunity.com/market/search?appid=753&category_753_Game%5B%5D=tag_app_{appId}&category_753_item_class%5B%5D=tag_item_class_2" +
            $"&category_753_cardborder%5B%5D=tag_cardborder_{(IsFoil ? 1 : 0)}";

        public RelayCommand<object> ShowAllCommand { get; }
        public RelayCommand<object> OpenPageCommand { get; }
        public RelayCommand<object> SignInCommand { get; }

        public SteamCardsPanel(string appId, string gameName, IResourceProvider resources, CancellationToken token, bool foil = false)
        {
            IsFoil = foil;
            this.appId = appId;
            this.gameName = gameName;
            this.resources = resources;
            this.token = token;
            ShowAllCommand = new RelayCommand<object>(a => Windows.SteamCardsWindow.Open(gameName, this, a as SteamCardTile), _ => HasCards);
            OpenPageCommand = new RelayCommand<object>(_ => GlobalCommands.NavigateUrl(PageUrl));
            SignInCommand = new RelayCommand<object>(_ => DesktopApplication.Current?.MainModel?.OpenInBrowser("https://steamcommunity.com/login/home/"));
            var known = foil ? null : SteamCards.GetFromMemory(appId);
            if (known != null)
            {
                Show(known);
            }
        }

        /// <summary>What is on the disk right away, Steam after the page is already showing, and the account after that.</summary>
        public async Task LoadAsync(int pause)
        {
            try
            {
                accountPending = SteamFriends.KnownSession != false;
                if (!IsVisible)
                {
                    Show(await Task.Run(() => SteamCards.GetKnown(appId), token));
                }

                await Task.Delay(pause, token);
                var language = StoreActivity.GetSteamLanguage(StoreActivity.Language);
                var result = await Task.Run(() => SteamCards.GetAsync(appId, language, token), token);
                token.ThrowIfCancellationRequested();
                if (result.State != SteamCardsState.Failed || !IsVisible)
                {
                    Show(result);
                }

                if (result.State != SteamCardsState.None)
                {
                    await LoadAccountAsync();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Debug(e, $"Failed to load the cards of {appId}.");
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    accountPending = false;
                    OnPropertyChanged(nameof(IsPrivate));
                    OnPropertyChanged(nameof(IsVisible));
                }
            }
        }

        /// <summary>The account only answers with a session in the browser of Atrio; without one nothing is asked.</summary>
        private async Task LoadAccountAsync()
        {
            // Who goes quickly through the list doesn't open a page for each game.
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            if (!await SteamFriends.HasSessionAsync(token))
            {
                return;
            }

            var result = await Task.Run(() => SteamCards.GetFromAccountAsync(appId, SteamFriends.LoadPage, token), token);
            token.ThrowIfCancellationRequested();
            if (result != null)
            {
                Show(result);
            }
        }

        /// <summary>After signing in the cards may be others, or show for the first time.</summary>
        public async void Reload()
        {
            await Task.Run(() => SteamCards.Forget(appId));
            await LoadAsync(1000);
        }

        private int reloads;

        /// <summary>After playing there may be another card. Steam takes a moment, and a game says several times that it was played.</summary>
        public async void ReloadLater()
        {
            var own = ++reloads;
            if (!IsVisible)
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), token);
                if (own == reloads)
                {
                    await Task.Run(() => SteamCards.Forget(appId), token);
                    await LoadAsync(0);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, $"Failed to reload the cards of {appId}.");
            }
        }

        private SteamCardsPanel foilPanel;
        private Task foilLoading;

        /// <summary>The foil cards: empty until they are asked for.</summary>
        public SteamCardsPanel Foil => foilPanel = foilPanel ?? new SteamCardsPanel(appId, gameName, resources, token, true);

        /// <summary>Asked the first time that someone wants to see them, and again if they couldn't be had.</summary>
        public Task LoadFoilAsync()
        {
            if (!Foil.HasCards && foilLoading?.IsCompleted != false)
            {
                foilLoading = AskFoilAsync();
            }

            return foilLoading ?? Task.CompletedTask;
        }

        private async Task AskFoilAsync()
        {
            var language = StoreActivity.GetSteamLanguage(StoreActivity.Language);
            Foil.EmptyText = resources.GetString("LOCAtrioActivityLoading");
            Foil.Show(Foil.Result);
            SteamCardsResult result;
            try
            {
                result = await Task.Run(() => SteamCards.GetFoilAsync(appId, language, CancellationToken.None));
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Debug(e, $"Failed to load the foil cards of {appId}.");
                result = new SteamCardsResult { State = SteamCardsState.Failed };
            }

            Foil.EmptyText = result.State == SteamCardsState.Ready ? null :
                resources.GetString(result.State == SteamCardsState.Private ? "LOCAtrioCardsFoilPrivate" : result.State == SteamCardsState.None ? "LOCAtrioCardsFoilNone" : "LOCAtrioCardsFoilFailed");
            Foil.Show(result);
        }

        public void Show(SteamCardsResult result)
        {
            Result = result ?? new SteamCardsResult();
            var badge = Badge;
            Tiles = badge == null ? new List<SteamCardTile>() : badge.Cards.
                Select(a => new SteamCardTile
                {
                    Card = a,
                    CountText = a.Owned > 1 ? $"×{a.Owned}" : null,
                    ToolTip = a.Owned > 0 ? string.Format(resources.GetString(a.Owned == 1 ? "LOCAtrioCardOwnedOne" : "LOCAtrioCardOwned"), a.Name, a.Owned) : string.Format(resources.GetString("LOCAtrioCardMissing"), a.Name)
                }).
                ToList();
            if (badge != null)
            {
                BadgeTitle = badge.Level > 0 ? badge.Name : resources.GetString("LOCAtrioBadgeNone");
                BadgeText = badge.Level > 0 ? string.Format(resources.GetString("LOCAtrioBadgeLevel"), badge.Level, badge.Xp) : null;
                NextText = badge.IsMaxed ? resources.GetString("LOCAtrioBadgeMaxed") :
                    badge.NextLevelName.IsNullOrEmpty() ? null :
                    string.Format(resources.GetString("LOCAtrioBadgeNext"), badge.NextLevelName, badge.NextLevelXp > 0 ? badge.NextLevelXp : (badge.Level + 1) * 100);
                CardsText = string.Format(resources.GetString("LOCAtrioCardsCount"), badge.OwnedKinds, badge.Cards.Count);
                Progress = badge.Cards.Count == 0 ? 0 : 100.0 * badge.OwnedKinds / badge.Cards.Count;
                DropsText = badge.DropsLeft == null ? null :
                    badge.DropsLeft == 0 ? resources.GetString("LOCAtrioCardsNoDrops") :
                    string.Format(resources.GetString(badge.DropsLeft == 1 ? "LOCAtrioCardsDropsOne" : "LOCAtrioCardsDrops"), badge.DropsLeft);
            }

            foreach (var name in new[]
            {
                nameof(IsVisible), nameof(HasCards), nameof(IsPrivate), nameof(Tiles), nameof(FirstTiles), nameof(IconUrl), nameof(HasIcon), nameof(BadgeTitle),
                nameof(BadgeText), nameof(NextText), nameof(CardsText), nameof(Progress), nameof(DropsText), nameof(CanCraft), nameof(Badge), nameof(EmptyText)
            })
            {
                OnPropertyChanged(name);
            }

            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }
}
