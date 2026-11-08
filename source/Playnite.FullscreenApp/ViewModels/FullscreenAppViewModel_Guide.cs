using Playnite.Atrio;
using Playnite.Commands;
using Playnite.SDK;
using Playnite.SDK.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

namespace Playnite.FullscreenApp.ViewModels
{
    /// <summary>
    /// Going between a game and the mode for the couch with the controller: Guide (when it's on) or holding
    /// View + Menu bring Atrio to the front with the status of the open game, and from there the same buttons
    /// or "Back to the game" return to it. Games opened outside Atrio count too.
    /// </summary>
    public partial class FullscreenAppViewModel
    {
        private readonly Dictionary<object, GuideCombo> guideCombos = new Dictionary<object, GuideCombo>();
        private DispatcherTimer guideComboTimer;

        /// <summary>The window that was in front when Atrio was called from a game, to go back to it.</summary>
        private IntPtr returnWindow;

        public RelayCommand ReturnToGameCommand { get; private set; }
        public RelayCommand CloseOpenGameCommand { get; private set; }

        private void InitializeGuideCommands()
        {
            ReturnToGameCommand = new RelayCommand(() => ReturnToGame());
            CloseOpenGameCommand = new RelayCommand(() => CloseOpenGame());
            OpenGames.Changed += OpenGames_Changed;
        }

        /// <summary>Guide and the hold of View + Menu. It gets every button, also while a game is in front.</summary>
        private void ProcessGuideInput(OnControllerButtonStateChangedArgs e)
        {
            if (!AppSettings.Fullscreen.EnableGameControllerSupport)
            {
                return;
            }

            if (e.Button == ControllerInput.Guide)
            {
                if (e.State == ControllerInputState.Released && AppSettings.Fullscreen.GuideButtonFocus &&
                    (!AppSettings.Fullscreen.GuideButtonFocusOnlyWhenNoGameRunning || GetOpenGameId() == null))
                {
                    SwitchWithGame();
                }

                return;
            }

            if ((e.Button == ControllerInput.Back || e.Button == ControllerInput.Start) &&
                GameUnifier.Current?.Settings.GuideComboReturns == true)
            {
                var key = (object)e.Controller ?? this;
                if (!guideCombos.TryGetValue(key, out var combo))
                {
                    guideCombos[key] = combo = new GuideCombo();
                }

                combo.Update(e.Button, e.State == ControllerInputState.Pressed, DateTime.Now);
                if (combo.IsHolding)
                {
                    StartGuideComboTimer();
                }
            }
        }

        private void StartGuideComboTimer()
        {
            if (guideComboTimer == null)
            {
                guideComboTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                guideComboTimer.Tick += (_, __) =>
                {
                    var now = DateTime.Now;
                    var fire = false;
                    foreach (var combo in guideCombos.Values)
                    {
                        fire |= combo.ShouldFire(now);
                    }

                    if (!guideCombos.Values.Any(a => a.IsHolding))
                    {
                        guideComboTimer.Stop();
                    }

                    if (fire)
                    {
                        SwitchWithGame();
                    }
                };
            }

            guideComboTimer.Start();
        }

        /// <summary>
        /// From a game it brings Atrio to the front with the status of the game; from Atrio, with a game open,
        /// it goes back to the game. With nothing open it only brings Atrio to the front.
        /// </summary>
        public void SwitchWithGame()
        {
            if (App.IsActive)
            {
                if (GetOpenGameId() != null)
                {
                    ReturnToGame();
                }

                return;
            }

            var window = OpenGames.GetOthersForegroundWindow();
            var gameId = OpenGames.FindGameOfWindow(window) ?? GetOpenGameId();
            returnWindow = gameId == null ? IntPtr.Zero : window;
            if (gameId != null)
            {
                ShowOpenGameStatus(gameId.Value);
            }

            RestoreWindow();
        }

        /// <summary>The game Atrio started, or else any game with something running from its folder.</summary>
        private Guid? GetOpenGameId()
        {
            if (GameStatusView?.Game != null && GameStatusView.Game.IsOpen)
            {
                return GameStatusView.Game.Id;
            }

            var started = GamesEditor.RunningGames.Keys.FirstOrDefault();
            if (started != Guid.Empty)
            {
                return started;
            }

            var detected = OpenGames.GetOpenGames();
            return detected.Count > 0 ? detected[0] : (Guid?)null;
        }

