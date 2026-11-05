using Playnite.Atrio;
using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Playnite.DesktopApp.ViewModels
{
    public class StoreFilterItem : ObservableObject
    {
        public Guid? PluginId { get; set; }
        public string Name { get; set; }
        public object Icon { get; set; }
        public string CountText { get; set; }

        private bool isSelected;
        public bool IsSelected
        {
            get => isSelected;
            set => SetValue(ref isSelected, value);
        }
    }

    /// <summary>
    /// Shortcut to the store filter, so that the most common filter is one click away and labeled.
    /// </summary>
    public class StoreFilterViewModel : ObservableObject
    {
        private readonly DesktopAppViewModel main;

        private List<StoreFilterItem> items;
        public List<StoreFilterItem> Items
        {
            get => items;
            set => SetValue(ref items, value);
        }

        private string gamesCountText;
        public string GamesCountText
        {
            get => gamesCountText;
            set => SetValue(ref gamesCountText, value);
        }

        public bool InstalledOnly
        {
            get => main.AppSettings.FilterSettings.IsInstalled;
            set
            {
                main.AppSettings.FilterSettings.IsInstalled = value;
                OnPropertyChanged();
            }
        }

        public RelayCommand<StoreFilterItem> SelectCommand { get; }

        public StoreFilterViewModel(DesktopAppViewModel main)
        {
            this.main = main;
            SelectCommand = new RelayCommand<StoreFilterItem>(Select);
            main.AppSettings.FilterSettings.FilterChanged += (_, __) =>
            {
                SyncSelection();
                OnPropertyChanged(nameof(InstalledOnly));
                UpdateCount();
            };
        }

        private void Select(StoreFilterItem item)
        {
            if (item == null)
            {
                return;
            }

            var filter = main.AppSettings.FilterSettings;
            if (item.PluginId == null || item.IsSelected)
            {
                filter.Library = null;
            }
            else
            {
                filter.Library = new IdItemFilterItemProperties(item.PluginId.Value);
            }
        }

        private void SyncSelection()
        {
            if (Items == null)
            {
                return;
            }

            var ids = main.AppSettings.FilterSettings.Library?.Ids;
            foreach (var item in Items)
            {
                item.IsSelected = item.PluginId == null ? !ids.HasItems() : ids?.Contains(item.PluginId.Value) == true;
            }
        }

        private void UpdateCount()
        {
            var count = main.GamesView?.CollectionView?.Count ?? 0;
            GamesCountText = count == 1 ?
                ResourceProvider.GetString("LOCAtrioOneGame") :
                string.Format(ResourceProvider.GetString("LOCAtrioGamesCount"), count.ToString("N0", Localization.ApplicationLanguageCultureInfo));
        }

        public void Refresh()
        {
            if (main.GamesView == null)
            {
                return;
            }

            var counts = new Dictionary<Guid, int>();
            foreach (var game in main.Database.Games)
            {
                if (!game.Hidden)
                {
                    counts.TryGetValue(game.PluginId, out var count);
                    counts[game.PluginId] = count + 1;
                }
            }

            var collapsed = GameUnifier.Current?.CollapsedCopiesCount ?? 0;
            var list = new List<StoreFilterItem>
            {
                new StoreFilterItem
                {
                    Name = ResourceProvider.GetString("LOCAtrioAllStores"),
                    CountText = (counts.Values.Sum() - collapsed).ToString("N0", Localization.ApplicationLanguageCultureInfo)
                }
            };

            foreach (var plugin in main.Extensions.LibraryPlugins.OrderBy(a => GameUnifier.Current.GetStoreRank(a.Id, a.Name)).ThenBy(a => a.Name))
            {
                if (counts.TryGetValue(plugin.Id, out var count) && count > 0)
                {
                    list.Add(new StoreFilterItem
                    {
                        PluginId = plugin.Id,
                        Name = plugin.Name,
                        Icon = plugin.LibraryIcon == null ? null : ImageSourceManager.GetImage(plugin.LibraryIcon, true),
                        CountText = count.ToString("N0", Localization.ApplicationLanguageCultureInfo)
                    });
                }
            }

            Items = list;
            SyncSelection();
            UpdateCount();
            OnPropertyChanged(nameof(InstalledOnly));
        }
    }
}
