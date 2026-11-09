using Playnite.DesktopApp.ViewModels;
using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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

    /// <summary>The start page of the library. It lives next to the list of games, so it opens and closes without being unloaded.</summary>
    public class HomeView : AtrioPage
    {
        static HomeView()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(HomeView), new FrameworkPropertyMetadata(typeof(HomeView)));
        }

        private bool watching;

        public HomeView()
        {
            IsVisibleChanged += (_, __) => Update();
            DataContextChanged += (_, __) => Update();
        }

        protected override void OnPageOpened()
        {
            Update();
        }

        private void Update()
        {
            if (!(DataContext is HomeViewModel model) || watching == IsVisible)
            {
                return;
            }

            watching = IsVisible;
            if (watching)
            {
                model.StartWatching();
                model.Refresh();
            }
            else
            {
                model.StopWatching();
            }
        }
    }

    /// <summary>
    /// Every collection of the library. It lives next to the list of games, whose games can be dropped on a collection.
    /// </summary>
    public class CollectionsView : AtrioPage
    {
        /// <summary>The games being dragged, as a list of ids separated by commas.</summary>
        public const string DragFormat = "AtrioGames";

        private bool watching;

        private CollectionsViewModel Model => DataContext as CollectionsViewModel;

        static CollectionsView()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(CollectionsView), new FrameworkPropertyMetadata(typeof(CollectionsView)));
        }

        public CollectionsView()
        {
            AllowDrop = true;
            IsVisibleChanged += (_, __) => Update();
            DataContextChanged += (_, __) => Update();
        }

        protected override void OnPageOpened()
        {
            Update();
        }

        private void Update()
        {
            var model = Model;
            if (model == null || watching == IsVisible)
            {
                return;
            }

            watching = IsVisible;
            if (watching)
            {
                model.StartWatching();
                model.Refresh();
            }
            else
            {
                model.StopWatching();
            }
        }

        private static CollectionTile FindTile(object source)
        {
            for (var current = source as DependencyObject; current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is FrameworkElement element && element.DataContext is CollectionTile tile)
                {
                    return tile;
                }

                if (!(current is Visual))
                {
                    return null;
                }
            }

            return null;
        }

        private void Highlight(CollectionTile tile)
        {
            foreach (var other in Model?.Tiles ?? Enumerable.Empty<CollectionTile>())
            {
                other.IsDropTarget = other == tile;
            }
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            base.OnDragOver(e);
            var tile = FindTile(e.OriginalSource);
            var accepted = tile?.Collection.AcceptsGames == true && e.Data.GetDataPresent(DragFormat);
            e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            Highlight(accepted ? tile : null);
        }

        protected override void OnDragLeave(DragEventArgs e)
        {
            base.OnDragLeave(e);
            Highlight(null);
        }

        protected override void OnDrop(DragEventArgs e)
        {
            base.OnDrop(e);
            Highlight(null);
            var tile = FindTile(e.OriginalSource);
            if (tile?.Collection.AcceptsGames == true && e.Data.GetData(DragFormat) is string ids)
            {
                Model?.AddGames(tile.Collection, ids.Split(',').Select(Guid.Parse));
                e.Handled = true;
            }
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);
            var tile = FindTile(e.OriginalSource);
            if (tile != null)
            {
                Model?.ShowMenu(tile.Collection, e.OriginalSource as FrameworkElement ?? this);
                e.Handled = true;
            }
        }
    }

    /// <summary>The browser of Atrio. The browser itself comes from the model, the page only gives it a place.</summary>
    [TemplatePart(Name = "PART_BrowserHost", Type = typeof(ContentControl))]
    public class BrowserView : AtrioPage
    {
        private readonly BrowserViewModel model;

        static BrowserView()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(BrowserView), new FrameworkPropertyMetadata(typeof(BrowserView)));
        }

        public BrowserView()
        {
        }

        public BrowserView(BrowserViewModel model)
        {
            this.model = model;
            DataContext = model;
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            if (model != null && Template.FindName("PART_BrowserHost", this) is ContentControl host)
            {
                host.Content = model.Browser;
            }
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

    public class DownloadsView : AtrioPage
    {
        private readonly DownloadsViewModel model;

        static DownloadsView()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(DownloadsView), new FrameworkPropertyMetadata(typeof(DownloadsView)));
        }

        public DownloadsView()
        {
        }

        public DownloadsView(DownloadsViewModel model)
        {
            this.model = model;
            DataContext = model;
        }
    }

    public class WishlistView : AtrioPage
    {
        static WishlistView()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(WishlistView), new FrameworkPropertyMetadata(typeof(WishlistView)));
        }

        public WishlistView()
        {
        }

        public WishlistView(WishlistViewModel model)
        {
            DataContext = model;
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
