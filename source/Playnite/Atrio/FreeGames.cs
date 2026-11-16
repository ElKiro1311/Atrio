using Playnite.Common;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using JArray = Newtonsoft.Json.Linq.JArray;
using JObject = Newtonsoft.Json.Linq.JObject;
using JToken = Newtonsoft.Json.Linq.JToken;
using JTokenType = Newtonsoft.Json.Linq.JTokenType;

namespace Playnite.Atrio
{
    /// <summary>What a game that is free for a while lets the person do.</summary>
    public enum FreeGameKind
    {
        /// <summary>Taken while the offer lasts, it stays in the account for good.</summary>
        Keep = 0,

        /// <summary>It can be played for nothing for a few days, like a free weekend, but it isn't kept.</summary>
        Trial = 1
    }

    /// <summary>A game that costs money and that a store gives, or lets play, for nothing for a while: now, or next.</summary>
    public class FreeGame
    {
        public const string Epic = "epic";
        public const string Steam = "steam";
        public const string Gog = "gog";

        public string Title { get; set; }
        public string Url { get; set; }
        public string ImageUrl { get; set; }
        public DateTime Start { get; set; }

        /// <summary>When it stops being free. Null when the store doesn't say it.</summary>
        public DateTime? End { get; set; }

        /// <summary>It can be taken now; otherwise it's the next one.</summary>
        public bool IsNow { get; set; }

        /// <summary>The store that gives it. The lists saved before only had Epic, and don't say it.</summary>
        public string Store { get; set; } = Epic;

        /// <summary>Its id in that store: the app of Steam, the product of GOG, the page of Epic.</summary>
        public string StoreId { get; set; }

        public FreeGameKind Kind { get; set; }

        /// <summary>What it costs when it isn't free, as the store writes it: "14,99 US$".</summary>
        public string PriceText { get; set; }

        /// <summary>When Atrio saw it in the store: one without an end is shown only for a while after that.</summary>
        public DateTime Seen { get; set; }
    }

    /// <summary>A headline of GG.deals about games given away, from its public feed.</summary>
    public class FreeGameNews
    {
        public string Title { get; set; }
        public string Url { get; set; }
        public string ImageUrl { get; set; }
        public DateTime Published { get; set; }

        /// <summary>A gift or a game to try for a while; null for a summary of several.</summary>
        public FreeGameKind? Kind { get; set; }

        /// <summary>The day the offer ends, when the headline says it ("until November 16"). Null if it doesn't.</summary>
        public DateTime? Ends { get; set; }
    }

    /// <summary>What the start shows: the games, and the latest headlines.</summary>
    public class FreeGamesList
    {
        public List<FreeGame> Games { get; set; } = new List<FreeGame>();
        public List<FreeGameNews> News { get; set; } = new List<FreeGameNews>();
    }

    /// <summary>What the person wants to see, from the settings of Atrio.</summary>
    public class FreeGamesOptions
    {
        /// <summary>Games that can be played for nothing for a few days, like the free weekends of Steam.</summary>
        public bool Trials { get; set; } = true;

        /// <summary>The headlines of GG.deals.</summary>
        public bool News { get; set; } = true;

        /// <summary>The apps of Steam the person wants: a free weekend of one of them matters the most.</summary>
        public List<string> SteamWishes { get; set; } = new List<string>();

        /// <summary>Lists asked with other options don't serve: a source that was off wasn't asked.</summary>
        public string Key => (Trials ? "t" : "-") + (News ? "n" : "-");
    }

    /// <summary>
    /// Games that cost money and are free for a while, as the stores themselves tell anyone: the gifts of Epic, Steam
    /// and GOG, which stay in the account, the free weekends of Steam, which don't, and the headlines of the public
    /// feed of free games of GG.deals. No account is used. Atrio only shows them and opens their page: taking one is
    /// done by the person, in the store, with their account.
    /// </summary>
    /// <remarks>
    /// SteamDB doesn't let programs read its pages, and sends them to Steam, where its list of free promotions comes
    /// from: Atrio asks Steam the same thing. GG.deals has an API with a key for each person and without a list of
    /// gifts, and its terms don't let programs read its pages; its feed of free games is public, made for readers of
    /// news, and Atrio reads it like one, every few hours.
    /// </remarks>
    public static class FreeGames
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string EpicUrl = "https://store-site-backend-static-ipv4.ak.epicgames.com/freeGamesPromotions?locale={0}&country={1}&allowCountries={1}";
        private const string SteamApi = "https://api.steampowered.com/";
        private const string SteamStore = "https://store.steampowered.com/";
        private const string SteamImages = "https://shared.akamai.steamstatic.com/store_item_assets/";
        private const string GogCatalog = "https://catalog.gog.com/v1/catalog?limit=48&order=desc:trending&discounted=eq:true&price=between:0,0&productType=in:game,pack&page=1&countryCode={0}&currencyCode={1}&locale=en-US";
        private const string GogSearch = "https://catalog.gog.com/v1/catalog?limit=10&query=like:{2}&productType=in:game,pack&page=1&countryCode={0}&currencyCode={1}&locale=en-US";
        private const string GogSections = "https://sections.gog.com/v1/pages/2f";
        private const string GogSectionQuery = "?countryCode={0}&currencyCode={1}&locale=en-US";

        /// <summary>A gift of GOG is taken on the banner of its home page.</summary>
        public const string GogGiveawayPage = "https://www.gog.com/en/#giveaway";
        public const string NewsFeedUrl = "https://gg.deals/news/freebies/feed/";
        public const string NewsPageUrl = "https://gg.deals/news/freebies/";
        public const string SteamDbPageUrl = "https://steamdb.info/upcoming/free/";
        public const string FileName = "atrio-gratis.json";

        /// <summary>At most this many headlines, of the last week.</summary>
        public const int MaxNews = 10;

        /// <summary>The stores are asked again after this. If one doesn't answer, sooner.</summary>
        private static readonly TimeSpan lifetime = TimeSpan.FromHours(3);
        private static readonly TimeSpan retry = TimeSpan.FromMinutes(30);

