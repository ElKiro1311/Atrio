using Playnite.Atrio;
using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Playnite.DesktopApp.ViewModels
{
    public class StoreFilterItem : ObservableObject
    {
        public Guid? PluginId { get; set; }
        public Guid? EmulatorId { get; set; }
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

            if (item.EmulatorId != null)
            {
                ShowEmulator(item.IsSelected ? (Guid?)null : item.EmulatorId);
                return;
            }

            main.GamesView.EmulatorFilter = null;
            var filter = main.AppSettings.FilterSettings;
            if (item.PluginId == null || item.IsSelected)
            {
                filter.Library = null;
            }
            else
            {
                filter.Library = new IdItemFilterItemProperties(item.PluginId.Value);
            }

            SyncSelection();
            UpdateCount();
        }

        /// <summary>An emulator takes the place of the store filter, so that only one of them is ever active.</summary>
        public void ShowEmulator(Guid? emulatorId)
        {
            main.AppSettings.FilterSettings.Library = null;
            main.GamesView.EmulatorFilter = emulatorId;
            SyncSelection();
            UpdateCount();
        }

        private void SyncSelection()
        {
            if (Items == null)
            {
                return;
            }

            var ids = main.AppSettings.FilterSettings.Library?.Ids;
            var emulator = main.GamesView?.EmulatorFilter;
            foreach (var item in Items)
            {
                if (item.EmulatorId != null)
                {
                    item.IsSelected = item.EmulatorId == emulator;
                }
                else if (item.PluginId == null)
                {
                    item.IsSelected = !ids.HasItems() && emulator == null;
                }
                else
                {
                    item.IsSelected = ids?.Contains(item.PluginId.Value) == true;
                }
            }
        }

        private void UpdateCount()
        {
            var count = main.GamesView?.CollectionView?.Count ?? 0;
            GamesCountText = count == 1 ?
                ResourceProvider.GetString("LOCAtrioOneGame") :
                string.Format(ResourceProvider.GetString("LOCAtrioGamesCount"), count.ToString("N0", Localization.ApplicationLanguageCultureInfo));
        }

        /// <summary>Games of each emulator, leaving out the ones with none.</summary>
        public static Dictionary<Emulator, int> GetEmulatorCounts(Playnite.Database.IGameDatabaseMain database)
        {
            var counts = new Dictionary<Guid, int>();
            foreach (var game in database.Games)
            {
                var id = game.Hidden ? Guid.Empty : EmulatorStores.GetEmulatorId(game);
                if (id != Guid.Empty)
                {
                    counts.TryGetValue(id, out var count);
                    counts[id] = count + 1;
                }
            }

            return database.Emulators.Where(a => counts.ContainsKey(a.Id)).ToDictionary(a => a, a => counts[a.Id]);
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

            foreach (var emulator in GetEmulatorCounts(main.Database).OrderBy(a => a.Key.Name))
            {
                list.Add(new StoreFilterItem
                {
                    EmulatorId = emulator.Key.Id,
                    Name = emulator.Key.Name,
                    Icon = EmulatorStores.GetIcon(emulator.Key.Name),
                    CountText = emulator.Value.ToString("N0", Localization.ApplicationLanguageCultureInfo)
                });
            }

            Items = list;
            SyncSelection();
            UpdateCount();
            OnPropertyChanged(nameof(InstalledOnly));
        }
    }
}
