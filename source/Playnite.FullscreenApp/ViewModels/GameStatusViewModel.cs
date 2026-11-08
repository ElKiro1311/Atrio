using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Playnite.FullscreenApp.ViewModels
{
    public class GameStatusViewModel : ObservableObject
    {
        private string gameStatusText;
        public string GameStatusText
        {
            get => gameStatusText;
            set
            {
                gameStatusText = value;
                OnPropertyChanged();
            }
        }

        private bool canReturn;
        /// <summary>The game is open: it can be brought back to the front or closed from here.</summary>
        public bool CanReturn
        {
            get => canReturn;
            set
            {
                canReturn = value;
                OnPropertyChanged();
            }
        }

        private string returnHint;
        /// <summary>How to come back to Atrio while playing, with the buttons that are on.</summary>
        public string ReturnHint
        {
            get => returnHint;
            set
            {
                returnHint = value;
                OnPropertyChanged();
            }
        }

        public GamesCollectionViewEntry Game { get; }

        public GameStatusViewModel(GamesCollectionViewEntry game)
        {
            Game = game;
        }
    }
}