        /// <summary>A gift whose end the store doesn't say is shown for this long after it was seen.</summary>
        private static readonly TimeSpan withoutEnd = TimeSpan.FromHours(12);
        private static readonly TimeSpan newsAge = TimeSpan.FromDays(7);

        /// <summary>A game to try is free a few days, from Thursday to Monday: its headline doesn't stay the whole week.</summary>
        private static readonly TimeSpan trialNewsAge = TimeSpan.FromDays(4);

        /// <summary>Apps of Steam asked about in one go, and in all: the list of wishes can be long.</summary>
        private const int SteamChunk = 100;
        private const int SteamMaxApps = 500;

        private static readonly HttpClient client = CreateClient();
        private static readonly object sync = new object();
        private static SavedList last;
        private static Task<FreeGamesList> running;
        private static string runningKey;

        private class SavedList
        {
            public DateTime Time { get; set; }

            /// <summary>The options it was asked with. Lists saved before don't say it, and are asked again.</summary>
            public string Options { get; set; }

            /// <summary>When it has to be asked again: before the usual time if a store didn't answer.</summary>
            public DateTime? Expires { get; set; }
            public List<FreeGame> Games { get; set; }
            public List<FreeGameNews> News { get; set; }
        }

        private static HttpClient CreateClient()
        {
            var created = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            created.DefaultRequestHeaders.UserAgent.ParseAdd($"Atrio/{AtrioInfo.Version}");
            return created;
        }

        private static string FilePath => Path.Combine(PlaynitePaths.ConfigRootPath, FileName);

        /// <summary>"es-ES" for the store when Atrio is in Spanish: its pages and names come in that language.</summary>
        public static string GetStoreLocale(CultureInfo culture)
        {
            var name = culture?.Name;
            return name.IsNullOrEmpty() || !name.Contains("-") ? "en-US" : name;
        }

        /// <summary>The language of the pages of Steam: it only has some, and calls them by their English name.</summary>
        public static string GetSteamLanguage(CultureInfo culture)
        {
            return culture?.TwoLetterISOLanguageName == "es" ? "spanish" : "english";
        }

