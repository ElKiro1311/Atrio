using Playnite.Behaviors;
using Playnite.Common;
using Playnite.Converters;
using Playnite.DesktopApp.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Playnite.DesktopApp.Controls.Views
{
    [TemplatePart(Name = "PART_ElemMainMenu", Type = typeof(FrameworkElement))]
    [TemplatePart(Name = "PART_PanelSideBarItems", Type = typeof(Panel))]
    [TemplatePart(Name = "PART_PanelSideBarBottomItems", Type = typeof(Panel))]
    [TemplatePart(Name = "PART_ScrollSideBarItems", Type = typeof(ScrollViewer))]
    [TemplatePart(Name = "PART_ButtonSideBarMore", Type = typeof(ButtonBase))]
    public class Sidebar : Control
    {
        private readonly DesktopAppViewModel mainModel;
        private Panel PanelSideBarItems;
        private Panel PanelSideBarBottomItems;
        private FrameworkElement ElemMainMenu;
        private ScrollViewer ScrollSideBarItems;
        private ButtonBase ButtonSideBarMore;

        static Sidebar()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(Sidebar), new FrameworkPropertyMetadata(typeof(Sidebar)));
        }

        public Sidebar() : this(DesktopApplication.Current?.MainModel)
        {
        }

        public Sidebar(DesktopAppViewModel mainModel)
        {
            if (DesignerProperties.GetIsInDesignMode(this))
            {
                this.mainModel = DesignMainViewModel.DesignIntance;
            }
            else if (mainModel != null)
            {
                this.mainModel = mainModel;
            }

            DataContext = this.mainModel;
            Loaded += Sidebar_Loaded;
            Unloaded += Sidebar_Unloaded;
        }

        private void Sidebar_Loaded(object sender, RoutedEventArgs e)
        {
            mainModel.SidebarItems.CollectionChanged += SidebarItems_CollectionChanged;
        }

        private void Sidebar_Unloaded(object sender, RoutedEventArgs e)
        {
            mainModel.SidebarItems.CollectionChanged -= SidebarItems_CollectionChanged;
        }

        private void SidebarItems_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            LoadSidebarItems();
        }

        public void LoadSidebarItems()
        {
            if (PanelSideBarItems == null)
            {
                return;
            }

            PanelSideBarItems.Children.Clear();
            PanelSideBarBottomItems?.Children.Clear();
            foreach (var sideItem in mainModel.SidebarItems)
            {
                var panel = sideItem.PlaceAtBottom && PanelSideBarBottomItems != null ? PanelSideBarBottomItems : PanelSideBarItems;
                panel.Children.Add(new SidebarItem { DataContext = sideItem });
            }
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            PanelSideBarItems = Template.FindName("PART_PanelSideBarItems", this) as Panel;
            PanelSideBarBottomItems = Template.FindName("PART_PanelSideBarBottomItems", this) as Panel;
            if (PanelSideBarItems != null)
            {
                LoadSidebarItems();
            }

            ElemMainMenu = Template.FindName("PART_ElemMainMenu", this) as FrameworkElement;
            if (ElemMainMenu != null)
            {
                LeftClickContextMenuBehavior.SetEnabled(ElemMainMenu, true);
                ElemMainMenu.ContextMenu = new MainMenu(mainModel)
                {
                    StaysOpen = false,
                    Placement = PlacementMode.Bottom
                };
                ElemMainMenu.ContextMenu.SetResourceReference(ContextMenu.StyleProperty, "TopPanelMenu");
                if (!DesktopAppViewModel.UsesLibraryPanes)
                {
                    BindingTools.SetBinding(ElemMainMenu,
                        FrameworkElement.VisibilityProperty,
                        mainModel.AppSettings,
                        nameof(PlayniteSettings.ShowMainMenuOnTopPanel),
                        converter: new InvertedBooleanToVisibilityConverter());
                }
            }

            ScrollSideBarItems = Template.FindName("PART_ScrollSideBarItems", this) as ScrollViewer;
            ButtonSideBarMore = Template.FindName("PART_ButtonSideBarMore", this) as ButtonBase;
            if (ScrollSideBarItems != null && ButtonSideBarMore != null)
            {
                ScrollSideBarItems.ScrollChanged += (_, __) =>
                    ButtonSideBarMore.Visibility = ScrollSideBarItems.ScrollableWidth > 0 ? Visibility.Visible : Visibility.Collapsed;
                ScrollSideBarItems.PreviewMouseWheel += (_, e) =>
                {
                    ScrollSideBarItems.ScrollToHorizontalOffset(ScrollSideBarItems.HorizontalOffset - e.Delta);
                    e.Handled = true;
                };
                ButtonSideBarMore.Click += (_, __) => ShowHiddenItems();
            }
        }

        /// <summary>What doesn't fit in a row of navigation stays one click away.</summary>
        public void ShowHiddenItems()
        {
            var menu = new ContextMenu
            {
                PlacementTarget = ButtonSideBarMore,
                Placement = PlacementMode.Bottom,
                StaysOpen = false
            };
            menu.SetResourceReference(ContextMenu.StyleProperty, "TopPanelMenu");
            foreach (var item in GetHiddenItems())
            {
                menu.Items.Add(new MenuItem { Header = item.Title, Command = item.Command });
            }

            menu.IsOpen = true;
        }

        public List<SidebarWrapperItem> GetHiddenItems()
        {
            var hidden = new List<SidebarWrapperItem>();
            var items = PanelSideBarItems.Children.OfType<SidebarItem>().ToList();
            if (PanelSideBarBottomItems != null)
            {
                items.AddRange(PanelSideBarBottomItems.Children.OfType<SidebarItem>());
            }

            foreach (var item in items.Where(a => a.IsVisible))
            {
                var bounds = item.TransformToAncestor(ScrollSideBarItems).TransformBounds(new Rect(item.RenderSize));
                if (bounds.Left < -0.5 || bounds.Right > ScrollSideBarItems.ViewportWidth + 0.5)
                {
                    hidden.Add((SidebarWrapperItem)item.DataContext);
                }
            }

            return hidden;
        }
    }
}
