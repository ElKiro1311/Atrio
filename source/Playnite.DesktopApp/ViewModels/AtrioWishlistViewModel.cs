using Playnite.Atrio;
using Playnite.Commands;
using Playnite.Common;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Playnite.DesktopApp.ViewModels
{
    /// <summary>A game of the list, or found by the search, as the page shows it.</summary>
    public class WishlistRow : ObservableObject
    {
        public WishlistItem Item { get; set; }
        public string Title => Item.Title;
        public string ImageUrl => Item.ImageUrl;
        public string StoreName => Item.Store == WishlistItem.Gog ? "GOG" : "Steam";
        public bool OnSale => Item.Discount > 0;
        public string DiscountText => OnSale ? $"-{Item.Discount} %" : null;
        public string BasePriceText => OnSale ? Item.BasePriceText : null;

        public string PriceText
        {
            get
            {
                if (Item.IsFree || Item.Price == 0)
                {
                    return ResourceProvider.GetString("LOCAtrioWishFreePrice");
                }

                if (Item.Price == null)
                {
                    return ResourceProvider.GetString(Item.ComingSoon ? "LOCAtrioWishComingSoon" : "LOCAtrioWishNoPrice");
                }

                return Item.PriceText;
            }
        }

        /// <summary>The lowest price seen, until when the offer lasts, or when it comes out.</summary>
        public string DetailText { get; set; }

        private bool inList;
        public bool InList
        {
            get => inList;
            set => SetValue(ref inList, value);
        }
    }

    /// <summary>
    /// The page of the list of wishes: search the stores, add, and see the prices of Steam and GOG in the
    /// country of the computer. Atrio looks at them twice a day and tells when one gets cheaper or comes out.
    /// </summary>
    public class WishlistViewModel : ObservableObject
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly DesktopAppViewModel main;
        private CancellationTokenSource search;
        private bool isOpen;

        private List<WishlistRow> items = new List<WishlistRow>();
        public List<WishlistRow> Items
        {
            get => items;
            set
            {
                SetValue(ref items, value);
                OnPropertyChanged(nameof(IsEmpty));
            }
        }

        public bool IsEmpty => items.Count == 0;

        private List<WishlistRow> results = new List<WishlistRow>();
        public List<WishlistRow> Results
        {
            get => results;
            set
            {
                SetValue(ref results, value);
                OnPropertyChanged(nameof(HasResults));
            }
        }

        public bool HasResults => results.Count > 0;

        private string searchText;
        public string SearchText
        {
            get => searchText;
            set => SetValue(ref searchText, value);
        }

        private string statusText;

        /// <summary>What is going on: searching, looking at prices, what the import did.</summary>
        public string StatusText
        {
            get => statusText;
            set => SetValue(ref statusText, value);
        }

        private bool isBusy;
        public bool IsBusy
        {
            get => isBusy;
            set
            {
                SetValue(ref isBusy, value);
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        public RelayCommand<object> SearchCommand { get; }
        public RelayCommand<object> ClearSearchCommand { get; }
        public RelayCommand<WishlistRow> AddCommand { get; }
        public RelayCommand<WishlistRow> RemoveCommand { get; }
        public RelayCommand<WishlistRow> OpenCommand { get; }
        public RelayCommand<object> ImportSteamCommand { get; }
        public RelayCommand<object> RefreshCommand { get; }

        public WishlistViewModel(DesktopAppViewModel main)
        {
            this.main = main;
            SearchCommand = new RelayCommand<object>(_ => Search());
            ClearSearchCommand = new RelayCommand<object>(_ =>
            {
                search?.Cancel();
                SearchText = string.Empty;
                StatusText = null;
                Results = new List<WishlistRow>();
            });
            AddCommand = new RelayCommand<WishlistRow>(a =>
            {
                if (a?.Item != null)
                {
                    Wishlist.Add(a.Item);
                    a.InList = true;
                }
            });
            RemoveCommand = new RelayCommand<WishlistRow>(a =>
            {
                if (a?.Item != null)
                {
                    Wishlist.Remove(a.Item.Key);
                    foreach (var found in Results.Where(b => b.Item.Key == a.Item.Key))
                    {
                        found.InList = false;
                    }
                }
            });
            OpenCommand = new RelayCommand<WishlistRow>(a =>
            {
                if (a?.Item != null)
                {
                    GlobalCommands.NavigateUrl(a.Item.StoreUrl);
                }
            });
            ImportSteamCommand = new RelayCommand<object>(_ => ImportSteam(), _ => !IsBusy);
            RefreshCommand = new RelayCommand<object>(_ => RefreshPrices(), _ => !IsBusy);

            // Many changes in a row make the page once, when the window has a moment.
            Wishlist.Changed += (_, __) =>
            {
                if (System.Threading.Interlocked.Exchange(ref loadPending, 1) == 1)
                {
                    return;
                }

                Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    System.Threading.Interlocked.Exchange(ref loadPending, 0);
                    if (isOpen)
                    {
                        Load();
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            };
        }

        private int loadPending;

        public void OnShown()
        {
            isOpen = true;
            Load();

            // Prices older than half a day are asked again when the page opens.
            if (Wishlist.GetItems().Any(a => a.Checked == null || DateTime.UtcNow - a.Checked > TimeSpan.FromHours(12)))
            {
                RefreshPrices();
            }
        }

        public void OnHidden()
        {
            isOpen = false;
        }

        public static string GetDetail(WishlistItem item, DateTime nowUtc)
        {
            var culture = Localization.ApplicationLanguageCultureInfo;
            var parts = new List<string>();
            if (item.Discount > 0 && item.DiscountEnds > nowUtc)
            {
                parts.Add(string.Format(ResourceProvider.GetString("LOCAtrioWishUntil"), item.DiscountEnds.Value.ToLocalTime().ToString("d MMM", culture)));
            }

            if (item.ComingSoon && item.ReleaseDate > nowUtc)
            {
                parts.Add(string.Format(ResourceProvider.GetString("LOCAtrioWishRelease"), item.ReleaseDate.Value.ToLocalTime().ToString("d MMM yyyy", culture)));
            }

            if (item.Lowest != null && item.Price != null && item.Lowest < item.Price && !item.LowestText.IsNullOrEmpty())
            {
                parts.Add(string.Format(ResourceProvider.GetString("LOCAtrioWishLowest"), item.LowestText));
            }

            return string.Join(" · ", parts);
        }

        private void Load()
        {
            var now = DateTime.UtcNow;
            Items = Wishlist.GetItems().
                OrderByDescending(a => a.Discount > 0 || a.IsFree).
                ThenByDescending(a => a.Discount).
                ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase).
                Select(a => new WishlistRow { Item = a, InList = true, DetailText = GetDetail(a, now) }).
                ToList();
        }

        private async void Search()
        {
            search?.Cancel();
            var term = SearchText;
            if (term.IsNullOrWhiteSpace())
            {
                Results = new List<WishlistRow>();
                return;
            }

            search = new CancellationTokenSource();
            var token = search.Token;
            StatusText = ResourceProvider.GetString("LOCAtrioWishSearching");
            try
            {
                var found = await Wishlist.SearchAsync(term, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                Results = found.Results.Select(a => new WishlistRow { Item = a, InList = Wishlist.Contains(a.Key), DetailText = GetDetail(a, now) }).ToList();

                // Nothing found means the game isn't there only if both stores answered.
                StatusText = found.Results.Count > 0 ? null :
                    found.Failed > 0 ? ResourceProvider.GetString("LOCAtrioWishOffline") :
                    ResourceProvider.GetString("LOCAtrioWishNothingFound");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, "Failed to search the stores.");
                StatusText = ResourceProvider.GetString("LOCAtrioWishOffline");
            }
        }

        private async void RefreshPrices()
        {
            if (IsBusy || Wishlist.GetItems().Count == 0)
            {
                return;
            }

            IsBusy = true;
            StatusText = ResourceProvider.GetString("LOCAtrioWishChecking");
            try
            {
                // What changed is told like when Atrio looks by itself: looking from the page doesn't swallow it.
                var changes = await Wishlist.RefreshAsync(CancellationToken.None);
                if (changes.Count > 0 && GameUnifier.Current?.Settings.WishlistAlerts != false)
                {
                    Wishlist.Notify?.Invoke(changes);
                }

                StatusText = null;
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, "Failed to look at the prices.");
                StatusText = ResourceProvider.GetString("LOCAtrioWishOffline");
            }
            finally
            {
                IsBusy = false;
                if (isOpen)
                {
                    Load();
                }
            }
        }

        private async void ImportSteam()
        {
            IsBusy = true;
            StatusText = ResourceProvider.GetString("LOCAtrioWishImporting");
            try
            {
                var result = await Wishlist.ImportSteamAsync(CancellationToken.None);
                switch (result.Item1)
                {
                    case WishlistImport.NoSteam:
                        StatusText = ResourceProvider.GetString("LOCAtrioWishImportNoSteam");
                        break;
                    case WishlistImport.Private:
                        StatusText = ResourceProvider.GetString("LOCAtrioWishImportPrivate");
                        break;
                    default:
                        StatusText = string.Format(ResourceProvider.GetString("LOCAtrioWishImported"), result.Item2);
                        break;
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, "Failed to bring the list of Steam.");
                StatusText = ResourceProvider.GetString("LOCAtrioWishOffline");
            }
            finally
            {
                IsBusy = false;
                Load();
            }
        }

        /// <summary>What the notice says of each change.</summary>
        public static string GetNotice(WishlistChange change)
        {
            var row = new WishlistRow { Item = change.Item };
            switch (change.Kind)
            {
                case WishlistChangeKind.Free:
                    return string.Format(ResourceProvider.GetString("LOCAtrioWishNoticeFree"), change.Item.Title, row.StoreName);
                case WishlistChangeKind.Released:
                    return string.Format(ResourceProvider.GetString("LOCAtrioWishNoticeReleased"), change.Item.Title, row.PriceText, row.StoreName);
                default:
                    return change.Item.Discount > 0 ?
                        string.Format(ResourceProvider.GetString("LOCAtrioWishNoticeSale"), change.Item.Title, change.Item.PriceText, change.Item.Discount, row.StoreName) :
                        string.Format(ResourceProvider.GetString("LOCAtrioWishNoticeCheaper"), change.Item.Title, change.Item.PriceText, row.StoreName);
            }
        }
    }
}
