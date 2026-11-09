using Playnite.Common;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    /// <summary>A game the person wants, in one store, with what it costs there in the country of the computer.</summary>
    public class WishlistItem
    {
        public const string Steam = "steam";
        public const string Gog = "gog";

        public string Store { get; set; }
        public string StoreId { get; set; }
        public string Title { get; set; }

        /// <summary>The name of its page in GOG.</summary>
        public string Slug { get; set; }
        public string ImageUrl { get; set; }
        public DateTime Added { get; set; }

        /// <summary>Hundredths of the currency, null while unknown.</summary>
        public long? Price { get; set; }
        public long? BasePrice { get; set; }
        public int Discount { get; set; }
        public string PriceText { get; set; }
        public string BasePriceText { get; set; }

        /// <summary>The lowest price Atrio has seen since the game is in the list.</summary>
        public long? Lowest { get; set; }
        public string LowestText { get; set; }
        public bool IsFree { get; set; }
        public bool ComingSoon { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public DateTime? DiscountEnds { get; set; }
        public DateTime? Checked { get; set; }

        /// <summary>The country of the prices: with another one, they aren't compared with the old ones.</summary>
        public string Country { get; set; }

        public string Key => Store + ":" + StoreId;

        public string StoreUrl => Store == Gog ?
            "https://www.gog.com/game/" + (Slug ?? StoreId) :
            "https://store.steampowered.com/app/" + StoreId;

        public WishlistItem Copy()
        {
            return (WishlistItem)MemberwiseClone();
        }
    }

    public enum WishlistChangeKind
    {
        Cheaper,
        Free,
        Released
    }

    public class WishlistChange
    {
        public WishlistItem Item { get; set; }
        public WishlistChangeKind Kind { get; set; }
    }

    public enum WishlistImport
    {
        NoSteam,
        Private,
        Done
    }

    public class WishlistSearch
    {
        public List<WishlistItem> Results { get; set; } = new List<WishlistItem>();

        /// <summary>How many of the two stores didn't answer.</summary>
        public int Failed { get; set; }
    }

    /// <summary>
    /// The list of wishes of Atrio: its own file, with the prices that Steam and GOG show anyone in their
    /// stores, asked twice a day. No account is used; the list of Steam can be brought in when its profile
    /// shows it to everyone.
    /// </summary>
    public static class Wishlist
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        public const string FileName = "atrio-deseos.json";
        private static readonly object sync = new object();
        private static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static List<WishlistItem> items;
        private static System.Threading.Timer timer;
        private static int refreshing;
        private const string SteamImages = "https://shared.akamai.steamstatic.com/store_item_assets/";
        private const ulong SteamId64Base = 76561197960265728;

        public static event EventHandler Changed;

        /// <summary>Set by the program: what to tell when a game of the list gets cheaper or comes out.</summary>
        public static Action<List<WishlistChange>> Notify { get; set; }

        private static string FilePath => Path.Combine(PlaynitePaths.ConfigRootPath, FileName);

        private static List<WishlistItem> Load()
        {
            if (items != null)
            {
                return items;
            }

            try
            {
                if (File.Exists(FilePath))
                {
                    items = Playnite.SDK.Data.Serialization.FromJsonFile<List<WishlistItem>>(FilePath);
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, "Failed to read the list of wishes.");
                try
                {
                    File.Copy(FilePath, FilePath + ".roto", true);
                }
                catch (Exception copy) when (copy is IOException || copy is UnauthorizedAccessException)
                {
                }
            }

            items = items?.Where(a => a != null && !a.Store.IsNullOrEmpty() && !a.StoreId.IsNullOrEmpty()).ToList() ?? new List<WishlistItem>();
            return items;
        }

        /// <summary>A whole new file first, then it takes the place of the old one: a cut never leaves the list empty.</summary>
        private static void Save()
        {
            try
            {
                var temporary = FilePath + ".tmp";
                File.WriteAllText(temporary, Playnite.SDK.Data.Serialization.ToJson(items ?? new List<WishlistItem>(), true));
                if (File.Exists(FilePath))
                {
                    File.Replace(temporary, FilePath, null);
                }
                else
                {
                    File.Move(temporary, FilePath);
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, "Failed to save the list of wishes.");
            }
        }

        public static List<WishlistItem> GetItems()
        {
            lock (sync)
            {
                return Load().Select(a => a.Copy()).ToList();
            }
        }

        public static bool Contains(string key)
        {
            lock (sync)
            {
                return Load().Any(a => a.Key == key);
            }
        }

        public static void Add(WishlistItem item)
        {
            AddRange(new[] { item });
        }

        /// <summary>Several at once, with one save and one notice: a list of a thousand games doesn't write a thousand files.</summary>
        public static int AddRange(IEnumerable<WishlistItem> newItems)
        {
            var count = 0;
            lock (sync)
            {
                var list = Load();
                var keys = new HashSet<string>(list.Select(a => a.Key));
                foreach (var item in newItems ?? Enumerable.Empty<WishlistItem>())
                {
                    if (item == null || item.Store.IsNullOrEmpty() || item.StoreId.IsNullOrEmpty() || !keys.Add(item.Key))
                    {
                        continue;
                    }

                    var added = item.Copy();
                    added.Added = DateTime.UtcNow;
                    added.Country = added.Country ?? GetCountry();
                    added.Lowest = added.Lowest ?? added.Price;
                    added.LowestText = added.LowestText ?? added.PriceText;
                    list.Add(added);
                    count++;
                }

                if (count > 0)
                {
                    Save();
                }
            }

            if (count > 0)
            {
                Changed?.Invoke(null, EventArgs.Empty);
            }

            return count;
        }

        public static void Remove(string key)
        {
            lock (sync)
            {
                if (Load().RemoveAll(a => a.Key == key) == 0)
                {
                    return;
                }

                Save();
            }

            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static string GetCountry() => FreeGames.GetCountry();

        public static string GetCurrency()
        {
            try
            {
                return RegionInfo.CurrentRegion.ISOCurrencySymbol;
            }
            catch (ArgumentException)
            {
                return "USD";
            }
        }

        private static readonly Dictionary<string, CultureInfo> currencyCultures = new Dictionary<string, CultureInfo>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A culture that writes that currency the way its country does, in the language of Atrio if there's one:
        /// "$14.99" for dollars, "14,99 €" for euros. Null when no country uses it.
        /// </summary>
        public static CultureInfo GetCurrencyCulture(string currency, CultureInfo language)
        {
            if (currency.IsNullOrEmpty())
            {
                return null;
            }

            lock (currencyCultures)
            {
                var key = currency + "|" + language?.TwoLetterISOLanguageName;
                if (currencyCultures.TryGetValue(key, out var known))
                {
                    return known;
                }

                var matches = CultureInfo.GetCultures(CultureTypes.SpecificCultures).Where(a =>
                {
                    try
                    {
                        return new RegionInfo(a.Name).ISOCurrencySymbol.Equals(currency, StringComparison.OrdinalIgnoreCase);
                    }
                    catch (ArgumentException)
                    {
                        return false;
                    }
                }).ToList();

                var chosen = matches.FirstOrDefault(a => a.TwoLetterISOLanguageName == language?.TwoLetterISOLanguageName && a.Name.EndsWith("-" + GetCountry(), StringComparison.OrdinalIgnoreCase)) ??
                    matches.FirstOrDefault(a => a.TwoLetterISOLanguageName == language?.TwoLetterISOLanguageName) ??
                    matches.FirstOrDefault(a => a.TwoLetterISOLanguageName == "en") ??
                    matches.FirstOrDefault();
                currencyCultures[key] = chosen;
                return chosen;
            }
        }

        /// <summary>The price as the stores write it in the country of that currency; with the code when no country uses it.</summary>
        public static string FormatPrice(long cents, string currency, CultureInfo culture)
        {
            var money = GetCurrencyCulture(currency, culture);
            if (money == null)
            {
                return (cents / 100m).ToString("0.00", culture) + " " + currency;
            }

            return (cents / 100m).ToString("C", money.NumberFormat);
        }

        private static string SteamLanguage(CultureInfo culture)
        {
            return culture?.TwoLetterISOLanguageName == "es" ? "spanish" : "english";
        }

        /// <summary>
        /// The games that the stores find by that name: Steam first, then GOG. A store that doesn't answer, or takes
        /// too long, counts as failed; what the other found stays. Only the person cancelling stops it all.
        /// </summary>
        public static async Task<WishlistSearch> SearchAsync(string term, CancellationToken token)
        {
            var result = new WishlistSearch();
            if (term.IsNullOrWhiteSpace())
            {
                return result;
            }

            var culture = Localization.ApplicationLanguageCultureInfo;
            var steam = AskAsync($"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term.Trim())}&l={SteamLanguage(culture)}&cc={GetCountry()}", token);
            var gog = AskAsync($"https://catalog.gog.com/v1/catalog?limit=6&query=like:{Uri.EscapeDataString(term.Trim())}&productType=in:game,pack&countryCode={GetCountry()}&currencyCode={GetCurrency()}&locale=en-US", token);
            try
            {
                result.Results.AddRange(ParseSteamSearch(await steam.ConfigureAwait(false), culture));
            }
            catch (Exception e) when (!token.IsCancellationRequested && !(e is OutOfMemoryException))
            {
                logger.Debug(e, "Steam didn't answer the search.");
                result.Failed++;
            }

            try
            {
                result.Results.AddRange(ParseGogSearch(await gog.ConfigureAwait(false)));
            }
            catch (Exception e) when (!token.IsCancellationRequested && !(e is OutOfMemoryException))
            {
                logger.Debug(e, "GOG didn't answer the search.");
                result.Failed++;
            }

            token.ThrowIfCancellationRequested();
            return result;
        }

        private static async Task<string> AskAsync(string url, CancellationToken token)
        {
            using (var response = await client.GetAsync(url, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        public static List<WishlistItem> ParseSteamSearch(string json, CultureInfo culture)
        {
            var result = new List<WishlistItem>();
            var found = Newtonsoft.Json.Linq.JObject.Parse(json ?? "{}")["items"] as Newtonsoft.Json.Linq.JArray;
            foreach (var item in (found ?? new Newtonsoft.Json.Linq.JArray()).OfType<Newtonsoft.Json.Linq.JObject>())
            {
                var id = (string)item["id"];
                var name = (string)item["name"];
                if (id.IsNullOrEmpty() || name.IsNullOrEmpty() || (string)item["type"] != "app")
                {
                    continue;
                }

                var price = item["price"] as Newtonsoft.Json.Linq.JObject;
                var wish = new WishlistItem
                {
                    Store = WishlistItem.Steam,
                    StoreId = id,
                    Title = name,
                    ImageUrl = (string)item["tiny_image"]
                };

                if (price != null)
                {
                    var currency = (string)price["currency"] ?? GetCurrency();
                    wish.Price = (long?)price["final"];
                    wish.BasePrice = (long?)price["initial"];
                    wish.PriceText = wish.Price == null ? null : FormatPrice(wish.Price.Value, currency, culture);
                    wish.BasePriceText = wish.BasePrice == null ? null : FormatPrice(wish.BasePrice.Value, currency, culture);
                    if (wish.BasePrice > 0 && wish.Price < wish.BasePrice)
                    {
                        wish.Discount = (int)Math.Round(100 - wish.Price.Value * 100.0 / wish.BasePrice.Value);
                    }
                }

                result.Add(wish);
            }

            return result;
        }

        private static long? ParseMoney(string amount)
        {
            return decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? (long)Math.Round(value * 100) : (long?)null;
        }

        public static List<WishlistItem> ParseGogSearch(string json)
        {
            var result = new List<WishlistItem>();
            var products = Newtonsoft.Json.Linq.JObject.Parse(json ?? "{}")["products"] as Newtonsoft.Json.Linq.JArray;
            foreach (var product in (products ?? new Newtonsoft.Json.Linq.JArray()).OfType<Newtonsoft.Json.Linq.JObject>())
            {
                var id = (string)product["id"];
                var title = (string)product["title"];
                if (id.IsNullOrEmpty() || title.IsNullOrEmpty())
                {
                    continue;
                }

                var price = product["price"] as Newtonsoft.Json.Linq.JObject;
                var wish = new WishlistItem
                {
                    Store = WishlistItem.Gog,
                    StoreId = id,
                    Title = title,
                    Slug = (string)product["slug"],
                    ImageUrl = (string)product["coverHorizontal"],
                    ComingSoon = (string)product["productState"] == "coming-soon",
                    PriceText = (string)price?["final"],
                    BasePriceText = (string)price?["base"],
                    Price = ParseMoney((string)price?["finalMoney"]?["amount"]),
                    BasePrice = ParseMoney((string)price?["baseMoney"]?["amount"])
                };

                if (wish.BasePrice > 0 && wish.Price < wish.BasePrice)
                {
                    wish.Discount = (int)Math.Round(100 - wish.Price.Value * 100.0 / wish.BasePrice.Value);
                }

                wish.IsFree = wish.Price == 0 && wish.BasePrice == 0;
                result.Add(wish);
            }

            return result;
        }

        /// <summary>What the store of Steam says now of each game asked: its price, its picture and if it's out.</summary>
        public static Dictionary<string, WishlistItem> ParseSteamItems(string json)
        {
            var result = new Dictionary<string, WishlistItem>();
            var found = Newtonsoft.Json.Linq.JObject.Parse(json ?? "{}")["response"]?["store_items"] as Newtonsoft.Json.Linq.JArray;
            foreach (var item in (found ?? new Newtonsoft.Json.Linq.JArray()).OfType<Newtonsoft.Json.Linq.JObject>())
            {
                var id = (string)item["appid"] ?? (string)item["id"];
                if (id == null || (int?)item["success"] != 1)
                {
                    continue;
                }

                var option = item["best_purchase_option"] as Newtonsoft.Json.Linq.JObject;
                var release = item["release"] as Newtonsoft.Json.Linq.JObject;
                var assets = item["assets"] as Newtonsoft.Json.Linq.JObject;
                var wish = new WishlistItem
                {
                    Store = WishlistItem.Steam,
                    StoreId = id,
                    Title = (string)item["name"],
                    IsFree = (bool?)item["is_free"] == true,
                    ComingSoon = (bool?)release?["is_coming_soon"] == true
                };

                var released = (long?)release?["steam_release_date"];
                if (released > 0)
                {
                    wish.ReleaseDate = DateTimeOffset.FromUnixTimeSeconds(released.Value).UtcDateTime;
                }

                var format = (string)assets?["asset_url_format"];
                var header = (string)assets?["header"];
                if (!format.IsNullOrEmpty() && !header.IsNullOrEmpty())
                {
                    wish.ImageUrl = SteamImages + format.Replace("${FILENAME}", header);
                }

                if (option != null)
                {
                    wish.Price = long.TryParse((string)option["final_price_in_cents"], out var final) ? final : (long?)null;
                    wish.BasePrice = long.TryParse((string)option["original_price_in_cents"], out var original) ? original : wish.Price;
                    wish.PriceText = (string)option["formatted_final_price"];
                    wish.BasePriceText = (string)option["formatted_original_price"] ?? wish.PriceText;
                    wish.Discount = (int?)option["discount_pct"] ?? 0;
                    var ends = (option["active_discounts"] as Newtonsoft.Json.Linq.JArray)?.Select(a => (long?)a["discount_end_date"]).FirstOrDefault(a => a > 0);
                    if (ends != null)
                    {
                        wish.DiscountEnds = DateTimeOffset.FromUnixTimeSeconds(ends.Value).UtcDateTime;
                    }
                }
                else if (wish.IsFree)
                {
                    wish.Price = 0;
                    wish.BasePrice = 0;
                }

                result[id] = wish;
            }

            return result;
        }

        /// <summary>"1499 USD", as GOG says the price of one game.</summary>
        public static WishlistItem ParseGogPrices(string json, CultureInfo culture)
        {
            var prices = Newtonsoft.Json.Linq.JObject.Parse(json ?? "{}")["_embedded"]?["prices"] as Newtonsoft.Json.Linq.JArray;
            var first = prices?.FirstOrDefault();
            if (first == null)
            {
                return null;
            }

            long? cents(string text)
            {
                var number = Regex.Match(text ?? string.Empty, @"^\s*(\d+)");
                return number.Success && long.TryParse(number.Groups[1].Value, out var value) ? value : (long?)null;
            }

            var currency = (string)first["currency"]?["code"] ?? GetCurrency();
            var wish = new WishlistItem
            {
                Store = WishlistItem.Gog,
                Price = cents((string)first["finalPrice"]),
                BasePrice = cents((string)first["basePrice"])
            };

            wish.PriceText = wish.Price == null ? null : FormatPrice(wish.Price.Value, currency, culture);
            wish.BasePriceText = wish.BasePrice == null ? null : FormatPrice(wish.BasePrice.Value, currency, culture);
            if (wish.BasePrice > 0 && wish.Price < wish.BasePrice)
            {
                wish.Discount = (int)Math.Round(100 - wish.Price.Value * 100.0 / wish.BasePrice.Value);
            }

            wish.IsFree = wish.Price == 0 && wish.BasePrice == 0;
            return wish;
        }

        /// <summary>
        /// Puts what the store says now into the item, and tells what is worth telling: cheaper than the last time
        /// it was seen, free, or out when it was still coming.
        /// </summary>
        public static WishlistChange Apply(WishlistItem item, WishlistItem now, DateTime nowUtc)
        {
            // Prices of another country (another currency) aren't compared with the old ones: they start again.
            if (!now.Country.IsNullOrEmpty() && !item.Country.IsNullOrEmpty() && now.Country != item.Country)
            {
                item.Price = null;
                item.Lowest = null;
                item.LowestText = null;
            }

            item.Country = now.Country ?? item.Country;
            WishlistChange change = null;
            if (now.Price != null && item.Price != null && now.Price < item.Price)
            {
                change = new WishlistChange { Item = item, Kind = now.Price == 0 ? WishlistChangeKind.Free : WishlistChangeKind.Cheaper };
            }
            else if (item.ComingSoon && !now.ComingSoon && now.Price != null)
            {
                change = new WishlistChange { Item = item, Kind = WishlistChangeKind.Released };
            }

            item.Title = now.Title.IsNullOrEmpty() ? item.Title : now.Title;
            item.ImageUrl = now.ImageUrl ?? item.ImageUrl;
            item.Price = now.Price ?? item.Price;
            item.BasePrice = now.BasePrice ?? item.BasePrice;
            item.PriceText = now.PriceText ?? item.PriceText;
            item.BasePriceText = now.BasePriceText ?? item.BasePriceText;
            item.Discount = now.Price != null ? now.Discount : item.Discount;
            item.DiscountEnds = now.DiscountEnds;
            item.IsFree = now.IsFree;
            item.ComingSoon = now.ComingSoon;
            item.ReleaseDate = now.ReleaseDate ?? item.ReleaseDate;
            item.Checked = nowUtc;
            if (item.Price != null && (item.Lowest == null || item.Price < item.Lowest))
            {
                item.Lowest = item.Price;
                item.LowestText = item.PriceText;
            }

            return change;
        }

        /// <summary>The prices of every game of the list, now. Returns what changed in a way worth telling.</summary>
        public static async Task<List<WishlistChange>> RefreshAsync(CancellationToken token)
        {
            var changes = new List<WishlistChange>();
            if (Interlocked.Exchange(ref refreshing, 1) == 1)
            {
                return changes;
            }

            try
            {
                var list = GetItems();
                var culture = Localization.ApplicationLanguageCultureInfo;
                var country = GetCountry();
                var steam = list.Where(a => a.Store == WishlistItem.Steam && int.TryParse(a.StoreId, out _)).ToList();
                var fresh = new Dictionary<string, WishlistItem>();
                for (var i = 0; i < steam.Count; i += 100)
                {
                    var part = steam.Skip(i).Take(100).ToList();
                    var request = Playnite.SDK.Data.Serialization.ToJson(new
                    {
                        ids = part.Select(a => new { appid = int.Parse(a.StoreId) }).ToList(),
                        context = new { language = SteamLanguage(culture), country_code = country },
                        data_request = new { include_release = true, include_assets = true }
                    });

                    try
                    {
                        var json = await AskAsync("https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(request), token).ConfigureAwait(false);
                        foreach (var pair in ParseSteamItems(json))
                        {
                            pair.Value.Country = country;
                            fresh[WishlistItem.Steam + ":" + pair.Key] = pair.Value;
                        }
                    }
                    catch (Exception e) when (!token.IsCancellationRequested && !(e is OutOfMemoryException))
                    {
                        logger.Debug(e, "Steam didn't say the prices of the list.");
                    }
                }

                foreach (var item in list.Where(a => a.Store == WishlistItem.Gog))
                {
                    try
                    {
                        var json = await AskAsync($"https://api.gog.com/products/{Uri.EscapeDataString(item.StoreId)}/prices?countryCode={country}", token).ConfigureAwait(false);

                        // Without a price yet it's still looked at: the page doesn't ask again every time it opens. And GOG
                        // only tells prices: «it's out» is never said of a game of GOG, a preorder would look like one.
                        var now = ParseGogPrices(json, culture) ?? new WishlistItem { Store = WishlistItem.Gog };
                        now.ComingSoon = item.ComingSoon;
                        now.Country = country;
                        fresh[item.Key] = now;
                    }
                    catch (Exception e) when (!token.IsCancellationRequested && !(e is OutOfMemoryException))
                    {
                        logger.Debug(e, $"GOG didn't say the price of {item.Title}.");
                    }

                    await Task.Delay(500, token).ConfigureAwait(false);
                }

                lock (sync)
                {
                    foreach (var item in Load())
                    {
                        if (fresh.TryGetValue(item.Key, out var now))
                        {
                            var change = Apply(item, now, DateTime.UtcNow);
                            if (change != null)
                            {
                                changes.Add(new WishlistChange { Item = item.Copy(), Kind = change.Kind });
                            }
                        }
                    }

                    Save();
                }

                Changed?.Invoke(null, EventArgs.Empty);
            }
            finally
            {
                Interlocked.Exchange(ref refreshing, 0);
            }

            return changes;
        }

        /// <summary>
        /// The account of Steam used last on this computer, as its client keeps it. Null without one. The names
        /// inside each block are skipped as whole strings: a public name can have braces in it.
        /// </summary>
        public static string FindSteamId(string loginUsers)
        {
            string chosen = null;
            foreach (Match match in Regex.Matches(loginUsers ?? string.Empty, "\"(\\d{17})\"\\s*\\{((?:[^{}\"]|\"(?:[^\"\\\\]|\\\\.)*\")*)\\}"))
            {
                if (!ulong.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= SteamId64Base)
                {
                    continue;
                }

                if (chosen == null || Regex.IsMatch(match.Groups[2].Value, "\"MostRecent\"\\s*\"1\""))
                {
                    chosen = match.Groups[1].Value;
                }
            }

            return chosen;
        }

        public static List<string> ParseSteamWishlist(string json)
        {
            var found = Newtonsoft.Json.Linq.JObject.Parse(json ?? "{}")["response"]?["items"] as Newtonsoft.Json.Linq.JArray;
            return found?.Select(a => (string)a["appid"]).Where(a => !a.IsNullOrEmpty()).ToList() ?? new List<string>();
        }

        /// <summary>
        /// The list of Steam of the account used last on this computer, if its profile shows it to everyone: the
        /// same question the page of the list asks. The account and its key are not used.
        /// </summary>
        public static async Task<Tuple<WishlistImport, int>> ImportSteamAsync(CancellationToken token)
        {
            // The account signed in to the client now; without one, the one used last.
            string steamId = null;
            var active = SteamClientFiles.ActiveAccount;
            if (active > 0)
            {
                steamId = (SteamId64Base + active).ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                var folder = SteamClientFiles.Folder;
                var file = folder.IsNullOrEmpty() ? null : Path.Combine(folder, "config", "loginusers.vdf");
                steamId = file != null && File.Exists(file) ? FindSteamId(File.ReadAllText(file)) : null;
            }

            if (steamId == null)
            {
                return Tuple.Create(WishlistImport.NoSteam, 0);
            }

            var json = await AskAsync("https://api.steampowered.com/IWishlistService/GetWishlist/v1/?steamid=" + steamId, token).ConfigureAwait(false);
            var ids = ParseSteamWishlist(json);
            if (ids.Count == 0)
            {
                return Tuple.Create(WishlistImport.Private, 0);
            }

            var known = new HashSet<string>(GetItems().Select(a => a.Key));
            var missing = ids.Where(a => !known.Contains(WishlistItem.Steam + ":" + a) && int.TryParse(a, out _)).Distinct().ToList();
            var culture = Localization.ApplicationLanguageCultureInfo;
            var country = GetCountry();
            var found = new List<WishlistItem>();
            for (var i = 0; i < missing.Count; i += 100)
            {
                var part = missing.Skip(i).Take(100).ToList();
                var request = Playnite.SDK.Data.Serialization.ToJson(new
                {
                    ids = part.Select(a => new { appid = int.Parse(a) }).ToList(),
                    context = new { language = SteamLanguage(culture), country_code = country },
                    data_request = new { include_release = true, include_assets = true }
                });

                var answer = await AskAsync("https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(request), token).ConfigureAwait(false);
                foreach (var item in ParseSteamItems(answer).Values.Where(a => !a.Title.IsNullOrEmpty()))
                {
                    item.Checked = DateTime.UtcNow;
                    item.Country = country;
                    found.Add(item);
                }
            }

            // All of them in one go: one save of the list and one notice to the page.
            return Tuple.Create(WishlistImport.Done, AddRange(found));
        }

        /// <summary>Two minutes after the start and then every twelve hours, while the list has something.</summary>
        public static void Attach(Func<bool> alertsOn)
        {
            if (timer != null || AtrioInfo.GetTestSetting("ATRIO_SIN_RED") != null)
            {
                return;
            }

            timer = new System.Threading.Timer(_ =>
            {
                try
                {
                    // In the mode for the couch nobody shows the notices: the prices wait for the desktop, so none is lost.
                    if (GetItems().Count == 0 || Notify == null)
                    {
                        return;
                    }

                    var changes = RefreshAsync(CancellationToken.None).Result;
                    if (changes.Count > 0 && alertsOn())
                    {
                        Notify?.Invoke(changes);
                    }
                }
                catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Debug(e, "Failed to look at the prices of the list of wishes.");
                }
            }, null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(12));
        }
    }
}
