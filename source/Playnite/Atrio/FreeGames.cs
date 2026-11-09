using Playnite.Common;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    /// <summary>A game that Epic gives away for a while: now, or next.</summary>
    public class FreeGame
    {
        public string Title { get; set; }
        public string Url { get; set; }
        public string ImageUrl { get; set; }
        public DateTime Start { get; set; }
        public DateTime End { get; set; }

        /// <summary>It can be taken now; otherwise it's the next one.</summary>
        public bool IsNow { get; set; }
    }

    /// <summary>
    /// The games that the store of Epic gives away each week, as its own page lists them for anyone. Atrio only
    /// shows them and opens their page: taking one is done by the person, in the store, with their account.
    /// </summary>
    public static class FreeGames
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string PromotionsUrl = "https://store-site-backend-static-ipv4.ak.epicgames.com/freeGamesPromotions?locale={0}&country={1}&allowCountries={1}";
        public const string FileName = "atrio-gratis.json";
        private static readonly TimeSpan lifetime = TimeSpan.FromHours(3);
        private static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static List<FreeGame> last;
        private static DateTime lastTime;

        private class SavedList
        {
            public DateTime Time { get; set; }
            public List<FreeGame> Games { get; set; }
        }

        private static string FilePath => Path.Combine(PlaynitePaths.ConfigRootPath, FileName);

        /// <summary>"es-ES" for the store when Atrio is in Spanish: its pages and names come in that language.</summary>
        public static string GetStoreLocale(CultureInfo culture)
        {
            var name = culture?.Name;
            return name.IsNullOrEmpty() || !name.Contains("-") ? "en-US" : name;
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

        /// <summary>The list saved last time, right away, even if it's old. Only the ones that haven't ended.</summary>
        public static List<FreeGame> GetSaved(DateTime nowUtc)
        {
            try
            {
                var list = last;
                if (list == null && File.Exists(FilePath))
                {
                    list = Playnite.SDK.Data.Serialization.FromJsonFile<SavedList>(FilePath)?.Games;
                }

                return list?.Where(a => a.End > nowUtc).Select(a => Refresh(a, nowUtc)).ToList() ?? new List<FreeGame>();
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Debug(e, "Failed to read the free games saved.");
                return new List<FreeGame>();
            }
        }

        /// <summary>A gift that was next becomes the one of now when its day comes.</summary>
        private static FreeGame Refresh(FreeGame game, DateTime nowUtc)
        {
            game.IsNow = game.Start <= nowUtc && nowUtc < game.End;
            return game;
        }

        public static async Task<List<FreeGame>> GetAsync(CancellationToken token)
        {
            if (last != null && DateTime.UtcNow - lastTime < lifetime)
            {
                return GetSaved(DateTime.UtcNow);
            }

            var url = string.Format(PromotionsUrl, GetStoreLocale(Localization.ApplicationLanguageCultureInfo), GetCountry());
            var json = await client.GetStringAsync(url).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var games = Parse(json, DateTime.UtcNow, GetStoreLocale(Localization.ApplicationLanguageCultureInfo));
            last = games;
            lastTime = DateTime.UtcNow;
            try
            {
                FileSystem.WriteStringToFile(FilePath, Playnite.SDK.Data.Serialization.ToJson(new SavedList { Time = lastTime, Games = games }));
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Debug(e, "Failed to keep the free games.");
            }

            return games;
        }

        private static DateTime? ReadDate(Newtonsoft.Json.Linq.JToken token)
        {
            if (token == null)
            {
                return null;
            }

            if (token.Type == Newtonsoft.Json.Linq.JTokenType.Date)
            {
                return ((DateTime)token).ToUniversalTime();
            }

            return DateTime.TryParse((string)token, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date) ? date : (DateTime?)null;
        }

        /// <summary>
        /// The offers of the answer where the price goes down to nothing: the one of now, and the next. The store
        /// marks a gift as an offer of 0 % of the price, whatever the game cost.
        /// </summary>
        public static List<FreeGame> Parse(string json, DateTime nowUtc, string locale)
        {
            var result = new List<FreeGame>();
            var elements = Newtonsoft.Json.Linq.JObject.Parse(json)["data"]?["Catalog"]?["searchStore"]?["elements"] as Newtonsoft.Json.Linq.JArray;
            foreach (var element in (elements ?? new Newtonsoft.Json.Linq.JArray()).OfType<Newtonsoft.Json.Linq.JObject>())
            {
                var title = (string)element["title"];
                if (title.IsNullOrWhiteSpace())
                {
                    continue;
                }

                // Any of these can come as null instead of an object: each step is checked.
                Newtonsoft.Json.Linq.JObject obj(Newtonsoft.Json.Linq.JToken token) => token as Newtonsoft.Json.Linq.JObject;
                Newtonsoft.Json.Linq.JArray list(Newtonsoft.Json.Linq.JToken token) => token as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray();
                FreeGame found = null;
                foreach (var group in new[] { "promotionalOffers", "upcomingPromotionalOffers" })
                {
                    var offers = list(obj(element["promotions"])?[group]);
                    foreach (var offer in offers.OfType<Newtonsoft.Json.Linq.JObject>().SelectMany(a => list(a["promotionalOffers"]).OfType<Newtonsoft.Json.Linq.JObject>()))
                    {
                        var start = ReadDate(offer["startDate"]);
                        var end = ReadDate(offer["endDate"]);
                        var percent = (int?)obj(offer["discountSetting"])?["discountPercentage"];
                        if (start == null || end == null || percent != 0 || end <= nowUtc)
                        {
                            continue;
                        }

                        // The offer of now has to leave the game at nothing; the next one only says it will.
                        var isNow = start <= nowUtc;
                        if (isNow && ((long?)obj(obj(element["price"])?["totalPrice"])?["discountPrice"] ?? 0) != 0)
                        {
                            continue;
                        }

                        found = new FreeGame { Title = title.Trim(), Start = start.Value, End = end.Value, IsNow = isNow };
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

                string slugOf(Newtonsoft.Json.Linq.JToken token) => list(token).OfType<Newtonsoft.Json.Linq.JObject>().Select(a => a["pageSlug"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)a["pageSlug"] : null).FirstOrDefault(a => !a.IsNullOrEmpty());
                var slug = slugOf(obj(element["catalogNs"])?["mappings"]) ??
                    slugOf(element["offerMappings"]) ??
                    (element["productSlug"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)element["productSlug"] : null);
                found.Url = slug.IsNullOrEmpty() ? $"https://store.epicgames.com/{locale}/free-games" : $"https://store.epicgames.com/{locale}/p/{slug.Trim('/')}";
                var images = list(element["keyImages"]).OfType<Newtonsoft.Json.Linq.JObject>().ToList();
                found.ImageUrl = new[] { "OfferImageWide", "DieselStoreFrontWide", "Thumbnail", "OfferImageTall" }.
                    Select(type => images.FirstOrDefault(a => (string)a["type"] == type)?["url"]).
                    Select(a => a?.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)a : null).
                    FirstOrDefault(a => !a.IsNullOrEmpty());
                result.Add(found);
            }

            return result.
                GroupBy(a => a.Title, StringComparer.OrdinalIgnoreCase).
                Select(a => a.First()).
                OrderByDescending(a => a.IsNow).
                ThenBy(a => a.Start).
                ToList();
        }
    }
}
