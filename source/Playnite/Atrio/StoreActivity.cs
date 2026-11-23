using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Playnite.Atrio
{
    /// <summary>What a post of a store is about.</summary>
    public enum ActivityKind
    {
        News,

        /// <summary>The notes of a patch, or a small update.</summary>
        Patch,
        Update,
        MajorUpdate,
        Release,
        Upcoming,
        Dlc,
        Season,
        InGameEvent,
        Contest,
        Sale,
        Stream,
        Beta,
        FreeTrial
    }

    /// <summary>A post about a game: news, notes of a patch, an event.</summary>
    public class ActivityPost
    {
        public string Id { get; set; }
        public ActivityKind Kind { get; set; }
        public string Title { get; set; }

        /// <summary>The start of it in plain words, for the card.</summary>
        public string Summary { get; set; }

        /// <summary>The whole post. Null when the store only shows it on its page.</summary>
        public string Html { get; set; }
        public string ImageUrl { get; set; }

        /// <summary>The same picture, small, for the card. Null when the store doesn't have one.</summary>
        public string ThumbnailUrl { get; set; }
        public string Url { get; set; }

        /// <summary>When it was published, null when the store doesn't say.</summary>
        public DateTime? Date { get; set; }
        public int Likes { get; set; }
        public int Comments { get; set; }

        /// <summary>What a person waits for to play again: the patches and the updates, big or small.</summary>
        [JsonIgnore]
        public bool IsPatch => Kind == ActivityKind.Patch || Kind == ActivityKind.Update || Kind == ActivityKind.MajorUpdate;
    }

    public class ActivityPage
    {
        public List<ActivityPost> Posts { get; set; } = new List<ActivityPost>();

        /// <summary>The store has older ones, from this number on.</summary>
        public bool HasMore { get; set; }
        public int Next { get; set; }

        /// <summary>Who published them, when it isn't the store of the game.</summary>
        public bool FromSteam { get; set; }
        public DateTime Read { get; set; }

        /// <summary>Only because the store of the game didn't answer this time: it isn't kept.</summary>
        [JsonIgnore]
        public bool IsPassing { get; set; }
    }

    /// <summary>One store the person has the game in, and where its posts come from.</summary>
    public class ActivitySource
    {
        public Game Copy { get; set; }
        public string StoreName { get; set; }

        /// <summary>What identifies it in the caches: the same for the same game of the same store.</summary>
        public string Key { get; set; }

        /// <summary>The store publishes something of its own for each game.</summary>
        public bool HasOwn => OwnKind != null;

        /// <summary>steam, gog, xbox, itch, battlenet or riot. Null for the ones that publish nothing that can be read.</summary>
        public string OwnKind { get; set; }

        /// <summary>The number of the same game on Steam when the person has it there too.</summary>
        public string SteamAppId { get; set; }
    }

    /// <summary>
    /// The activity of a game as its own store tells it: the posts and the notes of the patches. Everything is public, the
    /// same anyone can read without an account. A store that publishes nothing for each game (Epic, Amazon, Ubisoft, EA)
    /// shows what the same game publishes on Steam, and the page says so.
    /// </summary>
    public static class StoreActivity
    {
        private class SavedId
        {
            public string AppId { get; set; }
            public DateTime Date { get; set; }
        }

        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly HttpClient client = CreateClient();
        private static readonly SemaphoreSlim slots = new SemaphoreSlim(3, 3);
        private static readonly ConcurrentDictionary<string, ActivityPage> pages = new ConcurrentDictionary<string, ActivityPage>();
        private static readonly ConcurrentQueue<string> pagesOrder = new ConcurrentQueue<string>();
        private static int trimmed;
        private const int PagesKept = 40;
        private static readonly object idsLock = new object();
        private static Dictionary<string, SavedId> ids;

        public const int PageSize = 10;
        private static readonly TimeSpan lifetime = TimeSpan.FromMinutes(45);
        private static readonly TimeSpan missingIdLifetime = TimeSpan.FromDays(30);
        private const long FirstClan = 103582791429521408;

        private static readonly Guid epicId = Guid.Parse("00000002-DBD1-46C6-B5D0-B1BA559D10E4");
        private static readonly Guid gogId = Guid.Parse("AEBE8B7C-6DC3-4A66-AF31-E7375C6B5E9E");
        private static readonly Guid xboxId = Guid.Parse("7e4fbb5e-2ae3-48d4-8ba0-6b30e7a4e287");
        private static readonly Guid itchId = Guid.Parse("00000001-EBB2-4EEC-ABCB-7C89937A42BB");
        private static readonly Guid battleNetId = Guid.Parse("E3C26A3D-D695-4CB7-A769-5FF7612C7EDD");
        private static readonly Guid riotId = Guid.Parse("91d13c6f-63d3-42ed-a100-6f811a8387ea");

        /// <summary>The numbers Steam gives to each kind of post.</summary>
        private static readonly Dictionary<int, ActivityKind> steamKinds = new Dictionary<int, ActivityKind>
        {
            { 10, ActivityKind.Release }, { 11, ActivityKind.Stream }, { 12, ActivityKind.Patch }, { 13, ActivityKind.Update }, { 14, ActivityKind.MajorUpdate },
            { 15, ActivityKind.Dlc }, { 16, ActivityKind.Upcoming }, { 17, ActivityKind.Stream }, { 18, ActivityKind.Stream }, { 19, ActivityKind.Stream },
            { 20, ActivityKind.Sale }, { 21, ActivityKind.Sale }, { 22, ActivityKind.InGameEvent }, { 23, ActivityKind.InGameEvent },
            { 24, ActivityKind.InGameEvent }, { 25, ActivityKind.InGameEvent }, { 26, ActivityKind.Contest }, { 28, ActivityKind.News },
            { 29, ActivityKind.Beta }, { 30, ActivityKind.Dlc }, { 31, ActivityKind.FreeTrial }, { 32, ActivityKind.Season }, { 33, ActivityKind.Season },
            { 35, ActivityKind.InGameEvent }
        };

        /// <summary>The place of each language in the lists of pictures and texts of Steam.</summary>
        private static readonly Dictionary<string, int> steamLanguages = new Dictionary<string, int>
        {
            { "english", 0 }, { "german", 1 }, { "french", 2 }, { "italian", 3 }, { "koreana", 4 }, { "spanish", 5 }, { "schinese", 6 }, { "tchinese", 7 },
            { "russian", 8 }, { "thai", 9 }, { "japanese", 10 }, { "portuguese", 11 }, { "polish", 12 }, { "danish", 13 }, { "dutch", 14 }, { "finnish", 15 },
            { "norwegian", 16 }, { "swedish", 17 }, { "hungarian", 18 }, { "czech", 19 }, { "romanian", 20 }, { "turkish", 21 },
            { "brazilian", 22 }, { "bulgarian", 23 }, { "greek", 24 }, { "arabic", 25 }, { "ukrainian", 26 }, { "latam", 27 }, { "vietnamese", 28 }, { "indonesian", 30 }
        };

        private static readonly Dictionary<string, string> blizzardGames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "WTCG", "hearthstone" }, { "Pro", "overwatch" }, { "Fen", "diablo-4" }, { "D3", "diablo-3" }, { "OSI", "diablo-2-resurrected" },
            { "ANBS", "diablo-immortal" }, { "S2", "starcraft-2" }, { "Hero", "heroes-of-the-storm" }, { "WoW", "world-of-warcraft" }, { "GRY", "warcraft-rumble" }
        };

        private static readonly Dictionary<string, string> riotGames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "valorant", "https://playvalorant.com" }, { "league", "https://www.leagueoflegends.com" }, { "lol", "https://www.leagueoflegends.com" },
            { "2xko", "https://2xko.riotgames.com" }, { "lion", "https://2xko.riotgames.com" }, { "tft", "https://teamfighttactics.leagueoflegends.com" }
        };

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler { UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate };
            var result = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 16 * 1024 * 1024 };
            result.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Atrio");
            return result;
        }

        /// <summary>The name Steam gives to the language of Atrio: es_ES is spanish, es_419 is latam.</summary>
        public static string GetSteamLanguage(string language)
        {
            switch ((language ?? string.Empty).Replace('-', '_'))
            {
                case "pt_BR":
                    return "brazilian";
                case "zh_CN":
                    return "schinese";
                case "zh_TW":
                    return "tchinese";
            }

            switch ((language ?? string.Empty).Split('_', '-')[0].ToLowerInvariant())
            {
                case "es":
                    return language.IndexOf("419", StringComparison.Ordinal) >= 0 ? "latam" : "spanish";
                case "de":
                    return "german";
                case "fr":
                    return "french";
                case "it":
                    return "italian";
                case "ko":
                    return "koreana";
                case "ru":
                    return "russian";
                case "ja":
                    return "japanese";
                case "pt":
                    return "portuguese";
                case "pl":
                    return "polish";
                case "da":
                    return "danish";
                case "nl":
                    return "dutch";
                case "fi":
                    return "finnish";
                case "no":
                case "nb":
                    return "norwegian";
                case "sv":
                    return "swedish";
                case "hu":
                    return "hungarian";
                case "cs":
                    return "czech";
                case "ro":
                    return "romanian";
                case "tr":
                    return "turkish";
                case "ar":
                    return "arabic";
                case "bg":
                    return "bulgarian";
                case "el":
                    return "greek";
                case "uk":
                    return "ukrainian";
                case "vi":
                    return "vietnamese";
                case "id":
                    return "indonesian";
                default:
                    return "english";
            }
        }

        public static string Language { get; set; } = "en_US";

        private static string SteamLanguage => GetSteamLanguage(Language);

        /// <summary>es-es, en-us: how the pages of Blizzard, Riot and GOG write a language.</summary>
        private static string WebLanguage
        {
            get
            {
                switch (Language.Split('_', '-')[0].ToLowerInvariant())
                {
                    case "es":
                        return Language.IndexOf("419", StringComparison.Ordinal) >= 0 ? "es-mx" : "es-es";
                    case "de":
                        return "de-de";
                    case "fr":
                        return "fr-fr";
                    case "it":
                        return "it-it";
                    case "pt":
                        return "pt-br";
                    case "pl":
                        return "pl-pl";
                    case "ru":
                        return "ru-ru";
                    case "ja":
                        return "ja-jp";
                    case "ko":
                        return "ko-kr";
                    default:
                        return "en-us";
                }
            }
        }

        /// <summary>The stores of the game, in the order of its copies: the first one is the one the person uses by default.</summary>
        public static List<ActivitySource> GetSources(IEnumerable<Game> copies, Func<Game, string> storeName)
        {
            var all = copies?.Where(a => a != null).ToList() ?? new List<Game>();
            var steam = SteamContent.GetAppId(all);
            var result = new List<ActivitySource>();
            foreach (var copy in all)
            {
                var source = CreateSource(copy, storeName(copy), steam);
                if (result.All(a => a.Key != source.Key))
                {
                    result.Add(source);
                }
            }

            return result;
        }

        private static ActivitySource CreateSource(Game copy, string storeName, string steamAppId)
        {
            var source = new ActivitySource { Copy = copy, StoreName = storeName, SteamAppId = steamAppId, Key = $"game-{copy.PluginId:N}-{copy.GameId}" };
            if (copy.PluginId == SteamContent.SteamPluginId && int.TryParse(copy.GameId, out _))
            {
                source.OwnKind = "steam";
                source.SteamAppId = copy.GameId;
                source.Key = $"steam-{copy.GameId}";
            }
            else if (copy.PluginId == gogId && long.TryParse(copy.GameId, out _))
            {
                source.OwnKind = "gog";
                source.Key = $"gog-{copy.GameId}";
            }
            else if (copy.PluginId == xboxId && !copy.GameId.IsNullOrEmpty())
            {
                source.OwnKind = "xbox";
                source.Key = $"xbox-{copy.GameId}";
            }
            else if (copy.PluginId == itchId && GetItchUrl(copy) != null)
            {
                source.OwnKind = "itch";
                source.Key = $"itch-{copy.GameId}";
            }
            else if (copy.PluginId == battleNetId && blizzardGames.ContainsKey(copy.GameId ?? string.Empty))
            {
                source.OwnKind = "battlenet";
                source.Key = $"battlenet-{copy.GameId}";
            }
            else if (copy.PluginId == riotId && GetRiotSite(copy.GameId) != null)
            {
                source.OwnKind = "riot";
                source.Key = $"riot-{copy.GameId}";
            }

            return source;
        }

        private static string GetItchUrl(Game copy)
        {
            var url = copy.Links?.Select(a => a.Url).FirstOrDefault(a => a != null && Regex.IsMatch(a, @"^https?://[^/]+\.itch\.io/[^/?#]+"));
            return url == null ? null : Regex.Match(url, @"^https?://[^/]+\.itch\.io/[^/?#]+").Value;
        }

        private static string GetRiotSite(string gameId)
        {
            var id = (gameId ?? string.Empty).ToLowerInvariant();
            return riotGames.Where(a => id.Contains(a.Key)).Select(a => a.Value).FirstOrDefault();
        }

        private static void Remember(string key, ActivityPage page)
        {
            if (pages.TryAdd(key, page))
            {
                pagesOrder.Enqueue(key);
                while (pages.Count > PagesKept && pagesOrder.TryDequeue(out var oldest))
                {
                    pages.TryRemove(oldest, out _);
                }
            }
            else
            {
                pages[key] = page;
            }
        }

        /// <summary>What is kept in memory goes away; the copies on the disk stay.</summary>
        public static void ForgetMemory()
        {
            pages.Clear();
            while (pagesOrder.TryDequeue(out _))
            {
            }
        }

        private static void TrimOnce()
        {
            if (Interlocked.Exchange(ref trimmed, 1) == 0)
            {
                Task.Run(() => Controls.WebImage.Trim(Path.Combine(PlaynitePaths.DataCachePath, "actividad"), 60L * 1024 * 1024, DateTime.UtcNow.AddDays(-60)));
            }
        }

        /// <summary>The last answer of this session, at once. Null without it.</summary>
        public static ActivityPage GetFromMemory(ActivitySource source)
        {
            return pages.TryGetValue(CacheKey(source), out var page) ? page : null;
        }

        /// <summary>
        /// The last answer, from this session or from the disk, however old: it shows while the new one comes. It reads a
        /// file: not for the thread of the window.
        /// </summary>
        public static ActivityPage GetKnown(ActivitySource source)
        {
            TrimOnce();
            var key = CacheKey(source);
            if (pages.TryGetValue(key, out var page))
            {
                return page;
            }

            try
            {
                var path = CachePath(key);
                page = File.Exists(path) ? JsonConvert.DeserializeObject<ActivityPage>(File.ReadAllText(path)) : null;
                if (page?.Posts != null)
                {
                    Remember(key, page);
                    return page;
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException)
            {
                logger.Debug(e, $"Failed to read the saved activity of {key}.");
            }

            return null;
        }

        public static bool IsFresh(ActivityPage page)
        {
            return page != null && DateTime.UtcNow - page.Read < lifetime;
        }

        private static string CacheKey(ActivitySource source)
        {
            return $"{Regex.Replace(source.Key, "[^A-Za-z0-9._-]", "_")}-{SteamLanguage}";
        }

        private static string CachePath(string key)
        {
            return Path.Combine(PlaynitePaths.DataCachePath, "actividad", key + ".json");
        }

        private static void Save(string key, ActivityPage page)
        {
            Remember(key, page);
            try
            {
                var path = CachePath(key);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(page));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                logger.Debug(e, $"Failed to save the activity of {key}.");
            }
        }

        /// <summary>The newest posts of a store, or the ones from a number on: of the store itself or of Steam, as the first ones.</summary>
        public static async Task<ActivityPage> GetAsync(ActivitySource source, int from, bool fromSteam, CancellationToken token)
        {
            await slots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var page = await ReadAsync(source, from, fromSteam, token).ConfigureAwait(false);
                if (!page.IsPassing)
                {
                    page.Read = DateTime.UtcNow;
                    if (from == 0)
                    {
                        Save(CacheKey(source), page);
                    }
                }

                return page;
            }
            finally
            {
                slots.Release();
            }
        }

        private static async Task<ActivityPage> ReadAsync(ActivitySource source, int from, bool fromSteam, CancellationToken token)
        {
            Exception problem = null;
            if (source.HasOwn && source.OwnKind != "steam" && !(from > 0 && fromSteam))
            {
                try
                {
                    var own = await ReadOwnAsync(source, from, token).ConfigureAwait(false);
                    if (own.Posts.Count > 0 || from > 0)
                    {
                        return own;
                    }
                }
                catch (Exception e) when (from == 0 && !token.IsCancellationRequested && !(e is OutOfMemoryException))
                {
                    // A store that doesn't answer, or that answers something else, isn't a store without posts.
                    logger.Debug(e, $"Failed to read the posts of {source.Key}.");
                    problem = e;
                }
            }

            var appId = source.SteamAppId ?? (IsFindable(source.Copy) ? await FindSteamAppIdAsync(source.Copy, token).ConfigureAwait(false) : null);
            if (appId == null)
            {
                if (problem != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(problem).Throw();
                }

                return new ActivityPage();
            }

            var page = await ReadSteamAsync(appId, from, token).ConfigureAwait(false);
            page.FromSteam = source.OwnKind != "steam";
            page.IsPassing = problem != null;
            return page;
        }

        /// <summary>A game of a store for PC: the name of one of a console, or of a program, says little on Steam.</summary>
        private static bool IsFindable(Game copy)
        {
            return copy.PluginId != Guid.Empty && !copy.Roms.HasItems() && GameUnifier.IsPc(copy);
        }

        private static Task<ActivityPage> ReadOwnAsync(ActivitySource source, int from, CancellationToken token)
        {
            switch (source.OwnKind)
            {
                case "gog":
                    return ReadGogAsync(source.Copy.GameId, token);
                case "xbox":
                    return ReadXboxAsync(source.Copy.GameId, token);
                case "itch":
                    return ReadItchAsync(GetItchUrl(source.Copy), token);
                case "battlenet":
                    return ReadBlizzardAsync(blizzardGames[source.Copy.GameId], from, token);
                case "riot":
                    return ReadRiotAsync(GetRiotSite(source.Copy.GameId), token);
                default:
                    return Task.FromResult(new ActivityPage());
            }
        }

        private static async Task<string> GetTextAsync(string url, CancellationToken token)
        {
            using (var response = await client.GetAsync(url, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        // ---- Steam

        private const string SteamEventsUrl = "https://store.steampowered.com/events/ajaxgetpartnereventspageable/?clan_accountid=0&appid={0}&offset={1}&count={2}&l={3}&origin=https%3A%2F%2Fsteamcommunity.com";

        private static async Task<ActivityPage> ReadSteamAsync(string appId, int from, CancellationToken token)
        {
            var json = await GetTextAsync(string.Format(SteamEventsUrl, appId, from, PageSize, SteamLanguage), token).ConfigureAwait(false);
            return ParseSteam(json, appId, from, SteamLanguage, VideoText);
        }

        /// <summary>The words of a link to a video inside a post.</summary>
        public static string VideoText { get; set; } = "Video";

        public static ActivityPage ParseSteam(string json, string appId, int from, string language, string videoText)
        {
            var page = new ActivityPage();
            var events = (JObject.Parse(json)["events"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            foreach (var item in events)
            {
                var body = item["announcement_body"] as JObject;
                var text = (string)body?["body"];
                var extra = ParseExtra((string)item["jsondata"]);
                var clan = GetClan((string)item["clan_steamid"]);
                var start = (long?)item["rtime32_start_time"] ?? 0;
                var posted = (long?)body?["posttime"] ?? 0;
                var subtitle = Localized(extra, "localized_summary", language) ?? Localized(extra, "localized_subtitle", language);
                var capsule = Localized(extra, "localized_capsule_image", language);
                var excerpt = SteamMarkup.GetExcerpt(text, 320);
                var title = ((string)item["event_name"])?.Trim();
                page.Posts.Add(new ActivityPost
                {
                    Id = (string)item["gid"],
                    Kind = steamKinds.TryGetValue((int?)item["event_type"] ?? 0, out var kind) && kind != ActivityKind.News ? kind :
                        IsPatchTitle(title) ? ActivityKind.Patch : ActivityKind.News,
                    Title = title,
                    Summary = subtitle.IsNullOrWhiteSpace() ? excerpt : SteamMarkup.Cut(Regex.Replace(subtitle, @"\s+", " ").Trim(), 320),
                    Html = SteamMarkup.ToHtml(text, videoText),
                    ImageUrl = GetClanImage(clan, capsule) ??
                        GetClanImage(clan, Localized(extra, "localized_title_image", language)) ??
                        SteamMarkup.GetFirstImage(text),
                    ThumbnailUrl = GetThumbnail(GetClanImage(clan, capsule)),
                    Url = $"https://store.steampowered.com/news/app/{appId}/view/{(string)item["gid"]}",
                    Date = posted > 0 || start > 0 ? DateTimeOffset.FromUnixTimeSeconds(posted > 0 ? posted : start).LocalDateTime : (DateTime?)null,
                    Likes = (int?)item["votes_up"] ?? 0,
                    Comments = (int?)body?["commentcount"] ?? (int?)item["comment_count"] ?? 0
                });
            }

            page.Next = from + events.Count;
            page.HasMore = events.Count >= PageSize;
            return page;
        }

        /// <summary>Some write the notes of a patch as any other news: their title says it.</summary>
        private static readonly Regex patchTitleRegex = new Regex(
            @"\b(patch\s*notes|hotfix|changelog|release\s+notes|notas?\s+(del?|de\s+la)\s+(parche|versi[oó]n|actualizaci[oó]n)|parche\s+\d|patch\s+v?\d|correcci[oó]n\s+urgente)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static bool IsPatchTitle(string title)
        {
            return patchTitleRegex.IsMatch(title ?? string.Empty);
        }

        private static JObject ParseExtra(string json)
        {
            // Old posts answer "[]" here.
            if (json.IsNullOrEmpty() || !json.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                return JObject.Parse(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Localized(JObject extra, string name, string language)
        {
            if (!(extra?[name] is JArray values))
            {
                return null;
            }

            string at(string key) => steamLanguages.TryGetValue(key, out var index) && index < values.Count ? (string)values[index] : null;
            var result = at(language);
            if (result.IsNullOrEmpty() && language == "latam")
            {
                result = at("spanish");
            }
            else if (result.IsNullOrEmpty() && language == "spanish")
            {
                result = at("latam");
            }

            if (result.IsNullOrEmpty())
            {
                result = at("english");
            }

            return result.IsNullOrEmpty() ? null : result;
        }

        private static string GetClan(string steamId)
        {
            return long.TryParse(steamId, out var id) && id > FirstClan ? (id - FirstClan).ToString() : null;
        }

        private static string GetClanImage(string clan, string file)
        {
            return clan == null || file.IsNullOrEmpty() || !Regex.IsMatch(file, @"^[A-Za-z0-9_-]+\.(png|jpe?g|gif|webp)$") ? null :
                $"https://clan.akamai.steamstatic.com/images/{clan}/{file}";
        }

        /// <summary>Steam keeps a small one of each picture of a post: a third of the size, for a card that is small too.</summary>
        private static string GetThumbnail(string url)
        {
            return url == null || url.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? null :
                url.Insert(url.LastIndexOf('.'), "_400x225");
        }

        private const string SteamSearchUrl = "https://store.steampowered.com/api/storesearch/?term={0}&l=english&cc=US";

        private const string SteamDateUrl = "https://store.steampowered.com/api/appdetails?appids={0}&filters=release_date&l=english&cc=US";

        /// <summary>
        /// The same game on Steam, by its name: the same one, so that a sequel, a demo or a remake of another year never takes
        /// its place. The answer is kept, also when there's none.
        /// </summary>
        private static async Task<string> FindSteamAppIdAsync(Game copy, CancellationToken token)
        {
            var name = copy.Name;
            if (GameUnifier.NormalizeName(name).Length == 0)
            {
                return null;
            }

            var year = copy.ReleaseYear;
            var key = $"{name.Trim().ToLowerInvariant()}|{year}";

            lock (idsLock)
            {
                var known = LoadIds();
                if (known.TryGetValue(key, out var saved) && (saved.AppId != null || DateTime.UtcNow - saved.Date < missingIdLifetime))
                {
                    return saved.AppId;
                }
            }

            var json = await GetTextAsync(string.Format(SteamSearchUrl, Uri.EscapeDataString(name)), token).ConfigureAwait(false);
            var appId = ParseSteamSearch(json, name);
            if (appId != null && year != null)
            {
                var found = ParseReleaseYear(await GetTextAsync(string.Format(SteamDateUrl, appId), token).ConfigureAwait(false));
                if (found != null && !IsSameGame(year.Value, found.Value))
                {
                    logger.Debug($"{name} on Steam is of {found}, not of {year}: it's another game.");
                    appId = null;
                }
            }

            lock (idsLock)
            {
                LoadIds()[key] = new SavedId { AppId = appId, Date = DateTime.UtcNow };
                SaveIds();
            }

            return appId;
        }

        public static string ParseSteamSearch(string json, string name)
        {
            var key = GameUnifier.NormalizeName(name);
            var found = (JObject.Parse(json)["items"] as JArray)?.OfType<JObject>().
                Where(a => (string)a["type"] == "app" && GameUnifier.NormalizeName((string)a["name"]) == key).
                ToList() ?? new List<JObject>();
            var exact = found.FirstOrDefault(a => string.Equals((string)a["name"], name, StringComparison.OrdinalIgnoreCase)) ?? found.FirstOrDefault();
            return exact == null ? null : ((long?)exact["id"])?.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The library has the year a game first came out anywhere, and Steam the one it came out there, often some years
        /// later. An older one, or one of a decade later, is another game with the same name.
        /// </summary>
        public static bool IsSameGame(int year, int steamYear)
        {
            return steamYear >= year - 2 && steamYear <= year + 9;
        }

        /// <summary>The year of «Jan 14, 2022». Null when it isn't out, or when Steam doesn't say.</summary>
        public static int? ParseReleaseYear(string json)
        {
            var date = JObject.Parse(json).Descendants().OfType<JProperty>().FirstOrDefault(a => a.Name == "release_date")?.Value as JObject;
            var year = Regex.Match((string)(date?["date"] as JValue) ?? string.Empty, @"\b(19|20)\d\d\b");
            return year.Success ? int.Parse(year.Value, CultureInfo.InvariantCulture) : (int?)null;
        }

        private static string IdsPath => Path.Combine(PlaynitePaths.ConfigRootPath, "atrio-steam-nombres.json");

        private static Dictionary<string, SavedId> LoadIds()
        {
            if (ids != null)
            {
                return ids;
            }

            try
            {
                ids = File.Exists(IdsPath) ? JsonConvert.DeserializeObject<Dictionary<string, SavedId>>(File.ReadAllText(IdsPath)) : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException)
            {
                logger.Debug(e, "Failed to read the numbers of Steam found by name.");
            }

            ids = ids ?? new Dictionary<string, SavedId>();
            return ids;
        }

        private static void SaveIds()
        {
            try
            {
                File.WriteAllText(IdsPath, JsonConvert.SerializeObject(ids));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                logger.Debug(e, "Failed to save the numbers of Steam found by name.");
            }
        }

        // ---- GOG

        private static async Task<ActivityPage> ReadGogAsync(string id, CancellationToken token)
        {
            var json = await GetTextAsync($"https://api.gog.com/products/{id}?expand=changelog&locale={WebLanguage}", token).ConfigureAwait(false);
            var answer = JObject.Parse(json);
            var page = new ActivityPage
            {
                Posts = ParseChangelog((string)answer["changelog"], (string)answer["title"], (string)((answer["links"] as JObject)?["product_card"] as JValue), $"gog-{id}")
            };
            return page;
        }

        private static readonly Regex headingRegex = new Regex(@"<h(?<level>[1-6])[^>]*>(?<text>.*?)</h\k<level>>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex entryRegex = new Regex(
            @"\d+\.\d+|\b(19|20)\d\d\b|\b\d{1,2}[/.-]\d{1,2}[/.-]\d{2,4}\b|\b(patch|update|hotfix|version|build|installer|parche|actualizaci[oó]n)\b.*\d|\d.*\b(patch|update|hotfix|version|build|parche|actualizaci[oó]n)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex scriptRegex = new Regex(@"<(script|style|iframe|object|embed|form)\b[^>]*>.*?</\1>|<(script|style|iframe|object|embed|form|link|meta)\b[^>]*>|\son[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        /// <summary>What a store may send as HTML, without anything that isn't text, pictures and links.</summary>
        public static string CleanHtml(string html)
        {
            return scriptRegex.Replace(html ?? string.Empty, string.Empty);
        }

        /// <summary>
        /// GOG keeps all the changes of a game in one text. Each title with a version or a date starts one of them; a title
        /// without either ("Gameplay", "UI") is a part of the one before.
        /// </summary>
        public static List<ActivityPost> ParseChangelog(string html, string gameName, string url, string idPrefix)
        {
            var posts = new List<ActivityPost>();
            var clean = CleanHtml(html);
            if (SteamMarkup.HtmlToText(clean).Length == 0)
            {
                return posts;
            }

            var headings = headingRegex.Matches(clean).Cast<Match>().ToList();
            var starts = headings.Where(a => entryRegex.IsMatch(SteamMarkup.HtmlToText(a.Groups["text"].Value))).ToList();
            if (starts.Count > 0)
            {
                // The titles of the changes are all of the same size: a bigger number inside one of them isn't another.
                var level = starts[0].Groups["level"].Value;
                starts = starts.Where(a => a.Groups["level"].Value == level).ToList();
            }

            if (starts.Count == 0)
            {
                posts.Add(CreateChange(gameName, clean, url, $"{idPrefix}-0"));
                return posts;
            }

            // What goes before the first one: usually where the last notes are.
            var before = clean.Substring(0, starts[0].Index);
            if (SteamMarkup.HtmlToText(before).Length > 0)
            {
                posts.Add(CreateChange(gameName, before, url, $"{idPrefix}-start"));
            }

            for (var i = 0; i < starts.Count; i++)
            {
                var begin = starts[i].Index + starts[i].Length;
                var end = i + 1 < starts.Count ? starts[i + 1].Index : clean.Length;
                posts.Add(CreateChange(SteamMarkup.HtmlToText(starts[i].Groups["text"].Value), clean.Substring(begin, end - begin), url, $"{idPrefix}-{i}"));
            }

            return posts;
        }

        private static ActivityPost CreateChange(string title, string html, string url, string id)
        {
            return new ActivityPost
            {
                Id = id,
                Kind = ActivityKind.Patch,
                Title = title,
                Html = html,
                Summary = SteamMarkup.Cut(SteamMarkup.HtmlToText(html), 320),
                Url = url,
                Date = FindDate(title)
            };
        }

        private static readonly string[] months = { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };
        private static readonly Regex monthFirstRegex = new Regex(@"\b(?<month>[A-Za-z]{3,9})\.?\s+(?<day>\d{1,2})(?:st|nd|rd|th)?,?\s+(?<year>(?:19|20)\d\d)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex dayFirstRegex = new Regex(@"\b(?<day>\d{1,2})(?:st|nd|rd|th)?\s+(?:of\s+)?(?<month>[A-Za-z]{3,9})\.?,?\s+(?<year>(?:19|20)\d\d)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex isoRegex = new Regex(@"\b(?<year>(?:19|20)\d\d)[-./](?<month>\d{1,2})[-./](?<day>\d{1,2})\b", RegexOptions.Compiled);

        /// <summary>The date written in a title. Null when there's none, or when it could be read two ways (03/04/2024).</summary>
        public static DateTime? FindDate(string text)
        {
            foreach (var match in new[] { monthFirstRegex.Match(text ?? string.Empty), dayFirstRegex.Match(text ?? string.Empty) }.Where(a => a.Success))
            {
                var name = match.Groups["month"].Value.ToLowerInvariant();
                var month = Array.FindIndex(months, a => a.StartsWith(name, StringComparison.Ordinal) || (name == "sept" && a == "september")) + 1;
                if (month > 0 && TryDate(match.Groups["year"].Value, month, match.Groups["day"].Value, out var date))
                {
                    return date;
                }
            }

            var iso = isoRegex.Match(text ?? string.Empty);
            return iso.Success && int.TryParse(iso.Groups["month"].Value, out var number) && TryDate(iso.Groups["year"].Value, number, iso.Groups["day"].Value, out var isoDate) ?
                isoDate : (DateTime?)null;
        }

        private static bool TryDate(string year, int month, string day, out DateTime date)
        {
            date = default(DateTime);
            if (!int.TryParse(year, out var y) || !int.TryParse(day, out var d) || month < 1 || month > 12 || d < 1 || d > DateTime.DaysInMonth(y, month))
            {
                return false;
            }

            date = new DateTime(y, month, d);
            return true;
        }

        // ---- Xbox

        private static async Task<ActivityPage> ReadXboxAsync(string packageFamily, CancellationToken token)
        {
            var language = WebLanguage;
            var lookup = await GetTextAsync(
                $"https://displaycatalog.mp.microsoft.com/v7.0/products/lookup?alternateId=PackageFamilyName&Value={Uri.EscapeDataString(packageFamily)}&market=US&languages={language}&fieldsTemplate=details",
                token).ConfigureAwait(false);
            var productId = (string)(JObject.Parse(lookup)["Products"] as JArray)?.OfType<JObject>().FirstOrDefault()?["ProductId"];
            if (productId.IsNullOrEmpty())
            {
                return new ActivityPage();
            }

            var json = await GetTextAsync(
                $"https://storeedgefd.dsx.mp.microsoft.com/v9.0/products/{Uri.EscapeDataString(productId)}?market=US&locale={language}&deviceFamily=Windows.Desktop",
                token).ConfigureAwait(false);
            return new ActivityPage { Posts = ParseMicrosoft(json, productId, WhatsNewText) };
        }

        /// <summary>The title of what is new in the version of the store of Microsoft, which comes without one.</summary>
        public static string WhatsNewText { get; set; } = "What's new";

        public static List<ActivityPost> ParseMicrosoft(string json, string productId, string title)
        {
            var product = JObject.Parse(json)["Payload"] as JObject;
            var notes = (product?["Notes"] as JArray)?.Select(a => ((string)a)?.Trim()).Where(a => !a.IsNullOrEmpty()).ToList() ?? new List<string>();
            if (notes.Count == 0)
            {
                return new List<ActivityPost>();
            }

            var text = string.Join("\n\n", notes);
            return new List<ActivityPost>
            {
                new ActivityPost
                {
                    Id = $"xbox-{productId}",
                    Kind = ActivityKind.Patch,
                    Title = title,
                    Summary = SteamMarkup.Cut(Regex.Replace(text, @"\s+", " "), 320),
                    Html = WebUtility.HtmlEncode(text).Replace("\r", string.Empty).Replace("\n", "<br>"),
                    Url = $"https://apps.microsoft.com/detail/{productId}"

                    // Without a date: the one of the catalog is of any change in it, an offer too.
                }
            };
        }

        // ---- itch.io

        private static async Task<ActivityPage> ReadItchAsync(string url, CancellationToken token)
        {
            using (var response = await client.GetAsync($"{url}/devlog.rss", token).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return new ActivityPage();
                }

                response.EnsureSuccessStatusCode();
                return new ActivityPage { Posts = ParseRss(await response.Content.ReadAsStringAsync().ConfigureAwait(false)) };
            }
        }

        private static readonly Regex versionTitleRegex = new Regex(
            @"\bv?\d+\.\d+(\.\d+)*\b|\b(patch|update|hotfix|version|build|parche|actualizaci[oó]n)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static List<ActivityPost> ParseRss(string xml)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(xml), settings))
            {
                return XDocument.Load(reader).Descendants("item").Take(30).Select(a =>
                {
                    var html = CleanHtml((string)a.Element("description"));
                    var title = ((string)a.Element("title"))?.Trim();
                    return new ActivityPost
                    {
                        Id = (string)a.Element("guid") ?? (string)a.Element("link"),
                        Kind = versionTitleRegex.IsMatch(title ?? string.Empty) || IsPatchTitle(title) ? ActivityKind.Patch : ActivityKind.News,
                        Title = title,

                        // The feed only has the start of each one: the whole post is on its page.
                        Summary = SteamMarkup.Cut(SteamMarkup.HtmlToText(html), 320),
                        ImageUrl = SteamMarkup.SafeUrl(Regex.Match(html, @"<img[^>]+src=""([^""]+)""", RegexOptions.IgnoreCase).Groups[1].Value),
                        Url = SteamMarkup.SafeUrl((string)a.Element("link")),
                        Date = DateTimeOffset.TryParse((string)a.Element("pubDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.LocalDateTime : (DateTime?)null
                    };
                }).ToList();
            }
        }

        // ---- Battle.net

        private static async Task<ActivityPage> ReadBlizzardAsync(string game, int from, CancellationToken token)
        {
            // The first ones come with the page of the game; the ones after that, only from its feed.
            var json = await GetTextAsync($"https://news.blizzard.com/{WebLanguage}/api/{(from > 0 ? "feed" : "news")}/{game}?offset={from}", token).ConfigureAwait(false);
            return ParseBlizzard(json, from);
        }

        public static ActivityPage ParseBlizzard(string json, int from)
        {
            var answer = JObject.Parse(json);
            var feed = answer["feed"] as JObject ?? answer;
            var items = (feed?["contentItems"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
            var page = new ActivityPage { Next = from + items.Count, HasMore = items.Count > 0 && (bool?)((feed["pagination"] as JObject)?["hasNextPage"] as JValue) == true };
            foreach (var properties in items.Select(a => a["properties"] as JObject).Where(a => a != null))
            {
                var title = ((string)properties["title"])?.Trim();
                page.Posts.Add(new ActivityPost
                {
                    Id = (string)properties["newsId"],
                    Kind = Regex.IsMatch($"{title} {(string)(properties["category"] as JValue)}", @"patch|hotfix|parche|correcci|notas de la versi", RegexOptions.IgnoreCase) ? ActivityKind.Patch : ActivityKind.News,
                    Title = title,
                    Summary = SteamMarkup.Cut(SteamMarkup.HtmlToText((string)properties["summary"]), 320),
                    ImageUrl = SteamMarkup.SafeUrl((string)((properties["staticAsset"] as JObject)?["imageUrl"] as JValue)),
                    Url = SteamMarkup.SafeUrl((string)properties["newsUrl"]),
                    Date = ((DateTime?)properties["lastUpdated"])?.ToLocalTime()
                });
            }

            return page;
        }

        // ---- Riot

        private static async Task<ActivityPage> ReadRiotAsync(string site, CancellationToken token)
        {
            var html = await GetTextAsync($"{site}/{WebLanguage}/news/", token).ConfigureAwait(false);
            if (html.IndexOf("__NEXT_DATA__", StringComparison.Ordinal) < 0)
            {
                throw new HttpRequestException("The page of news isn't the one of always.");
            }

            return new ActivityPage { Posts = ParseRiot(html, site) };
        }

        /// <summary>The pages of Riot carry their list of news as data.</summary>
        public static List<ActivityPost> ParseRiot(string html, string site)
        {
            var data = Regex.Match(html ?? string.Empty, @"<script id=""__NEXT_DATA__""[^>]*>(?<json>.*?)</script>", RegexOptions.Singleline);
            if (!data.Success)
            {
                return new List<ActivityPost>();
            }

            var posts = new List<ActivityPost>();
            foreach (var item in JObject.Parse(data.Groups["json"].Value).Descendants().OfType<JObject>())
            {
                var title = item["title"] as JValue;
                var date = (item["analytics"] as JObject)?["publishDate"] as JValue ?? item["publishedAt"] as JValue;
                var link = (string)((item["action"] as JObject)?["payload"] as JObject)?["url"];
                if (title?.Type != JTokenType.String || date == null || link.IsNullOrEmpty())
                {
                    continue;
                }

                var url = SteamMarkup.SafeUrl(link.StartsWith("/", StringComparison.Ordinal) ? site + link : link);
                if (url == null || posts.Any(a => a.Url == url))
                {
                    continue;
                }

                posts.Add(new ActivityPost
                {
                    Id = url,
                    Kind = Regex.IsMatch(url, @"patch[-_]notes|patch[-_][\d-]+[-_]notes", RegexOptions.IgnoreCase) ? ActivityKind.Patch : ActivityKind.News,
                    Title = ((string)title).Trim(),
                    Summary = SteamMarkup.Cut(SteamMarkup.HtmlToText(GetRiotText(item["description"])), 320),
                    ImageUrl = SteamMarkup.SafeUrl((string)((item["media"] as JObject)?["url"] as JValue)),
                    Url = url,
                    Date = ParseDate(date)
                });
            }

            return posts.OrderByDescending(a => a.Date ?? DateTime.MinValue).Take(40).ToList();
        }

        private static string GetRiotText(JToken token)
        {
            return token is JValue value && value.Type == JTokenType.String ? (string)value :
                token is JObject item ? (string)(item["body"] as JValue) : null;
        }

        private static DateTime? ParseDate(JValue value)
        {
            if (value.Type == JTokenType.Date)
            {
                return ((DateTime)value).ToLocalTime();
            }

            return DateTimeOffset.TryParse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date.LocalDateTime : (DateTime?)null;
        }
    }
}
