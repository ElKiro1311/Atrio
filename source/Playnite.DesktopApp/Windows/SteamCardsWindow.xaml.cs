using Playnite.Commands;
using Playnite.Controls;
using Playnite.DesktopApp.ViewModels;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Playnite.DesktopApp.Windows
{
    /// <summary>All the cards of a game of Steam, with the one chosen in big and its badge.</summary>
    public class SteamCardsModel : ObservableObject
    {
        public string WindowTitle { get; }
        private readonly SteamCardsPanel normal;

        private SteamCardsPanel panel;

        /// <summary>The set that shows: the normal cards, or the foil ones.</summary>
        public SteamCardsPanel Panel
        {
            get => panel;
            private set
            {
                panel = value;
                OnPropertyChanged();
            }
        }

        public bool ShowsNormal
        {
            get => !ShowsFoil;
            set => ShowsFoil = !value;
        }

        private bool showsFoil;
        public bool ShowsFoil
        {
            get => showsFoil;
            set
            {
                if (showsFoil != value)
                {
                    showsFoil = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShowsNormal));
                    ChangeSet();
                }
            }
        }

        private async void ChangeSet()
        {
            if (!ShowsFoil)
            {
                Panel = normal;
                Chosen = First(normal);
                return;
            }

            Panel = normal.Foil;
            Chosen = First(Panel);
            await normal.LoadFoilAsync();
            if (ShowsFoil)
            {
                Chosen = First(normal.Foil);
            }
        }

        private static SteamCardTile First(SteamCardsPanel panel)
        {
            return panel.Tiles.FirstOrDefault(a => a.IsOwned) ?? panel.Tiles.FirstOrDefault();
        }

        private SteamCardTile chosen;
        public SteamCardTile Chosen
        {
            get => chosen;
            private set
            {
                if (chosen == value)
                {
                    return;
                }

                chosen = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PictureUrl));
                OnPropertyChanged(nameof(ChosenMarketUrl));
                OnPropertyChanged(nameof(HasChosen));
            }
        }

        /// <summary>The artwork when it's known; the card otherwise.</summary>
        public string PictureUrl => Chosen?.Card.ArtworkUrl ?? Chosen?.ImageUrl;

        public string ChosenMarketUrl => Chosen?.Card.MarketHash.IsNullOrEmpty() != false ? null :
            $"https://steamcommunity.com/market/listings/753/{Uri.EscapeDataString(Chosen.Card.MarketHash)}";

        public bool HasChosen => Chosen != null;

        /// <summary>The page opened inside Atrio, behind this window.</summary>
        public event System.EventHandler OpenedBehind;

        public RelayCommand<SteamCardTile> ChooseCommand { get; }
        public RelayCommand<string> OpenUrlCommand { get; }

        public SteamCardsModel(string gameName, SteamCardsPanel panel, SteamCardTile first)
        {
            normal = panel;
            Panel = panel;
            WindowTitle = string.Format(ResourceProvider.GetString("LOCAtrioCardsWindow"), gameName);
            ChooseCommand = new RelayCommand<SteamCardTile>(a => Chosen = a ?? Chosen);
            OpenUrlCommand = new RelayCommand<string>(a =>
            {
                if (WebLinks.Open(a))
                {
                    OpenedBehind?.Invoke(this, System.EventArgs.Empty);
                }
            });
            Chosen = first ?? First(panel);
        }
    }

    public partial class SteamCardsWindow : WindowBase
    {
        public SteamCardsWindow(SteamCardsModel model) : base()
        {
            DataContext = model;
            InitializeComponent();
            model.OpenedBehind += (_, __) => Close();
        }

        public static void Open(string gameName, SteamCardsPanel panel, SteamCardTile first)
        {
            if (panel?.HasCards != true)
            {
                return;
            }

            var window = new SteamCardsWindow(new SteamCardsModel(gameName, panel, first))
            {
                Owner = PlayniteApplication.CurrentNative?.Windows.Cast<Window>().FirstOrDefault(a => a.IsActive) ?? PlayniteApplication.CurrentNative?.MainWindow
            };

            // No taller than the window it's in front of: its buttons are at the bottom.
            var owner = window.Owner;
            if (owner != null && owner.ActualHeight > 0)
            {
                window.Height = System.Math.Max(520, System.Math.Min(820, owner.ActualHeight * 0.9));
            }

            window.Show();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