        private GamesCollectionViewEntry GetEntry(Guid gameId)
        {
            var group = GameUnifier.Current?.GetGroup(gameId);
            var ids = new HashSet<Guid>(group?.Copies?.Select(a => a.Id) ?? new[] { gameId });
            var entry = GamesView?.Items?.FirstOrDefault(a => ids.Contains(a.Id));
            if (entry != null)
            {
                return entry;
            }

            var game = Database.Games.Get(gameId);
            return game == null ? null : new GamesCollectionViewEntry(game, GamesView.GetLibraryPlugin(game), AppSettings);
        }

        internal void ShowOpenGameStatus(Guid gameId)
        {
            var entry = GetEntry(gameId);
            if (entry == null)
            {
                return;
            }

            if (GameStatusView?.Game?.Id != entry.Id)
            {
                GameStatusView = new GameStatusViewModel(entry);
            }

            GameStatusView.GameStatusText = ResourceProvider.GetString(LOC.GameIsRunning).Format(entry.DisplayName);
            GameStatusView.CanReturn = true;
            GameStatusView.ReturnHint = GetReturnHint();
            GameStatusVisible = true;
        }

        /// <summary>The way back to Atrio with the buttons that are on, or nothing if none is.</summary>
        internal string GetReturnHint()
        {
            var guide = AppSettings.Fullscreen.EnableGameControllerSupport && AppSettings.Fullscreen.GuideButtonFocus &&
                !AppSettings.Fullscreen.GuideButtonFocusOnlyWhenNoGameRunning;
            var combo = AppSettings.Fullscreen.EnableGameControllerSupport && GameUnifier.Current?.Settings.GuideComboReturns == true;
            if (guide && combo)
            {
                return ResourceProvider.GetString("LOCAtrioReturnHintBoth");
            }

            if (guide)
            {
                return ResourceProvider.GetString("LOCAtrioReturnHintGuide");
            }

            return combo ? ResourceProvider.GetString("LOCAtrioReturnHintCombo") : null;
        }

        /// <summary>Puts the game in front again and gets out of its way.</summary>
        public void ReturnToGame()
        {
            var gameId = GetOpenGameId();
            var shown = OpenGames.BringToFront(returnWindow) ||
                (gameId != null && OpenGames.ShowGame(gameId.Value));

            // Without a window to show, getting out of the way leaves in front what was behind: the game.
            if (!shown || AppSettings.Fullscreen.MinimizeAfterGameStartup)
            {
                MinimizeWindow();
            }
        }

        /// <summary>"Back to the game" of the page of an open game: that game, whichever was in front before.</summary>
        public void ReturnToGame(Guid gameId)
        {
            var shown = OpenGames.ShowGame(gameId);
            if (!shown || AppSettings.Fullscreen.MinimizeAfterGameStartup)
            {
                MinimizeWindow();
            }
        }

        private async void CloseOpenGame()
        {
            var gameId = GetOpenGameId();
            var entry = gameId == null ? null : GetEntry(gameId.Value);
            var copy = entry == null ? null : OpenGames.GetOpenCopy(entry.Copies.Select(a => a.Game));
            if (copy == null)
            {
                return;
            }

            await OpenGames.StopAsync(copy, entry.DisplayName, Dialogs);
        }

        private void OpenGames_Changed(object sender, OpenGamesChangedEventArgs e)
        {
            // A game opened outside Atrio was closed: its status goes away, as with the ones Atrio starts.
            var shown = GameStatusView?.Game;
            if (shown == null || shown.IsOpen || GamesEditor.RunningGames.ContainsKey(shown.Id) ||
                !shown.Copies.Any(a => e.Closed.Contains(a.Game.Id)))
            {
                return;
            }

            returnWindow = IntPtr.Zero;
            if (GameStatusVisible)
            {
                GameStatusVisible = false;
                if (GameDetailsVisible)
                {
                    GameDetailsFocused = true;
                }
                else
                {
                    GameListFocused = true;
                }
            }

            GameStatusView = null;
        }
    }
}
