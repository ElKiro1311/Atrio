using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    /// <summary>What PCGamingWiki says about a game on a PC: wide screens, HDR, frame rates and controllers.</summary>
    public class PcgwInfo
    {
        /// <summary>The name of its page, null when the wiki doesn't have one.</summary>
        public string Page { get; set; }

        // As the wiki writes them: true, false, hackable, limited, unknown, n/a, always on, or nothing.
        public string Ultrawide { get; set; }
        public string Hdr { get; set; }
        public string Fps60 { get; set; }
        public string Fps120 { get; set; }
        public string Controller { get; set; }
        public string FullController { get; set; }

        /// <summary>When it was asked, in UTC.</summary>
        public DateTime Checked { get; set; }

        public bool HasPage => !Page.IsNullOrEmpty();

        public string PageUrl => HasPage ? "https://www.pcgamingwiki.com/wiki/" + Uri.EscapeDataString(Page.Replace(' ', '_')) : null;
    }

    /// <summary>
    /// The pages of PCGamingWiki, read one at a time when a game's page opens and kept for a month: the wiki is a
    /// small project that asks for that (60 questions a minute at most, a name that says who asks, and keeping
    /// what was read). Its content is under CC BY-NC-SA 3.0, so the page always says where it comes from.
    /// Nothing about the person goes there: only the id of the game in its store, or its name.
    /// </summary>
    public static class PcGamingWiki
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string Api = "https://www.pcgamingwiki.com/w/api.php";
        public const string FileName = "atrio-pcgw.json";
        private static readonly TimeSpan foundLifetime = TimeSpan.FromDays(30);
        private static readonly TimeSpan missingLifetime = TimeSpan.FromDays(7);
        private static readonly TimeSpan pause = TimeSpan.FromSeconds(1.5);
        private static readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private static readonly Regex fieldRegex = new Regex(@"^\|\s*(?<key>[a-z0-9+ ]+?)\s*=\s*(?<value>[^\r\n|}]*)", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);
        private static readonly object fileLock = new object();
        private static ConcurrentDictionary<string, PcgwInfo> cache;
        private static DateTime lastAsked = DateTime.MinValue;
        private static HttpClient client;

        public static event EventHandler Changed;

        private static string FilePath => Path.Combine(PlaynitePaths.ConfigRootPath, FileName);

        private static HttpClient Client
        {
            get
            {
                if (client == null)
                {
                    client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                    client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"Atrio/{AtrioInfo.CurrentVersion} (https://github.com/ElKiro1311/Atrio)");
                }

                return client;
            }
        }

        private static ConcurrentDictionary<string, PcgwInfo> Cache
        {
            get
            {
                if (cache == null)
                {
                    lock (fileLock)
                    {
                        if (cache == null)
                        {
                            Dictionary<string, PcgwInfo> saved = null;
                            try
                            {
                                if (File.Exists(FilePath))
                                {
                                    saved = Playnite.SDK.Data.Serialization.FromJsonFile<Dictionary<string, PcgwInfo>>(FilePath);
                                }
                            }
                            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                            {
                                logger.Warn(e, "Failed to read what PCGamingWiki said before.");
                            }

                            cache = new ConcurrentDictionary<string, PcgwInfo>(saved ?? new Dictionary<string, PcgwInfo>(), StringComparer.OrdinalIgnoreCase);
                        }
                    }
                }

                return cache;
            }
        }

        private static void Save()
        {
            try
            {
                lock (fileLock)
                {
                    FileSystem.WriteStringToFile(FilePath, Playnite.SDK.Data.Serialization.ToJson(Cache.ToDictionary(a => a.Key, a => a.Value)));
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, "Failed to keep what PCGamingWiki said.");
            }
        }

        public static readonly Guid GogPluginId = Guid.Parse("AEBE8B7C-6DC3-4A66-AF31-E7375C6B5E9E");

        /// <summary>How the game is known: its id on Steam, its id on GOG, or its name.</summary>
        public static string GetKey(IEnumerable<Game> copies, string name)
        {
            var list = copies?.Where(a => a != null).ToList() ?? new List<Game>();
            var steam = SteamContent.GetAppId(list);
            if (steam != null)
            {
                return "steam:" + steam;
            }

            var gog = list.FirstOrDefault(a => a.PluginId == GogPluginId && long.TryParse(a.GameId, out _));
            if (gog != null)
            {
                return "gog:" + gog.GameId;
            }

            // By name only the games of a store, whose names are public: never the ones added by hand.
            if (!list.Any(a => a.PluginId != Guid.Empty))
            {
                return null;
            }

            var normalized = GameCaptures.Normalize(name ?? string.Empty);
            return normalized.IsNullOrWhiteSpace() ? null : "name:" + normalized;
        }

        /// <summary>What the wiki said last time, even if it's old. Null if it was never asked.</summary>
        public static PcgwInfo GetSaved(string key)
        {
            return key != null && Cache.TryGetValue(key, out var info) ? info : null;
        }

        public static bool IsFresh(PcgwInfo info, DateTime nowUtc)
        {
            return info != null && nowUtc - info.Checked < (info.HasPage ? foundLifetime : missingLifetime);
        }

        public static async Task<PcgwInfo> GetAsync(IEnumerable<Game> copies, string name, CancellationToken token)
        {
            var key = GetKey(copies, name);
            if (key == null)
            {
                return null;
            }

            var saved = GetSaved(key);
            if (IsFresh(saved, DateTime.UtcNow))
            {
                return saved;
            }

            string page;
            if (key.StartsWith("steam:", StringComparison.Ordinal) || key.StartsWith("gog:", StringComparison.Ordinal))
            {
                var steam = key.StartsWith("steam:", StringComparison.Ordinal);
                var json = await AskAsync(
                    $"action=idlookup&format=json&formatversion=2&field={(steam ? "steamappid" : "gogid")}&value={Uri.EscapeDataString(key.Substring(steam ? 6 : 4))}",
                    token).ConfigureAwait(false);

                // No answer, or an answer of error: what was known stays, and it's asked again another time.
                if (!IsAnswer(json, "idlookup"))
                {
                    return saved;
                }

                page = ParsePageName(json);
            }
            else
            {
                page = name;
            }

            var info = page.IsNullOrWhiteSpace() ? new PcgwInfo() : await ReadPageAsync(page, token).ConfigureAwait(false);
            if (info == null)
            {
                return saved;
            }

            info.Checked = DateTime.UtcNow;
            Cache[key] = info;
            Save();
            Changed?.Invoke(null, EventArgs.Empty);
            return info;
        }

        private static async Task<string> AskAsync(string query, CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var wait = lastAsked + pause - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, token).ConfigureAwait(false);
                }

                lastAsked = DateTime.UtcNow;
                using (var response = await Client.GetAsync(Api + "?" + query, token).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode == 429)
                    {
                        // Too many questions: the wiki shuts the door for a minute.
                        lastAsked = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                        return null;
                    }

                    return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync().ConfigureAwait(false) : null;
                }
            }
            finally
            {
                gate.Release();
            }
        }

        /// <summary>A real answer of the wiki: one that has what was asked, not an error or a page of limits.</summary>
        public static bool IsAnswer(string json, string part)
        {
            try
            {
                var root = Newtonsoft.Json.Linq.JObject.Parse(json ?? string.Empty);
                return root["error"] == null && root[part] != null;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return false;
            }
        }

        public static string ParsePageName(string json)
        {
            try
            {
                var found = Newtonsoft.Json.Linq.JObject.Parse(json ?? "{}")["idlookup"] as Newtonsoft.Json.Linq.JArray;
                return (string)found?.FirstOrDefault()?["title"]?["Page"];
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return null;
            }
        }

        /// <returns>Null when the question didn't get an answer, an empty one when there is no page.</returns>
        private static async Task<PcgwInfo> ReadPageAsync(string page, CancellationToken token)
        {
            var json = await AskAsync("action=query&format=json&formatversion=2&prop=revisions&rvprop=content&rvslots=main&redirects=1&titles=" +
                Uri.EscapeDataString(page), token).ConfigureAwait(false);
            return IsAnswer(json, "query") ? ParsePage(json) : null;
        }

        public static PcgwInfo ParsePage(string json)
        {
            try
            {
                var first = (Newtonsoft.Json.Linq.JObject.Parse(json)["query"]?["pages"] as Newtonsoft.Json.Linq.JArray)?.FirstOrDefault();
                var text = (string)first?["revisions"]?.FirstOrDefault()?["slots"]?["main"]?["content"];
                if (first == null || first["missing"] != null || text.IsNullOrEmpty())
                {
                    return new PcgwInfo();
                }

                var info = ParseWikitext(text);
                info.Page = (string)first["title"];
                return info;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return new PcgwInfo();
            }
        }

        /// <summary>The fields of the templates of the page that matter here; the first time each one appears.</summary>
        public static PcgwInfo ParseWikitext(string text)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in fieldRegex.Matches(text ?? string.Empty))
            {
                var key = Regex.Replace(match.Groups["key"].Value.Trim(), @"\s+", " ");
                if (!values.ContainsKey(key))
                {
                    values[key] = match.Groups["value"].Value.Trim().ToLowerInvariant();
                }
            }

            string get(string key) => values.TryGetValue(key, out var value) && !value.IsNullOrEmpty() ? value : null;
            return new PcgwInfo
            {
                Ultrawide = get("ultrawidescreen"),
                Hdr = get("hdr"),
                Fps60 = get("60 fps"),
                // The pages call it «120 fps»; some older ones, «120+ fps».
                Fps120 = get("120 fps") ?? get("120+ fps"),
                Controller = get("controller support"),
                FullController = get("full controller")
            };
        }
    }

    /// <summary>
    /// Whether the store of Steam says a game is played with a controller, all of it or a part. It's asked for
    /// a hundred games at a time to the same public service its pages use, without any account, and kept.
    /// </summary>
    public static class SteamControllerSupport
    {
        public const int Unknown = -1;
        public const int None = 0;
        public const int Partial = 1;
        public const int Full = 2;

        public class Entry
        {
            public int Level { get; set; }
            public DateTime Checked { get; set; }
        }

        private static readonly ILogger logger = LogManager.GetLogger();
        public const string FileName = "atrio-mando.json";
        private const int FullCategory = 28;
        private const int PartialCategory = 18;
        private const int Batch = 100;
        private static readonly TimeSpan lifetime = TimeSpan.FromDays(30);
        private static readonly object fileLock = new object();
        private static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private static ConcurrentDictionary<string, Entry> entries;
        private static int refreshing;
        private static System.Threading.Timer timer;

        public static event EventHandler Changed;

        private static string FilePath => Path.Combine(PlaynitePaths.ConfigRootPath, FileName);

        private static ConcurrentDictionary<string, Entry> Entries
        {
            get
            {
                if (entries == null)
                {
                    lock (fileLock)
                    {
                        if (entries == null)
                        {
                            Dictionary<string, Entry> saved = null;
                            try
                            {
                                if (File.Exists(FilePath))
                                {
                                    saved = Playnite.SDK.Data.Serialization.FromJsonFile<Dictionary<string, Entry>>(FilePath);
                                }
                            }
                            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                            {
                                logger.Warn(e, "Failed to read what Steam said about controllers.");
                            }

                            entries = new ConcurrentDictionary<string, Entry>(saved ?? new Dictionary<string, Entry>());
                        }
                    }
                }

                return entries;
            }
        }

        public static int GetLevel(string appId)
        {
            return appId != null && Entries.TryGetValue(appId, out var entry) ? entry.Level : Unknown;
        }

        /// <summary>Played all of it with a controller, as the store of Steam or PCGamingWiki tell. Only what's already known.</summary>
        public static bool IsFullyPlayable(IEnumerable<Game> copies, string name)
        {
            var list = copies?.ToList() ?? new List<Game>();
            if (GetLevel(SteamContent.GetAppId(list)) == Full)
            {
                return true;
            }

            var wiki = PcGamingWiki.GetSaved(PcGamingWiki.GetKey(list, name));
            return wiki?.FullController == "true";
        }

        /// <summary>The ids of the store that need asking: never asked, or asked more than a month ago.</summary>
        public static List<string> GetMissing(IEnumerable<string> appIds, DateTime nowUtc)
        {
            // The mods of Steam have ids of 64 bits: they aren't in the store, and they don't fit in the question.
            return (appIds ?? Enumerable.Empty<string>()).
                Where(a => !a.IsNullOrEmpty() && int.TryParse(a, out var id) && id > 0).
                Distinct().
                Where(a => !Entries.TryGetValue(a, out var entry) || nowUtc - entry.Checked > lifetime).
                ToList();
        }

        /// <summary>What the answer says of each game asked: one it doesn't name is not in the store.</summary>
        public static Dictionary<string, int> ParseItems(string json, IEnumerable<string> asked)
        {
            var result = asked.ToDictionary(a => a, a => Unknown);
            var items = Newtonsoft.Json.Linq.JObject.Parse(json)["response"]?["store_items"] as Newtonsoft.Json.Linq.JArray;
            foreach (var item in (items ?? new Newtonsoft.Json.Linq.JArray()).OfType<Newtonsoft.Json.Linq.JObject>())
            {
                var id = (string)item["appid"] ?? (string)item["id"];
                if (id == null || !result.ContainsKey(id) || (int?)item["success"] != 1)
                {
                    continue;
                }

                var categories = (item["categories"] as Newtonsoft.Json.Linq.JObject)?["controller_categoryids"] as Newtonsoft.Json.Linq.JArray;
                var ids = categories?.Select(a => (int)a).ToList() ?? new List<int>();
                result[id] = ids.Contains(FullCategory) ? Full : ids.Contains(PartialCategory) ? Partial : None;
            }

            return result;
        }

        public static async Task RefreshAsync(IEnumerable<string> appIds, CancellationToken token)
        {
            if (Interlocked.Exchange(ref refreshing, 1) == 1)
            {
                return;
            }

            try
            {
                var missing = GetMissing(appIds, DateTime.UtcNow);
                var changed = false;
                for (var i = 0; i < missing.Count; i += Batch)
                {
                    token.ThrowIfCancellationRequested();
                    var part = missing.Skip(i).Take(Batch).ToList();
                    var request = Playnite.SDK.Data.Serialization.ToJson(new
                    {
                        ids = part.Select(a => new { appid = int.Parse(a) }).ToList(),
                        context = new { language = "english", country_code = "US" },
                        data_request = new { }
                    });

                    var url = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(request);
                    string json;
                    using (var response = await client.GetAsync(url, token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            logger.Debug($"Steam answered {(int)response.StatusCode} about controllers.");
                            break;
                        }

                        json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }

                    var now = DateTime.UtcNow;
                    foreach (var pair in ParseItems(json, part))
                    {
                        Entries[pair.Key] = new Entry { Level = pair.Value, Checked = now };
                    }

                    // Each batch is kept as it comes: a failure later doesn't lose it.
                    Save();
                    changed = true;
                    await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                }

                if (changed)
                {
                    Changed?.Invoke(null, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Debug(e, "Failed to ask Steam about controllers.");
            }
            finally
            {
                Interlocked.Exchange(ref refreshing, 0);
            }
        }

        private static void Save()
        {
            try
            {
                lock (fileLock)
                {
                    FileSystem.WriteStringToFile(FilePath, Playnite.SDK.Data.Serialization.ToJson(Entries.ToDictionary(a => a.Key, a => a.Value)));
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, "Failed to keep what Steam said about controllers.");
            }
        }

        /// <summary>A minute and a half after the start, and then once a day, only the games still unknown.</summary>
        public static void Attach(Func<IEnumerable<Game>> games)
        {
            if (timer != null || AtrioInfo.GetTestSetting("ATRIO_SIN_RED") != null)
            {
                return;
            }

            timer = new System.Threading.Timer(_ =>
            {
                try
                {
                    var ids = games()?.Where(a => a.PluginId == SteamContent.SteamPluginId).Select(a => a.GameId).ToList() ?? new List<string>();
                    RefreshAsync(ids, CancellationToken.None).Wait();
                }
                catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Debug(e, "Failed to refresh the controllers of the games.");
                }
            }, null, TimeSpan.FromSeconds(90), TimeSpan.FromHours(24));
        }
    }
}
