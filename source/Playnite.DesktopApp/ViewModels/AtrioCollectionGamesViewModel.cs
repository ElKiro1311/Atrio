using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.Windows;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;

namespace Playnite.DesktopApp.ViewModels
{
    public class CollectionGameOption : ObservableObject
    {
        public Game Game { get; }
        public string Name { get; }
        public Action SelectionChanged { get; set; }

        private bool isSelected;
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                SetValue(ref isSelected, value);
                SelectionChanged?.Invoke();
            }
        }

        public CollectionGameOption(Game game, string name, bool isSelected)
        {
            Game = game;
            Name = name;
            this.isSelected = isSelected;
        }
    }

    /// <summary>The games of a collection, ticked in a list with all of them: the quick way to fill one.</summary>
    public class CollectionGamesViewModel : ObservableObject
    {
        private readonly IWindowFactory window;
        private readonly List<CollectionGameOption> options;

        public string WindowTitle { get; }
        public ICollectionView Games { get; }

        private string searchText;
        public string SearchText
        {
            get => searchText;
            set
            {
                SetValue(ref searchText, value);
                Games.Refresh();
            }
        }

        public string SelectionText => CollectionsViewModel.GetCountText(options.Count(a => a.IsSelected));

        public RelayCommand<object> ConfirmCommand { get; }
        public RelayCommand<object> CloseCommand { get; }

        public CollectionGamesViewModel(IWindowFactory window, string collectionName, List<CollectionGameOption> options)
        {
            this.window = window;
            this.options = options;
            WindowTitle = string.Format(ResourceProvider.GetString("LOCAtrioCollectionGamesTitle"), collectionName);
            options.ForEach(a => a.SelectionChanged = () => OnPropertyChanged(nameof(SelectionText)));

            // What is already in goes first, the rest of the library can be thousands of games.
            Games = new ListCollectionView(options.
                OrderByDescending(a => a.IsSelected).
                ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).
                ToList())
            {
                Filter = a => searchText.IsNullOrEmpty() || ((CollectionGameOption)a).Name?.IndexOf(searchText, StringComparison.CurrentCultureIgnoreCase) >= 0
            };

            ConfirmCommand = new RelayCommand<object>(_ => window.Close(true));
            CloseCommand = new RelayCommand<object>(_ => window.Close(false));
        }

        public bool? OpenView()
        {
            return window.CreateAndOpenDialog(this);
        }
    }
}
