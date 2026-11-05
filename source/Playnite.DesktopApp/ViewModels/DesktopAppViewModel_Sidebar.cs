using Playnite.Common;
using Playnite.Database;
using Playnite.Extensions.Markup;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing.Imaging;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Playnite.DesktopApp.ViewModels
{
    public class SidebarWrapperItem : ObservableObject
    {
        public enum SourceType
        {
            Builtin,
            Extension,
            SoftwareTool
        }

        private static ILogger logger = LogManager.GetLogger();
        private DesktopAppViewModel model;
        public RelayCommand<object> Command { get; set; }

        public SidebarItem SideItem { get; }

        public SiderbarItemType Type
        {
            get => SideItem.Type;
        }

        public string Title
        {
            get => SideItem.Title;
        }

        public Thickness IconPadding
        {
            get => SideItem.IconPadding;
            set { }
        }

        public double ProgressValue
        {
            get => SideItem.ProgressValue;
            set { }
        }

        public double ProgressMaximum
        {
            get => SideItem.ProgressMaximum;
            set { }
        }

        public bool Visible
        {
            get => SideItem.Visible;
            set { }
        }

        public object IconObject => SdkHelpers.ResolveUiItemIcon(SideItem.Icon);

        /// <summary>Goes to the lower group of the sidebar in themes that have one.</summary>
        public bool PlaceAtBottom => SideItem is AtrioSidebarItem item && item.PlaceAtBottom;

        private bool selected;
        public bool Selected
        {
            get => selected;
            set
            {
                if (SideItem.Type == SiderbarItemType.Button)
                {
                    return;
                }

                if (selected != value && value == false)
                {
                    if (SideItem.Closed != null)
                    {
                        SideItem.Closed();
                    }
                }

                selected = value;
                OnPropertyChanged();
            }
        }

        public SidebarWrapperItem(SidebarItem item, DesktopAppViewModel model)
        {
            this.model = model;
            SideItem = item;
            Command = new RelayCommand<object>(Activation);
            item.PropertyChanged += Item_PropertyChanged;
        }

        private void Item_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            OnPropertyChanged(e.PropertyName);
        }

        private void Activation(object arg)
        {
            if (Selected)
            {
                return;
            }

            if (SideItem.Type == SiderbarItemType.Button)
            {
                try
                {
                    SideItem.Activated();
                }
                catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Error(e, "Failed to activate sidebar item.");
                }
            }
            else
            {
                Control view = null;
                if (SideItem.Opened != null)
                {
                    view = SideItem.Opened();
                    if (view == null)
                    {
                        return;
                    }
                }

                model.SidebarItems.ForEach(a =>
                {
                    if (a.Selected)
                    {
                        a.Selected = false;
                    }
                });

                Selected = true;
                model.ActiveView = view;
                model.UpdateAmbient();
                Controls.PageTransition.Play(view);
            }
        }
    }

    public class MainSidebarViewItem : SidebarItem
    {
        private Control view;
        private DesktopAppViewModel model;

        public ApplicationView AppView { get; }

        public MainSidebarViewItem(Control view, DesktopAppViewModel model, ApplicationView appView)
        {
            this.view = view;
            this.AppView = appView;
            this.model = model;
            Type = SiderbarItemType.View;
            Opened = () =>
            {
                if (AppView == ApplicationView.Statistics)
                {
                    model.LibraryStats.Calculate();
                }

                return view;
            };
        }
    }

    public class AtrioSidebarItem : SidebarItem
    {
        public bool PlaceAtBottom { get; set; }

        public AtrioSidebarItem(Control view, Action opened = null)
        {
            Type = SiderbarItemType.View;
            Opened = () =>
            {
                opened?.Invoke();
                return view;
            };
        }

        public AtrioSidebarItem(Action activated)
        {
            Type = SiderbarItemType.Button;
            Activated = activated;
        }
    }

    public class SoftwareToolSidebarItem : SidebarItem
    {
        private AppSoftware app;
        private DesktopAppViewModel model;

        public SoftwareToolSidebarItem(AppSoftware app, DesktopAppViewModel model)
        {
            this.app = app;
            this.model = model;
            Type = SiderbarItemType.Button;
            Icon = app.Icon;
            Title = app.Name;
            Activated = () => model.StartSoftwareToolCommand.Execute(app);
        }
    }

    public partial class DesktopAppViewModel
    {
        private ObservableCollection<SidebarWrapperItem> sidebarItems = new ObservableCollection<SidebarWrapperItem>();
        public ObservableCollection<SidebarWrapperItem> SidebarItems
        {
            get => sidebarItems;
            set
            {
                sidebarItems = value;
                OnPropertyChanged();
            }
        }

        private SidebarWrapperItem storesSidebarItem;

        public void SwitchToStoresView()
        {
            storesSidebarItem?.Command.Execute(null);
        }

        /// <summary>
        /// The add-on links to its settings only from a first page that goes away as soon as there are
        /// achievements, and its refresh button is an icon without a name. The page keeps both in plain words.
        /// </summary>
        private void AddAchievementsButtons(SidebarItem item)
        {
            var open = item.Opened;
            item.Opened = () =>
            {
                var refresh = CreateAchievementsButton("LOCAtrioAchievementsRefresh");
                refresh.Command = new RelayCommand<object>(_ => ShowAchievementsRefreshMenu(refresh));
                var settings = CreateAchievementsButton("LOCAtrioAchievementsSettings");
                settings.Command = new RelayCommand<object>(_ => OpenPluginSettings(Playnite.Atrio.AtrioInfo.AchievementsPluginId));

                var buttons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 10, 140, 0)
                };
                buttons.Children.Add(refresh);
                buttons.Children.Add(settings);

                var page = new Grid();
                page.Children.Add(open());
                page.Children.Add(buttons);
                return new ContentControl { Content = page, Focusable = false };
            };
        }

        private Button CreateAchievementsButton(string textKey)
        {
            var button = new Button
            {
                Content = Resources.GetString(textKey),
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(12, 0, 12, 0),
                MinHeight = 30,
                FontSize = 12
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, "AtrioGhostButton");
            return button;
        }

        /// <summary>
        /// Offers what the add-on puts in its own menu, which already knows about a refresh in progress:
        /// the kinds of refresh when it is idle, and how to follow or cancel the current one when it is not.
        /// </summary>
        private void ShowAchievementsRefreshMenu(Button button)
        {
            var refreshSection = "|" + Resources.GetString("LOCPlayAch_Button_Refresh");
            var inProgress = new[] { "LOCPlayAch_Menu_RefreshInProgress", "LOCPlayAch_Common_View", "LOCPlayAch_Common_Cancel" }.Select(a => Resources.GetString(a)).ToList();
            var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            try
            {
                var plugin = Extensions.Plugins[Playnite.Atrio.AtrioInfo.AchievementsPluginId].Plugin;
                foreach (var item in plugin.GetMainMenuItems(new GetMainMenuItemsArgs()))
                {
                    if (item.MenuSection?.EndsWith(refreshSection) != true && !inProgress.Contains(item.Description))
                    {
                        continue;
                    }

                    var entry = new MenuItem { Header = item.Description, IsEnabled = item.Action != null };
                    entry.Click += (_, __) => item.Action(new MainMenuItemActionArgs());
                    menu.Items.Add(entry);
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                Logger.Error(e, "Failed to get the refresh options of the achievements add-on.");
                return;
            }

            button.ContextMenu = menu;
            menu.IsOpen = true;
        }

        public void LoadSideBarItems()
        {
            libraryView = new Controls.Views.Library(this);
            statsView = new Controls.LibraryStatistics(LibraryStats);
            StoreFilter = new StoreFilterViewModel(this);
            HomeModel = new HomeViewModel(this);
            var storesModel = new StoresViewModel(this);
            var themesModel = new ThemesViewModel(this);

            var libraryItem = new MainSidebarViewItem(libraryView, this, ApplicationView.Library)
            {
                Icon = "SidebarLibraryIcon",
                Title = Resources.GetString(LOC.Library)
            };

            var statsItem = new MainSidebarViewItem(statsView, this, ApplicationView.Statistics)
            {
                Icon = "SidebarStatisticsIcon",
                Title = Resources.GetString(LOC.Statistics)
            };

            var sideItems = new List<SidebarWrapperItem>();
            SidebarWrapperItem achievementsItem = null;
            foreach (var plugin in Extensions.Plugins)
            {
                try
                {
                    var items = plugin.Value.Plugin.GetSidebarItems().ToList();
                    if (items.HasItems())
                    {
                        if (plugin.Key == Playnite.Atrio.AtrioInfo.AchievementsPluginId)
                        {
                            items[0].Title = Resources.GetString("LOCAtrioAchievements");
                            AddAchievementsButtons(items[0]);
                            achievementsItem = new SidebarWrapperItem(items[0], this);
                            continue;
                        }

                        items.ForEach(a => sideItems.Add(new SidebarWrapperItem(a, this)));
                    }
                }
                catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    Logger.Error(e, $"Failed to GetSidebarItems, from {plugin.Value.Description.Name}");
                }
            }

            sideItems = sideItems.OrderByDescending(a => a.SideItem.Type).ThenBy(a => a.SideItem.Title).ToList();
            sideItems.Insert(0, new SidebarWrapperItem(statsItem, this));
            if (achievementsItem != null)
            {
                sideItems.Insert(0, achievementsItem);
            }

            storesSidebarItem = new SidebarWrapperItem(new AtrioSidebarItem(new Controls.Views.StoresView(storesModel))
            {
                Icon = "SidebarStoresIcon",
                Title = Resources.GetString("LOCAtrioStores")
            }, this);
            sideItems.Insert(0, storesSidebarItem);
            sideItems.Insert(0, new SidebarWrapperItem(libraryItem, this));
            sideItems.Insert(0, new SidebarWrapperItem(new AtrioSidebarItem(new Controls.Views.HomeView(HomeModel), HomeModel.Refresh)
            {
                Icon = "SidebarHomeIcon",
                Title = Resources.GetString("LOCAtrioHome")
            }, this));

            sideItems.Add(new SidebarWrapperItem(new AtrioSidebarItem(new Controls.Views.ThemesView(themesModel), themesModel.Refresh)
            {
                Icon = "SidebarThemesIcon",
                Title = Resources.GetString("LOCAtrioThemes"),
                PlaceAtBottom = true
            }, this));
            sideItems.Add(new SidebarWrapperItem(new AtrioSidebarItem(() => OpenAddonsCommand.Execute(null))
            {
                Icon = "SidebarAddonsIcon",
                Title = Resources.GetString("LOCAtrioAddons"),
                PlaceAtBottom = true
            }, this));
            sideItems.Add(new SidebarWrapperItem(new AtrioSidebarItem(() => OpenSettingsCommand.Execute(null))
            {
                Icon = "SidebarSettingsIcon",
                Title = Resources.GetString("LOCAtrioSettings"),
                PlaceAtBottom = true
            }, this));

            sideItems[0].Command.Execute(null);
            SidebarItems.AddRange(sideItems);
        }

        public void LoadSoftwareToolsSidebarItems()
        {
            if (!Database.IsOpen)
            {
                return;
            }

            Database.SoftwareApps.ItemUpdated += (_, __) => RefreshSoftwareToolsSidebarItems();
            Database.SoftwareApps.ItemCollectionChanged += (_, __) => RefreshSoftwareToolsSidebarItems();
            foreach (var tool in Database.SoftwareApps.OrderBy(a => a.Name))
            {
                if (tool.ShowOnSidebar)
                {
                    SidebarItems.Add(new SidebarWrapperItem(new SoftwareToolSidebarItem(tool, this), this));
                }
            }
        }

        public void RefreshSoftwareToolsSidebarItems()
        {
            SidebarItems.Where(a => a.SideItem is SoftwareToolSidebarItem).ToList().ForEach(a => SidebarItems.Remove(a));
            foreach (var tool in Database.SoftwareApps.OrderBy(a => a.Name))
            {
                if (tool.ShowOnSidebar)
                {
                    SidebarItems.Add(new SidebarWrapperItem(new SoftwareToolSidebarItem(tool, this), this));
                }
            }
        }
    }
}
