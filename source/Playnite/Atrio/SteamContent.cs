using Microsoft.Win32;
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
    public class SteamNewsItem
    {
        public string Title { get; set; }
        public string Text { get; set; }
        public string Url { get; set; }
        public string ImageUrl { get; set; }
        public DateTime Date { get; set; }
    }

    /// <summary>
    /// What Steam publishes about a game for anyone to read: its artwork and its news. No account or key
    /// is involved. The time played in the last two weeks is read from the files of the local client.
    /// </summary>
    /// <summary>A trailer of the store of Steam: the video in pieces (DASH), in H.264 with AAC and in AV1.</summary>
    public class SteamTrailer
    {
        public string Name { get; set; }
        public string DashH264 { get; set; }
        public string DashAv1 { get; set; }
        public string Thumbnail { get; set; }
        public bool Highlight { get; set; }
    }

    public static class SteamContent
    {
        private class NewsResponse
        {
            public NewsList appnews { get; set; }
        }

        private class NewsList
        {
            public List<NewsEntry> newsitems { get; set; }
        }

        private class NewsEntry
        {
            public string title { get; set; }
            public string url { get; set; }
            public string contents { get; set; }
            public long date { get; set; }
        }

        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly ConcurrentDictionary<string, Tuple<DateTime, List<SteamNewsItem>>> newsCache =
            new ConcurrentDictionary<string, Tuple<DateTime, List<SteamNewsItem>>>();
        private static readonly TimeSpan newsLifetime = TimeSpan.FromHours(1);
        private static readonly TimeSpan missingArtLifetime = TimeSpan.FromDays(7);
        private static readonly Regex tagRegex = new Regex(@"\[/?[a-zA-Z0-9*]+(=[^\]]*|(\s+[a-zA-Z0-9_-]+=[^\]]*)+)?\]|<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex imageRegex = new Regex(@"\[img\]\s*(?<url>[^\[\s]+)\s*\[/img\]|\[img\s+src=""(?<url>[^""]+)""[^\]]*\](\s*\[/img\])?|<img[^>]+src=""(?<url>[^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex configTokenRegex = new Regex(@"""(?<text>(?:[^""\\]|\\.)*)""|[{}]", RegexOptions.Compiled);
        private const string ArtUrl = "https://cdn.akamai.steamstatic.com/steam/apps/{0}/{1}";
        private const string NewsUrl = "https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/?appid={0}&count={1}&maxlength=0&format=json&feeds=steam_community_announcements";
        private const string ClanImages = "https://clan.akamai.steamstatic.com/images/";

        private static Dictionary<string, int> recentMinutes;
        private static string recentSource;
        private static DateTime recentSourceTime;

        public static readonly Guid SteamPluginId = Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB");

        public const string HeroFile = "library_hero.jpg";
        public const string WideHeroFile = "library_hero_2x.jpg";
        public const string LogoFile = "logo.png";
        public const string HeaderFile = "header.jpg";

        /// <summary>The Steam id of the first copy that has one, null for a game that isn't on Steam.</summary>
        public static string GetAppId(IEnumerable<Game> copies)
        {
            var game = copies?.FirstOrDefault(a => a.PluginId == SteamPluginId);
            return game != null && int.TryParse(game.GameId, out _) ? game.GameId : null;
        }

        public static string GetArtUrl(string appId, string file)
        {
            return string.Format(ArtUrl, appId, file);
        }

        private static string GetArtPath(string appId, string file)
        {
            return Path.Combine(PlaynitePaths.DataCachePath, "steam-art", $"{appId}-{file}");
        }

        /// <summary>Path of a picture of the game if it's already saved, which takes no waiting at all.</summary>
        public static string GetSavedArt(string appId, string file)
        {
            var path = GetArtPath(appId, file);
            return File.Exists(path) ? path : null;
        }

        /// <summary>Path of a picture of the game, saved after the first time. Null when Steam doesn't have it.</summary>
        public static async Task<string> GetArtAsync(string appId, string file, CancellationToken token)
        {
            var path = GetArtPath(appId, file);
            var folder = Path.GetDirectoryName(path);
            var missing = path + ".none";
            if (File.Exists(path))
            {
                return path;
            }

            if (File.Exists(missing) && DateTime.Now - File.GetLastWriteTime(missing) < missingArtLifetime)
            {
                return null;
            }

            using (var response = await client.GetAsync(GetArtUrl(appId, file), token).ConfigureAwait(false))
            {
                FileSystem.CreateDirectory(folder);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    File.WriteAllText(missing, string.Empty);
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var temporary = path + ".part";
                using (var file2 = File.Create(temporary))
                {
                    await response.Content.CopyToAsync(file2).ConfigureAwait(false);
                }

                File.Delete(path);
                File.Move(temporary, path);
                return path;
            }
        }

        /// <summary>
        /// Some games publish a whole picture where the logo should be. Only one with a transparent
        /// background can go on top of the art: PNG says so in the color type of its header.
        /// </summary>
        public static bool IsTransparentPng(byte[] header)
        {
            const int colorTypeOffset = 25;
            return header.Length > colorTypeOffset && (header[colorTypeOffset] == 4 || header[colorTypeOffset] == 6);
        }

        /// <summary>Path of the big picture of the game, in the best size that it has.</summary>
        public static async Task<string> GetHeroAsync(string appId, CancellationToken token)
        {
            return await GetArtAsync(appId, WideHeroFile, token).ConfigureAwait(false) ??
                   await GetArtAsync(appId, HeroFile, token).ConfigureAwait(false);
        }

        /// <summary>Path of the logo of the game, when it has a real one.</summary>
        public static async Task<string> GetLogoAsync(string appId, CancellationToken token)
        {
            var path = await GetArtAsync(appId, LogoFile, token).ConfigureAwait(false);
            return path != null && IsRealLogo(path) ? path : null;
        }

        /// <summary>Path of the logo of the game if it's already saved and it's a real one.</summary>
        public static string GetSavedLogo(string appId)
        {
            var path = GetSavedArt(appId, LogoFile);
            return path != null && IsRealLogo(path) ? path : null;
        }

        private static bool IsRealLogo(string path)
        {
            try
            {
                var header = new byte[32];
                using (var file = File.OpenRead(path))
                {
                    file.Read(header, 0, header.Length);
                }

                return IsTransparentPng(header);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                logger.Debug(e, $"Failed to read the logo {path}.");
                return false;
            }
        }

        private const string MoviesUrl = "https://store.steampowered.com/api/appdetails?appids={0}&filters=movies";
        private static readonly ConcurrentDictionary<string, Tuple<DateTime, List<SteamTrailer>>> trailerCache =
            new ConcurrentDictionary<string, Tuple<DateTime, List<SteamTrailer>>>();

        /// <summary>The trailers of the page of the game in the store, the highlighted ones first. Asked only when wanted.</summary>
        public static async Task<List<SteamTrailer>> GetTrailersAsync(string appId, CancellationToken token)
        {
            if (trailerCache.TryGetValue(appId, out var cached) && DateTime.Now - cached.Item1 < TimeSpan.FromHours(12))
            {
                return cached.Item2;
            }

            var json = await client.GetStringAsync(string.Format(MoviesUrl, appId)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var trailers = ParseTrailers(json, appId);
            trailerCache[appId] = Tuple.Create(DateTime.Now, trailers);
            return trailers;
        }

        public static List<SteamTrailer> ParseTrailers(string json, string appId)
        {
            var result = new List<SteamTrailer>();
            try
            {
                // Steam sometimes answers "data": [] instead of an object: each step is checked.
                var app = Newtonsoft.Json.Linq.JObject.Parse(json)?[appId] as Newtonsoft.Json.Linq.JObject;
                var data = app?["data"] as Newtonsoft.Json.Linq.JObject;
                var movies = data?["movies"] as Newtonsoft.Json.Linq.JArray;
                foreach (var movie in movies ?? new Newtonsoft.Json.Linq.JArray())
                {
                    var trailer = new SteamTrailer
                    {
                        Name = (string)movie["name"],
                        DashH264 = (string)movie["dash_h264"],
                        DashAv1 = (string)movie["dash_av1"],
                        Thumbnail = (string)movie["thumbnail"],
                        Highlight = (bool?)movie["highlight"] == true
                    };

                    if (!trailer.DashH264.IsNullOrEmpty() || !trailer.DashAv1.IsNullOrEmpty())
                    {
                        result.Add(trailer);
                    }
                }
            }
            catch (Newtonsoft.Json.JsonException)
            {
                // Steam answered something else, like a page of limits: no trailers this time.
            }

            return result.OrderByDescending(a => a.Highlight).ToList();
        }

        public static async Task<List<SteamNewsItem>> GetNewsAsync(string appId, int count, CancellationToken token)
        {
            if (newsCache.TryGetValue(appId, out var cached) && DateTime.Now - cached.Item1 < newsLifetime && cached.Item2.Count >= count)
            {
                return cached.Item2;
            }

            var json = await client.GetStringAsync(string.Format(NewsUrl, appId, count)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var entries = Playnite.SDK.Data.Serialization.FromJson<NewsResponse>(json)?.appnews?.newsitems ?? new List<NewsEntry>();
            var news = entries.Select(a => ParseNews(a)).ToList();
            newsCache[appId] = Tuple.Create(DateTime.Now, news);
            return news;
        }

        private static SteamNewsItem ParseNews(NewsEntry entry)
        {
            var image = imageRegex.Match(entry.contents ?? string.Empty).Groups["url"].Value;
            return new SteamNewsItem
            {
                Title = entry.title,
                Url = entry.url,
                Date = DateTimeOffset.FromUnixTimeSeconds(entry.date).LocalDateTime,
                ImageUrl = image.IsNullOrEmpty() ? null : image.Replace("{STEAM_CLAN_IMAGE}/", ClanImages),
                Text = GetExcerpt(entry.contents)
            };
        }

        /// <summary>The start of a post in plain words: posts come with their own markup, pictures and links.</summary>
        public static string GetExcerpt(string contents, int length = 240)
        {
            var text = imageRegex.Replace(contents ?? string.Empty, " ");
            text = tagRegex.Replace(text, " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length <= length ? text : text.Substring(0, length).TrimEnd() + "…";
        }

        /// <summary>Minutes played in the last two weeks, as the local client counts them. Null if it doesn't say.</summary>
        public static int? GetRecentMinutes(string appId)
        {
            try
            {
                var source = FindLocalConfig();
                if (source == null)
                {
                    return null;
                }

                var time = File.GetLastWriteTime(source);
                if (recentMinutes == null || source != recentSource || time != recentSourceTime)
                {
                    recentMinutes = ParseRecentMinutes(File.ReadAllText(source));
                    recentSource = source;
                    recentSourceTime = time;
                }

                return recentMinutes.TryGetValue(appId, out var minutes) ? minutes : (int?)null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                logger.Debug(e, "Failed to read the recent playtime of the Steam client.");
                return null;
            }
        }

        public static Dictionary<string, int> ParseRecentMinutes(string config)
        {
            var result = new Dictionary<string, int>();
            var parents = new Stack<string>();
            string key = null;
            foreach (Match token in configTokenRegex.Matches(config))
            {
                if (token.Value == "{")
                {
                    parents.Push(key);
                    key = null;
                }
                else if (token.Value == "}")
                {
                    if (parents.Count > 0)
                    {
                        parents.Pop();
                    }

                    key = null;
                }
                else if (key == null)
                {
                    key = token.Groups["text"].Value;
                }
                else
                {
                    // The entries of a game come in any order, some of them being whole blocks.
                    if (key == "Playtime2wks" && parents.Count > 0 && int.TryParse(parents.Peek(), out _) &&
                        int.TryParse(token.Groups["text"].Value, out var minutes))
                    {
                        result[parents.Peek()] = minutes;
                    }

                    key = null;
                }
            }

            return result;
        }

        /// <summary>The account that used the client last is the one that matters on a shared computer.</summary>
        private static string FindLocalConfig()
        {
            var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            var users = steam.IsNullOrEmpty() ? null : Path.Combine(steam, "userdata");
            if (users == null || !Directory.Exists(users))
            {
                return null;
            }

            return Directory.GetDirectories(users).
                Select(a => Path.Combine(a, "config", "localconfig.vdf")).
                Where(File.Exists).
                OrderByDescending(File.GetLastWriteTime).
                FirstOrDefault();
        }
    }
}
