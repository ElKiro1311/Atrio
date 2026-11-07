using Playnite.Atrio;
using Playnite.Commands;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Playnite.FullscreenApp.ViewModels
{
    public class GameDetailsViewModel : ObservableObject, IDisposable
    {
        private readonly IResourceProvider resources;
        private readonly GamesEditor gamesEditor;
        private readonly FullscreenAppViewModel mainModel;
        private IDialogsFactory dialogs;
        private readonly List<Game> copies = new List<Game>();

        // A game in several stores is installed through one of its copies.
        private Game InstallingCopy => copies.FirstOrDefault(a => a.IsInstalling);

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

        public string ContextActionDescription
        {
            get
            {
                if (Game?.IsRunning == true)
                {
                    return resources.GetString("LOCGameRunning");
                }
                else if (Game?.IsLaunching == true)
                {
                    return resources.GetString("LOCGameLaunching");
                }
                else if (InstallingCopy != null)
                {
                    var percent = SteamInstallProgress.Get(InstallingCopy.Id);
                    var installing = resources.GetString("LOCSetupRunning");
                    return percent > 0 ? $"{installing} {percent} %" : installing;
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

        #region Game Commands

        public RelayCommand<object> ContextActionCommand { get; private set; }
        #endregion

        public GameDetailsViewModel(
            GamesCollectionViewEntry gameView)
        {
            Game = gameView;
            resources = new ResourceProvider();
        }

        public GameDetailsViewModel(
            GamesCollectionViewEntry gameView,
            IResourceProvider resources,
            GamesEditor gamesEditor,
            FullscreenAppViewModel mainModel,
            IDialogsFactory dialogs)
        {
            this.resources = resources;
            this.gamesEditor = gamesEditor;
            this.mainModel = mainModel;
            this.dialogs = dialogs;
            Game = gameView;
            copies = Game.Copies.Select(a => a.Game).ToList();
            copies.ForEach(a => a.PropertyChanged += Game_PropertyChanged);
            SteamInstallProgress.Changed += SteamInstallProgress_Changed;
            InitializeCommands();
        }

        public void Dispose()
        {
            copies.ForEach(a => a.PropertyChanged -= Game_PropertyChanged);
            SteamInstallProgress.Changed -= SteamInstallProgress_Changed;
        }

        private void Game_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(ContextActionDescription));
        }

        private void SteamInstallProgress_Changed(object sender, Guid game)
        {
            if (copies.Any(a => a.Id == game))
            {
                OnPropertyChanged(nameof(ContextActionDescription));
            }
        }

        public void InitializeCommands()
        {
            ContextActionCommand = new RelayCommand<object>((a) =>
            {
                if (InstallingCopy != null || Game?.IsUninstalling == true)
                {
                    CheckSetup();
                }
                else if (Game?.IsRunning == true || Game?.IsLaunching == true)
                {
                    CheckExecution();
                }
                else if (Game?.IsInstalled == false)
                {
                    mainModel.InstallGame(Game.Game);
                }
                else if (Game?.IsInstalled == true)
                {
                    mainModel.StartGame(Game.Game, true);
                }
            });
        }

        public void CheckSetup()
        {
            if (dialogs.ShowMessage(
                resources.GetString("LOCCancelMonitoringSetupAsk"),
                resources.GetString("LOCCancelMonitoringAskTitle"),
                MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                gamesEditor.CancelGameMonitoring(InstallingCopy ?? Game.Game);
            }
        }

        public void CheckExecution()
        {
            if (dialogs.ShowMessage(
                resources.GetString("LOCCancelMonitoringExecutionAsk"),
                resources.GetString("LOCCancelMonitoringAskTitle"),
                MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                gamesEditor.CancelGameMonitoring(Game.Game);
            }
        }
    }
}
