using Playnite.DesktopApp.ViewModels;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace Playnite.DesktopApp.Controls.Views
{
    /// <summary>
    /// Base for the pages that Atrio adds to the sidebar. They are lookless controls like the
    /// rest of Playnite's views, so any theme can restyle them from Views/*.xaml.
    /// </summary>
    public abstract class AtrioPage : Control
    {
        protected AtrioPage()
        {
            Loaded += AtrioPage_Loaded;
        }

        private void AtrioPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (!DesignerProperties.GetIsInDesignMode(this))
            {
                OnPageOpened();
            }
        }

        protected virtual void OnPageOpened()
        {
        }
    }

    public class HomeView : AtrioPage
    {
        private readonly HomeViewModel model;

        static HomeView()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(HomeView), new FrameworkPropertyMetadata(typeof(HomeView)));
        }

        public HomeView()
        {
        }

        public HomeView(HomeViewModel model)
        {
            this.model = model;
            DataContext = model;
            Unloaded += (_, __) => model.StopRotation();
        }

        protected override void OnPageOpened()
        {
            // The sidebar item refreshes this page each time it opens, the first load comes too early for it.
            model?.Refresh();
            model?.StartRotation();
        }
    }

    public class StoresView : AtrioPage
    {
        private readonly StoresViewModel model;

        static StoresView()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(StoresView), new FrameworkPropertyMetadata(typeof(StoresView)));
        }

        public StoresView()
        {
        }

        public StoresView(StoresViewModel model)
        {
            this.model = model;
            DataContext = model;
        }

        protected override void OnPageOpened()
        {
            model?.Refresh();
        }
    }

    public class ThemesView : AtrioPage
    {
        private readonly ThemesViewModel model;

        static ThemesView()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(ThemesView), new FrameworkPropertyMetadata(typeof(ThemesView)));
        }

        public ThemesView()
        {
        }

        public ThemesView(ThemesViewModel model)
        {
            this.model = model;
            DataContext = model;
        }

        protected override void OnPageOpened()
        {
            model?.Refresh();
        }
    }

    /// <summary>
    /// Row of store shortcuts shown above the library. Themes place it inside Views/Library.xaml.
    /// </summary>
    public class StoreFilterBar : Control
    {
        static StoreFilterBar()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(StoreFilterBar), new FrameworkPropertyMetadata(typeof(StoreFilterBar)));
        }

        public StoreFilterBar()
        {
            if (!DesignerProperties.GetIsInDesignMode(this))
            {
                DataContext = DesktopApplication.Current?.MainModel?.StoreFilter;
                Loaded += (_, __) => (DataContext as StoreFilterViewModel)?.Refresh();
            }
        }
    }
}