        /// <summary>
        /// The country of the computer: the gifts and the prices change from one to another. The one Windows has as
        /// «Country or region», and the one of the format of dates and numbers if it doesn't say.
        /// </summary>
        public static string GetCountry()
        {
            try
            {
                var geo = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\International\Geo", "Name", null) as string;
                if (!geo.IsNullOrEmpty() && geo.Length == 2 && geo.All(char.IsLetter))
                {
                    return geo.ToUpperInvariant();
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException || e is UnauthorizedAccessException || e is System.IO.IOException)
            {
                logger.Debug(e, "Failed to read the country of Windows.");
            }

            try
            {
                var region = RegionInfo.CurrentRegion.TwoLetterISORegionName;
                return region.Length == 2 ? region.ToUpperInvariant() : "US";
            }
            catch (ArgumentException)
            {
                return "US";
            }
        }

        private static SavedList Load()
        {
            lock (sync)
            {
                if (last != null)
                {
                    return last;
                }
            }

            try
            {
                if (File.Exists(FilePath))
                {
                    var saved = Playnite.SDK.Data.Serialization.FromJsonFile<SavedList>(FilePath);
                    if (saved != null)
                    {
                        saved.Games = saved.Games?.Where(a => a != null && !a.Title.IsNullOrWhiteSpace()).ToList() ?? new List<FreeGame>();
                        saved.News = saved.News?.Where(a => a != null && !a.Title.IsNullOrWhiteSpace()).ToList() ?? new List<FreeGameNews>();
                        lock (sync)
                        {
                            last = last ?? saved;
                            return last;
                        }
                    }
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Debug(e, "Failed to read the free games saved.");
            }

            return null;
        }

        /// <summary>A gift that was next becomes the one of now when its day comes.</summary>
        private static FreeGame Refresh(FreeGame game, DateTime nowUtc)
        {
            game.IsNow = game.Start <= nowUtc && (game.End == null || nowUtc < game.End);
            return game;
        }

        /// <summary>Still free, or still to come: what has an end until then, what hasn't one for a while after it was seen.</summary>
        public static bool IsCurrent(FreeGame game, DateTime nowUtc)
        {
            if (game.End != null)
            {
                return game.End > nowUtc;
            }

            return nowUtc - game.Seen < withoutEnd && game.Seen <= nowUtc.AddMinutes(5);
        }

        /// <summary>Of the saved list, what the person wants to see and is still on.</summary>
        private static FreeGamesList Filter(SavedList list, DateTime nowUtc, FreeGamesOptions options)
        {
            var result = new FreeGamesList();
            if (list == null)
            {
                return result;
            }

            result.Games = Sort((list.Games ?? new List<FreeGame>()).
                Where(a => IsCurrent(a, nowUtc) && (options.Trials || a.Kind != FreeGameKind.Trial)).
                Select(a => Refresh(Copy(a), nowUtc)));
            if (options.News)
            {
                result.News = (list.News ?? new List<FreeGameNews>()).
                    Where(a => IsNewsVisible(a, nowUtc, options)).
                    OrderByDescending(a => a.Published).
                    Take(MaxNews).
                    ToList();
            }

            return result;
        }

        /// <summary>
        /// A headline is shown during the week it came out, while the offer it speaks of lasts if it says until when,
        /// and only if the person wants to see games to try when that's what it speaks of.
        /// </summary>
        public static bool IsNewsVisible(FreeGameNews news, DateTime nowUtc, FreeGamesOptions options)
        {
            if (news == null || nowUtc - news.Published >= newsAge || (news.Ends != null && news.Ends <= nowUtc))
            {
                return false;
            }

            if (news.Kind == FreeGameKind.Trial)
            {
                return options?.Trials != false && (news.Ends != null || nowUtc - news.Published < trialNewsAge);
            }

            return true;
        }

        /// <summary>
        /// What a store gave before and still lasts, but isn't in what it said now: when the store answered only in
        /// part, or not at all, it stays until it ends. A game is the same by its id in the store, or by its name.
        /// </summary>
        public static List<FreeGame> KeepPrevious(IEnumerable<FreeGame> fresh, IEnumerable<FreeGame> previous, string store, DateTime nowUtc)
        {
            var list = (fresh ?? Enumerable.Empty<FreeGame>()).ToList();
            var ids = new HashSet<string>(list.Where(a => !a.StoreId.IsNullOrEmpty()).Select(a => a.StoreId), StringComparer.OrdinalIgnoreCase);
            var titles = new HashSet<string>(list.Select(a => a.Title ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            return (previous ?? Enumerable.Empty<FreeGame>()).
                Where(a => a != null && (a.Store ?? FreeGame.Epic) == store && IsCurrent(a, nowUtc) &&
                    (a.StoreId.IsNullOrEmpty() || !ids.Contains(a.StoreId)) && !titles.Contains(a.Title ?? string.Empty)).
                ToList();
        }

        private static FreeGame Copy(FreeGame game)
        {
            return new FreeGame
            {
                Title = game.Title,
                Url = game.Url,
                ImageUrl = game.ImageUrl,
                Start = game.Start,
                End = game.End,
                IsNow = game.IsNow,
                Store = game.Store ?? FreeGame.Epic,
                StoreId = game.StoreId,
                Kind = game.Kind,
                PriceText = game.PriceText,
                Seen = game.Seen
            };
        }

        /// <summary>Gifts of now first, then the games to try, then what comes next; each by what ends first.</summary>
        public static List<FreeGame> Sort(IEnumerable<FreeGame> games)
        {
            return games.
                GroupBy(a => (a.Store ?? FreeGame.Epic) + ":" + (a.StoreId.IsNullOrEmpty() ? a.Title?.ToLowerInvariant() : a.StoreId)).
                Select(a => a.First()).
                OrderByDescending(a => a.IsNow).
                ThenBy(a => a.Kind).
                ThenBy(a => a.IsNow ? (a.End ?? DateTime.MaxValue) : a.Start).
                ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase).
                ToList();
        }

        /// <summary>The list saved last time, right away, even if it's old. Only what hasn't ended.</summary>
        public static FreeGamesList GetSaved(DateTime nowUtc, FreeGamesOptions options = null)
        {
            try
            {
                return Filter(Load(), nowUtc, options ?? new FreeGamesOptions());
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Debug(e, "Failed to read the free games saved.");
                return new FreeGamesList();
            }
        }

        /// <summary>The next time the stores are asked, they are asked: after a setting changed, for example.</summary>
        public static void Forget()
        {
            lock (sync)
            {
                if (last != null)
                {
                    last.Expires = DateTime.UtcNow;
                }
            }
        }

        /// <summary>
        /// What the stores give now, asked again every few hours; until then, what they said last. Two pages asking
        /// at once share the same questions.
        /// </summary>
        public static Task<FreeGamesList> GetAsync(FreeGamesOptions options, CancellationToken token)
        {
            options = options ?? new FreeGamesOptions();
            var now = DateTime.UtcNow;
            var saved = Load();
            lock (sync)
            {
                if (saved != null && saved.Options == options.Key && saved.Time <= now && now < (saved.Expires ?? saved.Time + lifetime))
                {
                    return Task.FromResult(Filter(saved, now, options));
                }

                if (running != null && !running.IsCompleted && runningKey == options.Key)
                {
                    return running;
                }

                runningKey = options.Key;
                running = Task.Run(() => FetchAsync(options, token));
                return running;
            }
        }

        /// <summary>What a store said. If part of it didn't answer, what it said is only part of what it gives.</summary>
        private class StoreAnswer
        {
            public List<FreeGame> Games { get; set; } = new List<FreeGame>();
            public bool Partial { get; set; }
        }

        private static async Task<T> TryAsync<T>(string name, Func<Task<T>> ask) where T : class
        {
            try
            {
                return await ask().ConfigureAwait(false);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Debug(e, $"{name} didn't say its free games.");
                return null;
            }
        }

        private static async Task<FreeGamesList> FetchAsync(FreeGamesOptions options, CancellationToken token)
        {
            var now = DateTime.UtcNow;
            var culture = Localization.ApplicationLanguageCultureInfo;
            var country = GetCountry();
            var currency = Wishlist.GetCurrency();
            var epic = TryAsync("Epic", () => GetEpicAsync(culture, country, now, token));
            var steam = TryAsync("Steam", () => GetSteamAsync(options, culture, country, now, token));
            var gog = TryAsync("GOG", () => GetGogAsync(country, currency, now, token));
            var news = options.News ? TryAsync("GG.deals", () => GetNewsAsync(now, token)) : Task.FromResult(new List<FreeGameNews>());
            await Task.WhenAll(epic, steam, gog, news).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            // A store that didn't answer, or answered only in part, keeps what it gave last time, while it lasts; and
            // it's asked again sooner. Only a store that answered whole says that what isn't there is over.
            var previous = Load()?.Games ?? new List<FreeGame>();
            var games = new List<FreeGame>();
            var answered = new List<string>();
            var failed = 0;
            var incomplete = 0;
            foreach (var answer in new[] { Tuple.Create(FreeGame.Epic, epic.Result), Tuple.Create(FreeGame.Steam, steam.Result), Tuple.Create(FreeGame.Gog, gog.Result) })
            {
                var found = answer.Item2;
                if (found == null)
                {
                    failed++;
                    games.AddRange(KeepPrevious(null, previous, answer.Item1, now));
                    continue;
                }

                games.AddRange(found.Games);
                if (found.Partial)
                {
                    // In GOG only its home page can fail: what its catalog said is whole, only the gift comes back.
                    incomplete++;
                    games.AddRange(KeepPrevious(found.Games, previous, answer.Item1, now).
                        Where(a => answer.Item1 != FreeGame.Gog || a.Url == GogGiveawayPage));
                }
                else
                {
                    answered.Add(answer.Item1);
                }
            }

            var headlines = news.Result;
            if (headlines == null)
            {
                failed++;
                headlines = Load()?.News ?? new List<FreeGameNews>();
            }

            var list = new SavedList
            {
                Time = now,
                Options = options.Key,
                Expires = failed + incomplete > 0 ? now + retry : (DateTime?)null,
                Games = Sort(games),
                News = RemoveRepeated(headlines, games, answered)
            };

            lock (sync)
            {
                last = list;
            }

            // Without internet nothing answers: what was there stays, and nobody is asked again for a while.
            if (failed == (options.News ? 4 : 3))
            {
                throw new HttpRequestException("No store said its free games.");
            }

            try
            {
                FileSystem.WriteStringToFile(FilePath, Playnite.SDK.Data.Serialization.ToJson(list));
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Debug(e, "Failed to keep the free games.");
            }

            return Filter(list, now, options);
        }

        private static async Task<string> AskAsync(string url, CancellationToken token)
        {
            using (var response = await client.GetAsync(url, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        #region Epic

        private static async Task<StoreAnswer> GetEpicAsync(CultureInfo culture, string country, DateTime nowUtc, CancellationToken token)
        {
            var locale = GetStoreLocale(culture);
            var json = await AskAsync(string.Format(EpicUrl, locale, country), token).ConfigureAwait(false);
            return new StoreAnswer { Games = Parse(json, nowUtc, locale) };
        }

        private static DateTime? ReadDate(JToken token)
        {
            if (token == null)
            {
                return null;
            }

            if (token.Type == JTokenType.Date)
            {
                return ((DateTime)token).ToUniversalTime();
            }

            return token.Type == JTokenType.String &&
                DateTime.TryParse((string)token, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date) ? date : (DateTime?)null;
        }

        private static JObject Obj(JToken token) => token as JObject;

        private static JArray List(JToken token) => token as JArray ?? new JArray();

        private static string Text(JToken token) => token != null && (token.Type == JTokenType.String || token.Type == JTokenType.Integer) ? (string)token : null;

        /// <summary>
        /// The offers of the answer of Epic where the price goes down to nothing: the one of now, and the next. The
        /// store marks a gift as an offer of 0 % of the price, whatever the game cost.
        /// </summary>
        public static List<FreeGame> Parse(string json, DateTime nowUtc, string locale)
        {
            var result = new List<FreeGame>();
            var elements = JObject.Parse(json)["data"]?["Catalog"]?["searchStore"]?["elements"] as JArray;
            foreach (var element in (elements ?? new JArray()).OfType<JObject>())
            {
                var title = Text(element["title"]);
                if (title.IsNullOrWhiteSpace())
                {
                    continue;
                }

                // Any of these can come as null instead of an object: each step is checked.
                var totalPrice = Obj(Obj(element["price"])?["totalPrice"]);
                FreeGame found = null;
                foreach (var group in new[] { "promotionalOffers", "upcomingPromotionalOffers" })
                {
                    var offers = List(Obj(element["promotions"])?[group]);
                    foreach (var offer in offers.OfType<JObject>().SelectMany(a => List(a["promotionalOffers"]).OfType<JObject>()))
                    {
                        var start = ReadDate(offer["startDate"]);
                        var end = ReadDate(offer["endDate"]);
                        var percent = (int?)Obj(offer["discountSetting"])?["discountPercentage"];
                        if (start == null || end == null || percent != 0 || end <= nowUtc)
                        {
                            continue;
                        }

                        // The offer of now has to leave the game at nothing; the next one only says it will.
                        var isNow = start <= nowUtc;
                        if (isNow && ((long?)totalPrice?["discountPrice"] ?? 0) != 0)
                        {
                            continue;
                        }

                        found = new FreeGame { Title = title.Trim(), Start = start.Value, End = end.Value, IsNow = isNow, Store = FreeGame.Epic, Seen = nowUtc };
                        break;
                    }

                    if (found != null)
                    {
                        break;
                    }
                }

                if (found == null)
                {
                    continue;
                }

                string slugOf(JToken token) => List(token).OfType<JObject>().Select(a => Text(a["pageSlug"])).FirstOrDefault(a => !a.IsNullOrEmpty());
                var slug = slugOf(Obj(element["catalogNs"])?["mappings"]) ?? slugOf(element["offerMappings"]) ?? Text(element["productSlug"]);
                found.StoreId = slug?.Trim('/');
                found.Url = slug.IsNullOrEmpty() ? $"https://store.epicgames.com/{locale}/free-games" : $"https://store.epicgames.com/{locale}/p/{slug.Trim('/')}";
                var images = List(element["keyImages"]).OfType<JObject>().ToList();
                found.ImageUrl = new[] { "OfferImageWide", "DieselStoreFrontWide", "Thumbnail", "OfferImageTall" }.
                    Select(type => images.FirstOrDefault(a => Text(a["type"]) == type)?["url"]).
                    Select(Text).
                    FirstOrDefault(a => !a.IsNullOrEmpty());
                if (((long?)totalPrice?["originalPrice"] ?? 0) > 0)
                {
                    var written = Text(Obj(totalPrice["fmtPrice"])?["originalPrice"]);
                    found.PriceText = written.IsNullOrWhiteSpace() || written == "0" ? null : written.Trim();
                }

                result.Add(found);
            }

            return result.
                GroupBy(a => a.Title, StringComparer.OrdinalIgnoreCase).
                Select(a => a.First()).
                OrderByDescending(a => a.IsNow).
                ThenBy(a => a.Start).
                ToList();
        }

        #endregion Epic

        #region Steam

        /// <summary>
        /// Gifts and free weekends of Steam. The specials at nothing come from its search and from the question of its
        /// store with a minimum discount (it ignores 100, so 99). Free weekends aren't a discount: they are looked for
        /// in the spotlights and offers of its front page, in what sells most, and in the list of wishes of the person.
        /// Then Steam says of each one, in its words, whether it's free to keep and until when, or free to play.
        /// </summary>
        private static async Task<StoreAnswer> GetSteamAsync(FreeGamesOptions options, CultureInfo culture, string country, DateTime nowUtc, CancellationToken token)
        {
            var language = GetSteamLanguage(culture);
            var context = new { language, country_code = country, steam_realm = 1 };
            var found = new List<string>();
            var answered = 0;
            var partial = false;
            async Task find(Func<Task<IEnumerable<string>>> source)
            {
                try
                {
                    found.AddRange(await source().ConfigureAwait(false));
                    answered++;
                }
                catch (Exception e) when (!token.IsCancellationRequested && !(e is OutOfMemoryException))
                {
                    logger.Debug(e, "A list of Steam didn't answer.");
                    partial = true;
                }
            }

            string query(object filters) => SteamApi + "IStoreQueryService/Query/v1/?input_json=" + Uri.EscapeDataString(Playnite.SDK.Data.Serialization.ToJson(new
            {
                query = new { start = 0, count = 100, filters },
                context
            }));

            await find(async () => ParseSteamSearchIds(await AskAsync(
                SteamStore + $"search/results/?query=&start=0&count=100&maxprice=free&specials=1&category1=998&infinite=1&ignore_preferences=1&cc={country}&l={language}", token).ConfigureAwait(false))).ConfigureAwait(false);
            await find(async () => ParseSteamQueryIds(await AskAsync(query(new
            {
                released_only = true,
                type_filters = new { include_games = true },
                price_filters = new { min_discount_percent = 99 }
            }), token).ConfigureAwait(false))).ConfigureAwait(false);

            if (options.Trials)
            {
                await find(async () => ParseSteamFeaturedIds(await AskAsync(SteamStore + $"api/featuredcategories?cc={country}&l={language}", token).ConfigureAwait(false))).ConfigureAwait(false);
                await find(async () => ParseSteamQueryIds(await AskAsync(query(new
                {
                    released_only = true,
                    type_filters = new { include_games = true },
                    regional_top_n_sellers = 100
                }), token).ConfigureAwait(false))).ConfigureAwait(false);
                found.AddRange(options.SteamWishes ?? new List<string>());
            }

            if (answered == 0)
            {
                throw new HttpRequestException("No list of Steam answered.");
            }

            var ids = found.Where(a => uint.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0).Distinct().Take(SteamMaxApps).ToList();
            var result = new List<FreeGame>();
            var chunksAnswered = 0;
            for (var i = 0; i < ids.Count; i += SteamChunk)
            {
                token.ThrowIfCancellationRequested();
                var request = Playnite.SDK.Data.Serialization.ToJson(new
                {
                    ids = ids.Skip(i).Take(SteamChunk).Select(a => new { appid = uint.Parse(a, CultureInfo.InvariantCulture) }).ToList(),
                    context,
                    data_request = new { include_assets = true, include_all_purchase_options = true }
                });

                try
                {
                    var json = await AskAsync(SteamApi + "IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(request), token).ConfigureAwait(false);
                    result.AddRange(ParseSteamItems(json, nowUtc, options.Trials));
                    chunksAnswered++;
                }
                catch (Exception e) when (!token.IsCancellationRequested && !(e is OutOfMemoryException))
                {
                    logger.Debug(e, "Steam didn't say how its games are.");
                    partial = true;
                }
            }

            if (ids.Count > 0 && chunksAnswered == 0)
            {
                throw new HttpRequestException("Steam didn't say how its games are.");
            }

            return new StoreAnswer { Games = result, Partial = partial };
        }

        /// <summary>The apps of an answer of the search of the store of Steam, which comes as a piece of its page.</summary>
        public static List<string> ParseSteamSearchIds(string json)
        {
            var html = Text(JObject.Parse(json ?? "{}")["results_html"]) ?? string.Empty;
            return Regex.Matches(html, "data-ds-appid=\"(\\d+)").Cast<Match>().Select(a => a.Groups[1].Value).Distinct().ToList();
        }

        /// <summary>The apps of an answer of the question of the store of Steam.</summary>
        public static List<string> ParseSteamQueryIds(string json)
        {
            var ids = JObject.Parse(json ?? "{}")["response"]?["ids"] as JArray;
            return List(ids).OfType<JObject>().Select(a => Text(a["appid"])).Where(a => !a.IsNullOrEmpty()).Distinct().ToList();
        }

        /// <summary>The apps that the front page of Steam shows: its spotlights, offers and what sells most.</summary>
        public static List<string> ParseSteamFeaturedIds(string json)
        {
            var result = new List<string>();
            foreach (var group in JObject.Parse(json ?? "{}").Properties().Select(a => a.Value).OfType<JObject>())
            {
                foreach (var item in List(group["items"]).OfType<JObject>())
                {
                    // Type 0 is an app; the spotlights say no id, only the page they open.
                    var id = Text(item["id"]);
                    if (!id.IsNullOrEmpty() && ((int?)item["type"] ?? 0) == 0)
                    {
                        result.Add(id);
                    }

                    var url = Text(item["url"]);
                    var app = url == null ? null : Regex.Match(url, @"store\.steampowered\.com/app/(\d+)");
                    if (app?.Success == true)
                    {
                        result.Add(app.Groups[1].Value);
                    }
                }
            }

            return result.Distinct().ToList();
        }

        private static long? Cents(JToken token)
        {
            var text = Text(token);
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : (long?)null;
        }

        private static DateTime? Seconds(JToken token)
        {
            var value = Cents(token);
            return value > 0 ? DateTimeOffset.FromUnixTimeSeconds(value.Value).UtcDateTime : (DateTime?)null;
        }

        private static string SteamImage(JObject assets)
        {
            var format = Text(assets?["asset_url_format"]);
            var file = Text(assets?["main_capsule"]) ?? Text(assets?["header"]);
            return format.IsNullOrEmpty() || file.IsNullOrEmpty() ? null : SteamImages + format.Replace("${FILENAME}", file);
        }

        /// <summary>
        /// What Steam says of each game asked: free to keep until a day (a way to buy it that costs nothing and stays,
        /// or the whole price taken off), or free to play for some days (a free weekend). The rest isn't free.
        /// </summary>
        public static List<FreeGame> ParseSteamItems(string json, DateTime nowUtc, bool trials)
        {
            var result = new List<FreeGame>();
            var items = JObject.Parse(json ?? "{}")["response"]?["store_items"] as JArray;
            foreach (var item in List(items).OfType<JObject>())
            {
                var id = Text(item["appid"]);
                var name = Text(item["name"]);
                if (id.IsNullOrEmpty() || name.IsNullOrWhiteSpace() || (int?)item["success"] != 1 || (bool?)item["visible"] == false)
                {
                    continue;
                }

                var best = Obj(item["best_purchase_option"]);
                var ways = new List<JObject>();
                if (best != null)
                {
                    ways.Add(best);
                }

                ways.AddRange(List(item["purchase_options"]).OfType<JObject>());
                var gift = ways.FirstOrDefault(a => (bool?)a["is_free_to_keep"] == true) ??
                    ways.FirstOrDefault(a => (int?)a["discount_pct"] == 100 && Cents(a["final_price_in_cents"]) == 0 && Cents(a["original_price_in_cents"]) > 0);
                var weekend = Obj(item["free_weekend"]);
                FreeGame game = null;
                if (gift != null || ((bool?)item["is_free_temporarily"] == true && weekend == null))
                {
                    var end = Seconds(gift?["free_to_keep_ends"]) ??
                        List(gift?["active_discounts"]).OfType<JObject>().Select(a => Seconds(a["discount_end_date"])).Where(a => a != null).Min();
                    if (end != null && end <= nowUtc)
                    {
                        continue;
                    }

                    var price = Text(gift?["formatted_original_price"]) ?? Text(best?["formatted_original_price"]);
                    game = new FreeGame { Kind = FreeGameKind.Keep, Start = nowUtc, End = end, IsNow = true, PriceText = price };
                }
                else if (trials && weekend != null)
                {
                    var start = Seconds(weekend["start_time"]);
                    var end = Seconds(weekend["end_time"]);
                    if (start == null || end == null || end <= nowUtc || end <= start)
                    {
                        continue;
                    }

                    game = new FreeGame { Kind = FreeGameKind.Trial, Start = start.Value, End = end, IsNow = start <= nowUtc, PriceText = Text(best?["formatted_final_price"]) };
                }

                if (game == null)
                {
                    continue;
                }

                game.Store = FreeGame.Steam;
                game.StoreId = id;
                game.Title = name.Trim();
                game.Seen = nowUtc;
                var path = Text(item["store_url_path"]);
                game.Url = SteamStore + (path.IsNullOrEmpty() || !Regex.IsMatch(path, @"^app/\d+(/[\w\-]*)?$") ? "app/" + id + "/" : path);
                game.ImageUrl = SteamImage(Obj(item["assets"]));
                result.Add(game);
            }

            return result;
        }

        #endregion Steam

        #region GOG

        /// <summary>
        /// The gift of the home page of GOG, when there is one, and the games of its catalog with the whole price
        /// taken off. Neither needs an account; the catalog doesn't say until when.
        /// </summary>
        private static async Task<StoreAnswer> GetGogAsync(string country, string currency, DateTime nowUtc, CancellationToken token)
        {
            var result = new List<FreeGame>();
            var partial = false;
            var catalog = await AskAsync(string.Format(GogCatalog, country, currency), token).ConfigureAwait(false);
            result.AddRange(ParseGogCatalog(catalog, nowUtc));
            try
            {
                var query = string.Format(GogSectionQuery, country, currency);
                var section = ParseGogGiveawaySection(await AskAsync(GogSections + query, token).ConfigureAwait(false));
                if (section != null)
                {
                    var gift = ParseGogGiveaway(await AskAsync(GogSections + "/sections/" + Uri.EscapeDataString(section) + query, token).ConfigureAwait(false), nowUtc);
                    if (gift != null)
                    {
                        // The catalog says what it costs, by its name.
                        try
                        {
                            var same = ParseGogProducts(await AskAsync(string.Format(GogSearch, country, currency, Uri.EscapeDataString(gift.Title)), token).ConfigureAwait(false)).
                                FirstOrDefault(a => !gift.StoreId.IsNullOrEmpty() && a.Item1 == gift.StoreId);
                            gift.PriceText = same?.Item2;
                        }
                        catch (Exception e) when (!token.IsCancellationRequested && !(e is OutOfMemoryException))
                        {
                            logger.Debug(e, "GOG didn't say the price of its gift.");
                        }

                        result.RemoveAll(a => a.StoreId == gift.StoreId || string.Equals(a.Title, gift.Title, StringComparison.OrdinalIgnoreCase));
                        result.Insert(0, gift);
                    }
                }
            }
            catch (Exception e) when (!token.IsCancellationRequested && !(e is OutOfMemoryException))
            {
                // Its gift is only told by its home page: without it, the one seen before stays.
                logger.Debug(e, "GOG didn't say its gift.");
                partial = true;
            }

            return new StoreAnswer { Games = result, Partial = partial };
        }

        private static decimal? Money(JToken money)
        {
            var amount = Text(Obj(money)?["amount"]);
            return decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : (decimal?)null;
        }

        /// <summary>The products of an answer of the catalog of GOG: their name in its pages, and their whole price.</summary>
        private static List<Tuple<string, string>> ParseGogProducts(string json)
        {
            var products = JObject.Parse(json ?? "{}")["products"] as JArray;
            return List(products).OfType<JObject>().
                Select(a => Tuple.Create(Text(a["slug"]), Text(Obj(a["price"])?["base"]))).
                Where(a => !a.Item1.IsNullOrEmpty()).
                ToList();
        }

        /// <summary>The games of the catalog of GOG that cost nothing now and cost something before.</summary>
        public static List<FreeGame> ParseGogCatalog(string json, DateTime nowUtc)
        {
            var result = new List<FreeGame>();
            var products = JObject.Parse(json ?? "{}")["products"] as JArray;
            foreach (var product in List(products).OfType<JObject>())
            {
                var id = Text(product["id"]);
                var title = Text(product["title"]);
                var price = Obj(product["price"]);
                if (id.IsNullOrEmpty() || title.IsNullOrWhiteSpace() || Money(price?["finalMoney"]) != 0 || !(Money(price?["baseMoney"]) > 0))
                {
                    continue;
                }

                var link = Text(product["storeLink"]);
                var slug = Text(product["slug"]);
                result.Add(new FreeGame
                {
                    Store = FreeGame.Gog,
                    StoreId = id,
                    Title = title.Trim(),
                    Kind = FreeGameKind.Keep,
                    Start = nowUtc,
                    IsNow = true,
                    Seen = nowUtc,
                    Url = link != null && link.StartsWith("https://www.gog.com/", StringComparison.OrdinalIgnoreCase) ? link :
                        "https://www.gog.com/en/game/" + (slug ?? id),
                    ImageUrl = Text(product["coverHorizontal"]),
                    PriceText = Text(price["base"])
                });
            }

            return result;
        }

        /// <summary>The section of the home page of GOG that holds its gift. Null when there's no gift now.</summary>
        public static string ParseGogGiveawaySection(string json)
        {
            var sections = JObject.Parse(json ?? "{}")["sections"] as JArray;
            return List(sections).OfType<JObject>().
                Where(a => Text(a["sectionType"]) == "GIVEAWAY_SECTION").
                Select(a => Text(a["sectionId"])).
                FirstOrDefault(a => !a.IsNullOrEmpty());
        }

        /// <summary>The gift of GOG, as its section says it: the game and when it ends. It's taken on the home page.</summary>
        public static FreeGame ParseGogGiveaway(string json, DateTime nowUtc)
        {
            var properties = Obj(JObject.Parse(json ?? "{}")["properties"]);
            var product = Obj(properties?["product"]);
            var title = Text(product?["title"]);
            if (title.IsNullOrWhiteSpace())
            {
                return null;
            }

            var end = ReadDate(properties["endDate"]);
            if (end != null && end <= nowUtc)
            {
                return null;
            }

            return new FreeGame
            {
                Store = FreeGame.Gog,
                StoreId = Text(product["slug"]),
                Title = title.Trim(),
                Kind = FreeGameKind.Keep,
                Start = nowUtc,
                End = end,
                IsNow = true,
                Seen = nowUtc,
                Url = GogGiveawayPage,
                ImageUrl = Text(product["coverHorizontal"])
            };
        }

        #endregion GOG

        #region GG.deals

        private static async Task<List<FreeGameNews>> GetNewsAsync(DateTime nowUtc, CancellationToken token)
        {
            return ParseNews(await AskAsync(NewsFeedUrl, token).ConfigureAwait(false), nowUtc);
        }

        private const RegexOptions Words = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        /// <summary>Not for the PC, or not a game that costs money: demos, tests, drops, mobile, consoles, subscriptions.</summary>
        private static readonly Regex notForPc = new Regex(
            @"\b(demos?|playtests?|beta|alpha|twitch|drops|mobile|ios|android|iphone|xbox|playstation|ps4|ps5|nintendo|switch 2|consoles?|" +
            @"prime gaming|amazon prime|game pass|ps plus|playstation plus|subscription|subscribers|soundtrack|skins?|in-game|loot)\b|" +
            @"\bfree\s+(\S+\s+){0,3}dlc\b", Words);

        /// <summary>
        /// Always free: those games never cost money. In the title either way; in the summary only the written word,
        /// because "completely free to play this weekend" is how a free weekend is told.
        /// </summary>
        private static readonly Regex alwaysFree = new Regex(@"\bfree[- ]to[- ]play\b|\bf2p\b", Words);
        private static readonly Regex alwaysFreeWord = new Regex(@"\bfree-to-play\b|\bf2p\b", Words);

        private static readonly Regex roundup = new Regex(@"\broundup\b|\bround-up\b", Words);

        /// <summary>
        /// Clearly a game to try for some days, even when it says "free to play": a free weekend, a free trial, free to
        /// play this weekend or until a day.
        /// </summary>
        private static readonly Regex clearTrial = new Regex(
            @"\bfree weekend\b|\bfree trial\b|\bfree to try\b|\bfree play (days|week|weekend)\b|" +
            @"\bfree[- ]to[- ]play\b.{0,40}\b(this weekend|the weekend|all weekend|this week|until|through|for a (very )?limited time)\b", Words);

        /// <summary>Clearly a gift: it's kept, it's given away, keys are handed out.</summary>
        private static readonly Regex gift = new Regex(
            @"\bfree to (keep|claim)\b|\bclaim and keep\b|\bkeep (it )?(for good|forever)\b|\bgiv(e|es|ing) away\b|\bgiveaway\b|" +
            @"\bhand(s|ing)? out\b|\bfree (steam |epic |gog )?keys?\b|\bkeys? (are|is) (ready|up) (to|for) grab\b|\b100\s?% off\b", Words);

        /// <summary>"Play X FREE", "try it for free": how a game to try is told, after the clear gifts.</summary>
        private static readonly Regex trial = new Regex(@"\btry (it |out )?.{0,60}\bfree\b|\bplay .{0,60}\bfree\b", Words);

        /// <summary>Free "for a limited time", without more: a gift of a small store, like the ones of itch.io.</summary>
        private static readonly Regex limitedTime = new Regex(@"\bfor a (very )?limited time\b", Words);

        /// <summary>"until November 16", "through Monday, November 9 at 18:00 UTC", "before 10 AM PT on November 20".</summary>
        private static readonly Regex untilDate = new Regex(
            @"\b(?:until|through|thru|till|before|ends?(?:\s+on)?)\s+" +
            @"(?:\d{1,2}(?::\d{2})?\s*(?:[ap]\.?m\.?)?\s*(?:utc|gmt|cet|cest|bst|pst|pdt|pt|est|edt|et|cst|cdt|ct|mst|mdt|mt)?\s*(?:on\s+)?)?" +
            @"(?:(?:mon|tues|wednes|thurs|fri|satur|sun)day,?\s+)?" +
            @"(?<month>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\.?\s+" +
            @"(?<day>\d{1,2})(?:st|nd|rd|th)?\b", Words);

        private static readonly string[] months = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

        /// <summary>
        /// The day an offer ends, when the headline says it: the end of that day in UTC, in the year it was published or
        /// the next one if the day already went by (a headline of December can say "until January 3").
        /// </summary>
        public static DateTime? ParseNewsEnd(string text, DateTime publishedUtc)
        {
            var match = untilDate.Match(text ?? string.Empty);
            if (!match.Success)
            {
                return null;
            }

            var month = Array.IndexOf(months, match.Groups["month"].Value.Substring(0, 3).ToLowerInvariant()) + 1;
            if (month < 1 || !int.TryParse(match.Groups["day"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var day) ||
                day < 1 || day > DateTime.DaysInMonth(publishedUtc.Year, month))
            {
                return null;
            }

            var date = new DateTime(publishedUtc.Year, month, day, 0, 0, 0, DateTimeKind.Utc);
            if (date < publishedUtc.Date.AddDays(-60))
            {
                if (day > DateTime.DaysInMonth(publishedUtc.Year + 1, month))
                {
                    return null;
                }

                date = new DateTime(publishedUtc.Year + 1, month, day, 0, 0, 0, DateTimeKind.Utc);
            }

            return date.AddDays(1);
        }

        /// <summary>
        /// Whether a headline of GG.deals speaks of a game that costs money and is free now: given away, or to try for
        /// some days. Summaries of the week too. The rest isn't shown: demos, tests, drops, games that are always free.
        /// </summary>
        public static bool ClassifyNews(string title, string summary, out FreeGameKind? kind)
        {
            kind = null;
            title = title ?? string.Empty;
            summary = summary ?? string.Empty;
            var text = title + " " + summary;
            if (notForPc.IsMatch(title))
            {
                return false;
            }

            var isTrial = clearTrial.IsMatch(text);
            if (!isTrial && (alwaysFree.IsMatch(title) || alwaysFreeWord.IsMatch(summary)))
            {
                return false;
            }

            // A summary speaks of many games, of every kind.
            if (roundup.IsMatch(title))
            {
                return true;
            }

            // From the clearest to the vaguest: a free weekend, a gift, "play it free", "free for a limited time".
            if (isTrial)
            {
                kind = FreeGameKind.Trial;
                return true;
            }

            if (gift.IsMatch(text))
            {
                kind = FreeGameKind.Keep;
                return true;
            }

            if (trial.IsMatch(text))
            {
                kind = FreeGameKind.Trial;
                return true;
            }

            if (limitedTime.IsMatch(text))
            {
                kind = FreeGameKind.Keep;
                return true;
            }

            return false;
        }

        /// <summary>The headlines of the feed of free games of GG.deals, of the last week, that are worth showing.</summary>
        public static List<FreeGameNews> ParseNews(string xml, DateTime nowUtc)
        {
            var result = new List<FreeGameNews>();
            XDocument document;
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null };
            using (var reader = System.Xml.XmlReader.Create(new StringReader(xml ?? string.Empty), settings))
            {
                document = XDocument.Load(reader);
            }

            foreach (var item in document.Root?.Element("channel")?.Elements("item") ?? Enumerable.Empty<XElement>())
            {
                var title = WebUtility.HtmlDecode((string)item.Element("title") ?? string.Empty).Trim();
                var link = ((string)item.Element("link") ?? string.Empty).Trim();
                var description = (string)item.Element("description") ?? string.Empty;
                if (title.IsNullOrEmpty() || !link.StartsWith("https://gg.deals/", StringComparison.OrdinalIgnoreCase) ||
                    !DateTimeOffset.TryParse((string)item.Element("pubDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published))
                {
                    continue;
                }

                var date = published.UtcDateTime;
                if (nowUtc - date >= newsAge || date > nowUtc.AddDays(1))
                {
                    continue;
                }

                var summary = WebUtility.HtmlDecode(Regex.Replace(description, "<[^>]+>", " "));
                summary = Regex.Replace(summary, @"\s+", " ").Trim();
                if (!ClassifyNews(title, summary, out var kind))
                {
                    continue;
                }

                var image = WebUtility.HtmlDecode(Regex.Match(description, "<img[^>]+src=\"([^\"]+)\"", Words).Groups[1].Value);
                if (!image.StartsWith("https://img.gg.deals/", StringComparison.OrdinalIgnoreCase))
                {
                    image = null;
                }

                result.Add(new FreeGameNews { Title = title, Url = link, ImageUrl = image, Published = date, Kind = kind, Ends = ParseNewsEnd(title + " " + summary, date) });
            }

            return result.
                GroupBy(a => a.Url, StringComparer.OrdinalIgnoreCase).
                Select(a => a.First()).
                OrderByDescending(a => a.Published).
                ToList();
        }

        private static readonly Regex aboutEpic = new Regex(@"\bepic\b", Words);
        private static readonly Regex aboutSteam = new Regex(@"\bsteam\b", Words);
        private static readonly Regex aboutKeys = new Regex(@"\bkeys?\b", Words);

        /// <summary>
        /// A headline about a game that the stores already show says nothing new: it goes. So does one about a gift of
        /// Epic, or of the store of Steam, when that store answered: what it gives now is in the cards, with its days,
        /// and what isn't there is over. Keys of Steam that others give away are another thing, and stay.
        /// </summary>
        public static List<FreeGameNews> RemoveRepeated(List<FreeGameNews> news, IEnumerable<FreeGame> games, ICollection<string> answered = null)
        {
            string plain(string text) => " " + GameCaptures.Normalize((text ?? string.Empty).Replace("&", " and ")) + " ";
            var names = (games ?? Enumerable.Empty<FreeGame>()).Select(a => plain(a.Title)).Where(a => a.Trim().Length >= 4).Distinct().ToList();
            return (news ?? new List<FreeGameNews>()).Where(a =>
            {
                var title = plain(a.Title);
                if (names.Any(name => title.Contains(name)))
                {
                    return false;
                }

                if (a.Kind == FreeGameKind.Keep && answered != null)
                {
                    if (answered.Contains(FreeGame.Epic) && aboutEpic.IsMatch(a.Title ?? string.Empty))
                    {
                        return false;
                    }

                    if (answered.Contains(FreeGame.Steam) && aboutSteam.IsMatch(a.Title ?? string.Empty) && !aboutKeys.IsMatch(a.Title ?? string.Empty))
                    {
                        return false;
                    }
                }

                return true;
            }).ToList();
        }

        #endregion GG.deals
    }
}
