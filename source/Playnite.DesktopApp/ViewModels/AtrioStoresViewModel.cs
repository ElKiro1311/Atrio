using Playnite.Atrio;
using Playnite.Common;
using Playnite.Plugins;
using Playnite.SDK;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Playnite.DesktopApp.ViewModels
{
    public class StoreCard
    {
        public string Name { get; set; }
        public object Icon { get; set; }
        public LibraryPlugin Plugin { get; set; }
        public string AddonId { get; set; }
        public string Description { get; set; }
        public string StatsText { get; set; }
        public string ClientText { get; set; }
        public bool ClientInstalled { get; set; }
        public bool HasSettings { get; set; }
        public string GamesText { get; set; }
        public string InstalledText { get; set; }
        public int GamesCount { get; set; }
        public bool NeedsDownload { get; set; }
        public string ActionText { get; set; }
        public System.Windows.Media.Brush CardBrush { get; set; }
        public System.Windows.Media.Brush ColorBrush { get; set; }
    }

    public class StorePriorityItem
    {
        public Guid PluginId { get; set; }
        public string Name { get; set; }
        public object Icon { get; set; }
    }

    public class StoresViewModel : ObservableObject
    {
        private readonly DesktopAppViewModel main;

        private List<StoreCard> stores;
        public List<StoreCard> Stores
        {
            get => stores;
            set => SetValue(ref stores, value);
        }

        private List<StoreCard> disabledStores;
        public List<StoreCard> DisabledStores
        {
            get => disabledStores;
            set
            {
                SetValue(ref disabledStores, value);
                OnPropertyChanged(nameof(HasDisabledStores));
            }
        }

        public bool HasDisabledStores => DisabledStores?.Count > 0;

        public ObservableCollection<StorePriorityItem> Priority { get; } = new ObservableCollection<StorePriorityItem>();

        private string unifySummary;
        public string UnifySummary
        {
            get => unifySummary;
            set => SetValue(ref unifySummary, value);
        }

        public bool UnifyDuplicates
        {
            get => GameUnifier.Current?.Settings.UnifyDuplicates == true;
            set
            {
                var unifier = GameUnifier.Current;
                unifier.Settings.UnifyDuplicates = value;
                unifier.Settings.Save();
                unifier.Rebuild();
                OnPropertyChanged();
                UpdateSummary();
            }
        }

        public bool AskStoreOnStart
        {
            get => GameUnifier.Current?.Settings.AskStoreOnStart == true;
            set
            {
                var unifier = GameUnifier.Current;
                unifier.Settings.AskStoreOnStart = value;
                unifier.Settings.Save();
                OnPropertyChanged();
            }
        }

        public RelayCommand<StoreCard> OpenClientCommand { get; }
        public RelayCommand<StoreCard> ConfigureCommand { get; }
        public RelayCommand<StoreCard> UpdateLibraryCommand { get; }
        public RelayCommand<StoreCard> EnableCommand { get; }
        public RelayCommand<StorePriorityItem> MoveUpCommand { get; }
        public RelayCommand<StorePriorityItem> MoveDownCommand { get; }
        public RelayCommand<object> UpdateAllCommand { get; }
        public RelayCommand<object> OpenAddonsCommand { get; }

        public StoresViewModel(DesktopAppViewModel main)
        {
            this.main = main;
            OpenClientCommand = new RelayCommand<StoreCard>(a => a?.Plugin?.Client?.Open());
            ConfigureCommand = new RelayCommand<StoreCard>(a =>
            {
                if (a?.Plugin != null)
                {
                    main.OpenPluginSettings(a.Plugin.Id);
                }
            });
            UpdateLibraryCommand = new RelayCommand<StoreCard>(a =>
            {
                if (a?.Plugin != null)
                {
                    main.UpdateLibraryCommand.Execute(a.Plugin);
                }
            });
            EnableCommand = new RelayCommand<StoreCard>(Enable);
            MoveUpCommand = new RelayCommand<StorePriorityItem>(a => Move(a, -1));
            MoveDownCommand = new RelayCommand<StorePriorityItem>(a => Move(a, 1));
            UpdateAllCommand = new RelayCommand<object>(_ => main.UpdateGamesCommand.Execute(null));
            OpenAddonsCommand = new RelayCommand<object>(_ => main.OpenAddonsCommand.Execute(null));
        }

        private void Enable(StoreCard card)
        {
            if (card?.AddonId == null)
            {
                return;
            }

            if (card.NeedsDownload)
            {
                main.App.InstallOnlineAddon(card.AddonId);
                return;
            }

            main.AppSettings.DisabledPlugins.Remove(card.AddonId);
            main.AppSettings.SaveSettings();
            main.App.Restart(new CmdLineOptions { SkipLibUpdate = true }, true);
        }

        private void Move(StorePriorityItem item, int direction)
        {
            var index = Priority.IndexOf(item);
            var target = index + direction;
            if (index < 0 || target < 0 || target >= Priority.Count)
            {
                return;
            }

            Priority.Move(index, target);
            var unifier = GameUnifier.Current;
            unifier.Settings.StorePriority = Priority.Select(a => a.PluginId).ToList();
            unifier.Settings.Save();
            unifier.RebuildAndNotify();
        }

        private void UpdateSummary()
        {
            var unifier = GameUnifier.Current;
            UnifySummary = string.Format(
                ResourceProvider.GetString("LOCAtrioUnifySummary"),
                unifier.GetAllGroups().Count().ToString("N0", Localization.ApplicationLanguageCultureInfo),
                unifier.CollapsedCopiesCount.ToString("N0", Localization.ApplicationLanguageCultureInfo));
        }

        public void Refresh()
        {
            var counts = new Dictionary<Guid, int>();
            var installedCounts = new Dictionary<Guid, int>();
            foreach (var game in main.Database.Games)
            {
                counts.TryGetValue(game.PluginId, out var count);
                counts[game.PluginId] = count + 1;
                if (game.IsInstalled)
                {
                    installedCounts.TryGetValue(game.PluginId, out var installed);
                    installedCounts[game.PluginId] = installed + 1;
                }
            }

            var cards = new List<StoreCard>();
            foreach (var plugin in main.Extensions.LibraryPlugins.OrderBy(a => GameUnifier.Current.GetStoreRank(a.Id, a.Name)).ThenBy(a => a.Name))
            {
                counts.TryGetValue(plugin.Id, out var games);
                installedCounts.TryGetValue(plugin.Id, out var installed);
                var hasClient = plugin.Client != null;
                var clientInstalled = false;
                try
                {
                    clientInstalled = hasClient && plugin.Client.IsInstalled;
                }
                catch
                {
                    // A broken client check of one add-on must not take the whole page down.
                }

                cards.Add(new StoreCard
                {
                    Name = plugin.Name,
                    Plugin = plugin,
                    GamesCount = games,
                    GamesText = games.ToString("N0", Localization.ApplicationLanguageCultureInfo),
                    InstalledText = string.Format(ResourceProvider.GetString("LOCAtrioInstalledCount"), installed.ToString("N0", Localization.ApplicationLanguageCultureInfo)),
                    CardBrush = StoreBranding.GetCardBrush(plugin.Name),
                    ColorBrush = StoreBranding.GetBrush(plugin.Name),
                    Icon = plugin.LibraryIcon == null ? null : ImageSourceManager.GetImage(plugin.LibraryIcon, true),
                    StatsText = string.Format(
                        ResourceProvider.GetString("LOCAtrioStoreStats"),
                        games.ToString("N0", Localization.ApplicationLanguageCultureInfo),
                        installed.ToString("N0", Localization.ApplicationLanguageCultureInfo)),
                    ClientInstalled = clientInstalled,
                    ClientText = ResourceProvider.GetString(!hasClient ? "LOCAtrioClientNone" : clientInstalled ? "LOCAtrioClientInstalled" : "LOCAtrioClientMissing"),
                    HasSettings = true
                });
            }

            Stores = cards;
            DisabledStores = GetDisabledStores();

            Priority.Clear();
            var unifier = GameUnifier.Current;
            foreach (var plugin in main.Extensions.LibraryPlugins.OrderBy(a => unifier.GetStoreRank(a.Id, a.Name)).ThenBy(a => a.Name))
            {
                Priority.Add(new StorePriorityItem
                {
                    PluginId = plugin.Id,
                    Name = plugin.Name,
                    Icon = plugin.LibraryIcon == null ? null : ImageSourceManager.GetImage(plugin.LibraryIcon, true)
                });
            }

            UpdateSummary();
            OnPropertyChanged(nameof(UnifyDuplicates));
            OnPropertyChanged(nameof(AskStoreOnStart));
        }

        private List<StoreCard> GetDisabledStores()
        {
            var result = new List<StoreCard>();
            var installed = ExtensionFactory.GetInstalledManifests();
            foreach (var addon in installed)
            {
                if (addon.Type == ExtensionType.GameLibrary && main.AppSettings.DisabledPlugins?.Contains(addon.Id) == true)
                {
                    result.Add(new StoreCard
                    {
                        Name = System.Text.RegularExpressions.Regex.Replace(
                            addon.Name, @"\s*(library|importer|integration|plugin)", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim(),
                        AddonId = addon.Id,
                        Icon = addon.Icon.IsNullOrEmpty() ? null : ImageSourceManager.GetImage(System.IO.Path.Combine(addon.DirectoryPath, addon.Icon), true),
                        Description = addon.Author,
                        ActionText = ResourceProvider.GetString("LOCAtrioEnable")
                    });
                }
            }

            result.AddRange(AtrioInfo.OnlineStores.Where(a => !installed.Any(b => b.Id == a.AddonId)).Select(a => new StoreCard
            {
                Name = a.Name,
                AddonId = a.AddonId,
                NeedsDownload = true,
                ActionText = ResourceProvider.GetString("LOCAtrioStoreDownload")
            }));

            return result.OrderBy(a => a.Name).ToList();
        }
    }
}
