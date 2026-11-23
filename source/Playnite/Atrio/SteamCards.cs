using AngleSharp.Dom;
using AngleSharp.Parser.Html;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    /// <summary>A trading card of a game of Steam, and how many of it the person has.</summary>
    public class SteamCard
    {
        public string Name { get; set; }
        public string ImageUrl { get; set; }

        /// <summary>The big picture of the card, which only some sources give.</summary>
        public string ArtworkUrl { get; set; }
        public int Owned { get; set; }

        /// <summary>How the market calls it. Null when the source doesn't say.</summary>
        public string MarketHash { get; set; }

        [JsonIgnore]
        public bool IsOwned => Owned > 0;
    }

    /// <summary>The badge of a game of Steam, the one that is made with its cards.</summary>
    public class SteamBadge
    {
        public string AppId { get; set; }

        /// <summary>The name of the level the person has, or of the first one when there's none yet.</summary>
        public string Name { get; set; }
        public string IconUrl { get; set; }
        public int Level { get; set; }
        public int Xp { get; set; }
        public int MaxLevel { get; set; } = 5;
        public string NextLevelName { get; set; }
        public int NextLevelXp { get; set; }

        /// <summary>When it was made, in the words of Steam.</summary>
        public string UnlockedText { get; set; }

        /// <summary>The cards that playing still gives. Null when only the account knows it.</summary>
        public int? DropsLeft { get; set; }
        public List<SteamCard> Cards { get; set; } = new List<SteamCard>();

        /// <summary>When it was read, or when Steam wrote it.</summary>
        public DateTime Read { get; set; }

        /// <summary>What the client had written about it, to know if it's the same the next time. The file also keeps other things.</summary>
        [JsonIgnore]
        public string Stamp { get; set; }

        [JsonIgnore]
        public int OwnedTotal => Cards.Sum(a => a.Owned);

        [JsonIgnore]
        public int OwnedKinds => Cards.Count(a => a.IsOwned);

        [JsonIgnore]
        public bool IsMaxed => Level >= MaxLevel && MaxLevel > 0;

        /// <summary>One of each: the next level can be made.</summary>
        [JsonIgnore]
        public bool CanCraft => !IsMaxed && Cards.Count > 0 && OwnedKinds == Cards.Count;
    }

    public enum SteamCardsState
    {
        Unknown,
        Ready,

        /// <summary>The game has no cards.</summary>
        None,

        /// <summary>The game has cards, and the profile of the person doesn't show which ones they have.</summary>
        Private,

        /// <summary>Steam asks to wait, or there is no connection.</summary>
        Failed
    }

    public class SteamCardsResult
    {
        public SteamCardsState State { get; set; }
        public SteamBadge Badge { get; set; }
    }

    /// <summary>
    /// The trading cards and the badge of each game of Steam. First what its client keeps on this computer, which needs
    /// nothing; then the public page of the profile, and the one of the account if the browser of Atrio has a session: only
    /// that one says how many cards are left to get. Nothing is written in Steam and no access is kept.
    /// </summary>
    public static class SteamCards
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly HttpClient client = CreateClient();
        private static readonly SemaphoreSlim asking = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim askingAccount = new SemaphoreSlim(1, 1);
        private static readonly ConcurrentDictionary<string, SteamCardsResult> known = new ConcurrentDictionary<string, SteamCardsResult>();
        private static readonly TimeSpan lifetime = TimeSpan.FromHours(6);
        private static readonly TimeSpan dropsLifetime = TimeSpan.FromHours(12);
        private static readonly TimeSpan accountRetry = TimeSpan.FromMinutes(30);

        /// <summary>Older than this, what the account said isn't told anymore.</summary>
        private static readonly TimeSpan dropsLimit = TimeSpan.FromHours(36);
        private static readonly TimeSpan closedLimit = TimeSpan.FromDays(30);
        private static readonly TimeSpan noneLifetime = TimeSpan.FromDays(30);
        private static readonly TimeSpan pause = TimeSpan.FromMilliseconds(700);
        private static readonly TimeSpan penalty = TimeSpan.FromMinutes(15);
        private const ulong FirstSteamId = 76561197960265728;
        private const int CardsCategory = 29;
        private static DateTime nextQuestion;

        private class Saved
        {
            public SteamCardsState State { get; set; }
            public SteamBadge Badge { get; set; }
            public DateTime Read { get; set; }

            /// <summary>When the account said how many cards are left, and how many there were then.</summary>
            public DateTime DropsRead { get; set; }
            public int DropsOwned { get; set; }
            public DateTime AccountTried { get; set; }

            /// <summary>The account is asked as soon as it can be, whenever it was asked before.</summary>
            public bool AccountAgain { get; set; }

            /// <summary>What the client had when the page was read.</summary>
            public string ClientStamp { get; set; }
        }

        /// <summary>Tests point it somewhere else.</summary>
        public static string TestSteamPath { get; set; }

        private static HttpClient CreateClient()
        {
            // Without cookies: the language of the page is in its address, and nothing is remembered between questions.
            var handler = new HttpClientHandler
            {
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            var result = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            result.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Atrio");
            return result;
        }

        public static string FindSteamPath()
        {
            return TestSteamPath ?? SteamCollections.FindSteamPath();
        }

        /// <summary>The account of the client: the one signed in now, or the one that used it last. Null without Steam.</summary>
        public static string FindAccountId()
        {
            var steam = FindSteamPath();
            var users = steam == null ? null : Path.Combine(steam, "userdata");
            if (users == null || !Directory.Exists(users))
            {
                return null;
            }

            try
            {
                if (TestSteamPath == null &&
                    Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", 0) is int active && active != 0 &&
                    Directory.Exists(Path.Combine(users, unchecked((uint)active).ToString())))
                {
                    return unchecked((uint)active).ToString();
                }

                return Directory.GetDirectories(users).
                    Where(a => Path.GetFileName(a).All(char.IsDigit) && Path.GetFileName(a) != "0").
                    Select(a => Tuple.Create(Path.GetFileName(a), Path.Combine(a, "config", "localconfig.vdf"))).
                    Where(a => File.Exists(a.Item2)).
                    OrderByDescending(a => File.GetLastWriteTimeUtc(a.Item2)).
                    Select(a => a.Item1).
                    FirstOrDefault();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException)
            {
                logger.Debug(e, "Failed to find the account of Steam.");
                return null;
            }
        }

        public static string GetSteamId(string accountId)
        {
            return ulong.TryParse(accountId, out var id) && id > 0 ? (id + FirstSteamId).ToString() : null;
        }

        /// <summary>What the client of Steam wrote about the badge of a game the last time it showed it. Null if it never did.</summary>
        public static SteamBadge ReadClient(string accountId, string appId)
        {
            var steam = FindSteamPath();
            if (steam == null || accountId == null || !appId.All(char.IsDigit))
            {
                return null;
            }

            try
            {
                var path = Path.Combine(steam, "userdata", accountId, "config", "librarycache", appId + ".json");
                if (!File.Exists(path))
                {
                    return null;
                }

                var badge = ParseClient(File.ReadAllText(path), appId);
                if (badge != null)
                {
                    badge.Read = File.GetLastWriteTimeUtc(path);
                }

                return badge;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException || e is ArgumentException ||
                e is FormatException || e is InvalidCastException || e is OverflowException)
            {
                logger.Debug(e, $"Failed to read what Steam keeps about {appId}.");
                return null;
            }
        }

        public static SteamBadge ParseClient(string json, string appId)
        {
            // A list of pairs: the name of each part and what's in it.
            if (!(JToken.Parse(json) is JArray parts))
            {
                return null;
            }

            var data = parts.OfType<JArray>().
                Where(a => a.Count == 2 && a[0].Type == JTokenType.String && (string)a[0] == "badge").
                Select(a => (a[1] as JObject)?["data"] as JObject).
                FirstOrDefault(a => a != null);
            if (!(data?["rgCards"] is JArray cards) || cards.Count == 0)
            {
                return null;
            }

            var level = (int?)data["nLevel"] ?? 0;
            var icon = (string)data["strIconURL"];
            return new SteamBadge
            {
                AppId = appId,
                Stamp = GetStamp(data.ToString(Formatting.None)),
                Level = level,
                Xp = (int?)data["nXP"] ?? 0,
                MaxLevel = (int?)data["nMaxLevel"] ?? 5,
                Name = level > 0 ? Text(data["strName"]) : null,
                IconUrl = level > 0 ? SteamMarkup.SafeUrl(icon) : null,
                NextLevelName = level >= ((int?)data["nMaxLevel"] ?? 5) ? null : Text(data["strNextLevelName"]),
                NextLevelXp = (int?)data["nNextLevelXP"] ?? 0,
                Cards = cards.OfType<JObject>().Select(a => new SteamCard
                {
                    Name = Text(a["strTitle"]) ?? Text(a["strName"]),
                    ImageUrl = SteamMarkup.SafeUrl((string)a["strImgURL"]),
                    ArtworkUrl = SteamMarkup.SafeUrl((string)a["strArtworkURL"]),
                    MarketHash = Text(a["strMarketHash"]),
                    Owned = (int?)a["nOwned"] ?? 0
                }).ToList()
            };
        }

        private static string GetStamp(string text)
        {
            using (var sha = SHA1.Create())
            {
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
            }
        }

        private static string Text(JToken token)
        {
            var text = ((string)token)?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        private static readonly Regex numberRegex = new Regex(@"\d+", RegexOptions.Compiled);

        /// <summary>The page of the cards of a game in a profile. Null when the page is another one.</summary>
        public static SteamBadge ParsePage(string html, string appId)
        {
            var page = new HtmlParser().Parse(html ?? string.Empty);
            var row = page.QuerySelector(".badge_gamecard_page");
            if (row == null)
            {
                return null;
            }

            var badge = new SteamBadge { AppId = appId };
            var current = row.QuerySelector(".badge_current");
            var title = Clean(current?.QuerySelector(".badge_info_title"));
            if (title != null)
            {
                // «Nivel 5, 500 EXP»: the same two numbers in every language.
                var numbers = numberRegex.Matches((Clean(current.QuerySelector(".badge_info_description > div:nth-child(2)")) ?? string.Empty).Replace(".", string.Empty)).
                    Cast<Match>().Select(a => int.TryParse(a.Value, out var number) ? number : 0).ToList();
                badge.Name = title;
                badge.IconUrl = SteamMarkup.SafeUrl(current.QuerySelector(".badge_info_image img")?.GetAttribute("src"));
                badge.Level = Math.Max(1, numbers.Count > 0 ? numbers[0] : 1);
                badge.Xp = numbers.Count > 1 ? numbers[1] : 0;
                badge.UnlockedText = Clean(current.QuerySelector(".badge_info_unlocked"));
            }
            else
            {
                var empty = current?.QuerySelectorAll(".badge_empty_name").Select(Clean).ToList() ?? new List<string>();
                badge.NextLevelName = empty.FirstOrDefault();
                var xp = empty.Count > 1 ? numberRegex.Match(empty[1] ?? string.Empty) : Match.Empty;
                badge.NextLevelXp = xp.Success && int.TryParse(xp.Value, out var value) ? value : 100;
            }

            foreach (var card in row.QuerySelectorAll(".badge_card_set_card"))
            {
                var name = card.QuerySelector(".badge_card_set_title");
                var quantity = name?.QuerySelector(".badge_card_set_text_qty");
                var owned = quantity == null ? (card.ClassList.Contains("owned") ? 1 : 0) :
                    int.TryParse(numberRegex.Match(quantity.TextContent ?? string.Empty).Value, out var count) ? count : 1;
                quantity?.Remove();
                badge.Cards.Add(new SteamCard
                {
                    Name = Clean(name),
                    ImageUrl = SteamMarkup.SafeUrl(card.QuerySelector("img.gamecard")?.GetAttribute("src")),
                    Owned = card.ClassList.Contains("unowned") ? 0 : owned
                });
            }

            // Only the page of the account says it, with a number or with the words of «none left».
            var drops = row.QuerySelector(".badge_title_stats_drops .progress_info_bold");
            if (drops != null)
            {
                var left = numberRegex.Match(drops.TextContent ?? string.Empty);
                badge.DropsLeft = left.Success && int.TryParse(left.Value, out var number) ? number : 0;
            }

            return badge;
        }

        private static string Clean(IElement element)
        {
            var text = element == null ? null : Regex.Replace(element.TextContent ?? string.Empty, @"\s+", " ").Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        /// <summary>The page of the list of badges is where Steam goes when a game has no cards.</summary>
        public static bool IsBadgeList(Uri address)
        {
            return address != null && Regex.IsMatch(address.AbsolutePath, @"/badges/?$", RegexOptions.IgnoreCase);
        }

        /// <summary>
        /// The newer of the two, with the big pictures and the names of the market that only the client has. The file of the
        /// client is written for other reasons too: only what it says that it didn't say before is news.
        /// </summary>
        public static SteamBadge Merge(SteamBadge client, SteamBadge page, string clientStamp = null)
        {
            // A profile that doesn't show the cards still shows the rest of the page.
            if (page == null || client == null || page.Cards.Count == 0)
            {
                return page?.Cards.Count > 0 ? page : client ?? page;
            }

            foreach (var card in page.Cards)
            {
                var same = client.Cards.FirstOrDefault(a => a.Name == card.Name) ?? client.Cards.FirstOrDefault(a => a.ImageUrl != null && a.ImageUrl == card.ImageUrl);
                card.ArtworkUrl = card.ArtworkUrl ?? same?.ArtworkUrl;
                card.MarketHash = card.MarketHash ?? same?.MarketHash;
            }

            if (client.Read > page.Read && client.Cards.Count > 0 && (clientStamp == null || client.Stamp != clientStamp))
            {
                // The client was opened after the page was read: what it has is what counts.
                client.DropsLeft = client.OwnedTotal == page.OwnedTotal ? page.DropsLeft : null;
                client.UnlockedText = client.Level == page.Level ? page.UnlockedText : null;
                return client;
            }

            page.MaxLevel = client.MaxLevel > 0 ? client.MaxLevel : page.MaxLevel;
            if (client.Level == page.Level && !page.IsMaxed)
            {
                page.NextLevelName = page.NextLevelName ?? client.NextLevelName;
                page.NextLevelXp = page.NextLevelXp > 0 ? page.NextLevelXp : client.NextLevelXp;
            }

            return page;
        }

        private static string CachePath(string accountId, string appId)
        {
            return Path.Combine(PlaynitePaths.DataCachePath, "cromos", $"{accountId}-{appId}.json");
        }

        private static Saved ReadSaved(string accountId, string appId)
        {
            try
            {
                var path = CachePath(accountId, appId);
                return File.Exists(path) ? JsonConvert.DeserializeObject<Saved>(File.ReadAllText(path)) : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException)
            {
                logger.Debug(e, $"Failed to read the saved cards of {appId}.");
                return null;
            }
        }

        private static void Save(string accountId, string appId, Saved saved)
        {
            try
            {
                var path = CachePath(accountId, appId);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(saved));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                logger.Debug(e, $"Failed to save the cards of {appId}.");
            }
        }

        private static SteamCardsResult Build(string accountId, string appId, Saved saved)
        {
            var badge = Merge(ReadClient(accountId, appId), saved?.Badge, saved?.ClientStamp);
            if (badge != null && badge.Cards.Count > 0)
            {
                // What the account said a while ago, or before getting another card, isn't told as if it was now.
                if (saved == null || DateTime.UtcNow - saved.DropsRead > dropsLimit || badge.OwnedTotal != saved.DropsOwned)
                {
                    badge.DropsLeft = null;
                }

                return new SteamCardsResult { State = SteamCardsState.Ready, Badge = badge };
            }

            return new SteamCardsResult { State = saved?.State ?? SteamCardsState.Unknown };
        }

        /// <summary>What was seen since Atrio opened, at once: it comes with the page of the game. Null when it wasn't.</summary>
        public static SteamCardsResult GetFromMemory(string appId)
        {
            return appId != null && known.TryGetValue($"{FindAccountId()}-{appId}", out var result) ? result : null;
        }

        /// <summary>What is already known, without asking anyone. It reads files: not for the thread of the window.</summary>
        public static SteamCardsResult GetKnown(string appId)
        {
            var accountId = FindAccountId();
            if (accountId == null || appId == null)
            {
                return new SteamCardsResult { State = SteamCardsState.Unknown };
            }

            return known.TryGetValue($"{accountId}-{appId}", out var result) ? result : Build(accountId, appId, ReadSaved(accountId, appId));
        }

        /// <summary>What is known, asking Steam when it's time.</summary>
        public static async Task<SteamCardsResult> GetAsync(string appId, string language, CancellationToken token)
        {
            var accountId = FindAccountId();
            if (accountId == null || appId == null)
            {
                return new SteamCardsResult { State = SteamCardsState.Unknown };
            }

            var key = $"{accountId}-{appId}";
            var saved = ReadSaved(accountId, appId);
            var age = saved?.State == SteamCardsState.None ? noneLifetime : lifetime;
            if (saved == null || DateTime.UtcNow - saved.Read > age)
            {
                await asking.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    saved = await AskAsync(accountId, appId, language, token).ConfigureAwait(false);
                }
                finally
                {
                    asking.Release();
                }
            }

            var result = Build(accountId, appId, saved);
            if (result.State != SteamCardsState.Failed)
            {
                known[key] = result;
            }

            return result;
        }

        /// <summary>Asked again: after signing in, or by hand.</summary>
        public static void Forget(string appId)
        {
            var accountId = FindAccountId();
            if (accountId != null && appId != null)
            {
                known.TryRemove($"{accountId}-{appId}", out _);
                var saved = ReadSaved(accountId, appId);
                if (saved != null)
                {
                    saved.Read = DateTime.MinValue;
                    saved.AccountAgain = true;
                    Save(accountId, appId, saved);
                }
            }
        }

        /// <summary>The public page of a profile, and where it ended. Null without an answer, or while Steam asks to wait.</summary>
        private static async Task<Tuple<Uri, string>> DownloadAsync(string url, CancellationToken token)
        {
            if (DateTime.UtcNow < nextQuestion)
            {
                return null;
            }

            await Task.Delay(pause, token).ConfigureAwait(false);
            using (var response = await client.GetAsync(url, token).ConfigureAwait(false))
            {
                if ((int)response.StatusCode == 429)
                {
                    // Steam asks to wait: everyone waits, not only this game.
                    nextQuestion = DateTime.UtcNow + penalty;
                    logger.Info("Steam asked to wait before more questions about cards.");
                    return null;
                }

                return response.IsSuccessStatusCode ?
                    Tuple.Create(response.RequestMessage?.RequestUri, await response.Content.ReadAsStringAsync().ConfigureAwait(false)) :
                    null;
            }
        }

        private enum PageKind
        {
            /// <summary>The cards and the badge.</summary>
            Cards,

            /// <summary>The profile, or what it has, isn't for everyone to see.</summary>
            Closed,

            /// <summary>The game has no cards.</summary>
            None,

            /// <summary>An error of Steam, or a page that isn't known.</summary>
            Other
        }

        private static PageKind GetKind(Uri address, string html, SteamBadge badge)
        {
            return badge?.Cards.Count > 0 ? PageKind.Cards :
                badge != null || html.IndexOf("profile_private_info", StringComparison.Ordinal) > 0 ? PageKind.Closed :
                IsBadgeList(address) && html.IndexOf("profile_badges_header", StringComparison.Ordinal) > 0 ? PageKind.None : PageKind.Other;
        }

        private static async Task<Saved> AskAsync(string accountId, string appId, string language, CancellationToken token)
        {
            try
            {
                var page = await DownloadAsync($"https://steamcommunity.com/profiles/{GetSteamId(accountId)}/gamecards/{appId}/?l={language}", token).ConfigureAwait(false);
                if (page == null)
                {
                    return ReadSaved(accountId, appId) ?? new Saved { State = SteamCardsState.Failed };
                }

                // The account may have answered meanwhile: what is kept is read now.
                var badge = ParsePage(page.Item2, appId);
                var kind = GetKind(page.Item1, page.Item2, badge);
                var closed = kind != PageKind.Closed ? SteamCardsState.Unknown :
                    badge != null ? SteamCardsState.Private : await HasCardsAsync(appId, token).ConfigureAwait(false);
                var result = ReadSaved(accountId, appId) ?? new Saved();
                result.Read = DateTime.UtcNow;
                switch (kind)
                {
                    case PageKind.Cards:
                        badge.Read = DateTime.UtcNow;
                        badge.DropsLeft = badge.OwnedTotal == result.DropsOwned ? result.Badge?.DropsLeft : null;
                        result.State = SteamCardsState.Ready;
                        result.Badge = badge;
                        result.ClientStamp = ReadClient(accountId, appId)?.Stamp;
                        break;
                    case PageKind.None:
                        result.State = SteamCardsState.None;
                        result.Badge = null;
                        break;
                    case PageKind.Closed:
                        result.State = closed;
                        result.Badge = result.Badge?.Cards.Count > 0 && DateTime.UtcNow - result.Badge.Read < closedLimit ? result.Badge : null;
                        break;
                    default:
                        // It's asked again in a while; what was known stays.
                        result.Read = DateTime.UtcNow - lifetime + accountRetry;
                        break;
                }

                Save(accountId, appId, result);
                return result;
            }
            catch (Exception e) when (!token.IsCancellationRequested && (e is HttpRequestException || e is OperationCanceledException || e is JsonException))
            {
                logger.Debug(e, $"Failed to ask Steam for the cards of {appId}.");
                return ReadSaved(accountId, appId) ?? new Saved { State = SteamCardsState.Failed };
            }
        }

        /// <summary>The foil badge of a game and its cards, from the public page. Only asked when the person wants to see them.</summary>
        public static async Task<SteamCardsResult> GetFoilAsync(string appId, string language, CancellationToken token)
        {
            var accountId = FindAccountId();
            if (accountId == null || appId == null)
            {
                return new SteamCardsResult { State = SteamCardsState.Unknown };
            }

            var key = $"{accountId}-{appId}-foil";
            if (known.TryGetValue(key, out var seen) && DateTime.UtcNow - seen.Badge.Read < accountRetry)
            {
                return seen;
            }

            await asking.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var page = await DownloadAsync($"https://steamcommunity.com/profiles/{GetSteamId(accountId)}/gamecards/{appId}/?l={language}&border=1", token).ConfigureAwait(false);
                var badge = page == null ? null : ParsePage(page.Item2, appId);
                var kind = page == null ? PageKind.Other : GetKind(page.Item1, page.Item2, badge);
                if (kind != PageKind.Cards)
                {
                    return new SteamCardsResult { State = kind == PageKind.Closed ? SteamCardsState.Private : kind == PageKind.None ? SteamCardsState.None : SteamCardsState.Failed };
                }

                badge.Read = DateTime.UtcNow;
                badge.MaxLevel = 1;
                return known[key] = new SteamCardsResult { State = SteamCardsState.Ready, Badge = badge };
            }
            catch (Exception e) when (!token.IsCancellationRequested && (e is HttpRequestException || e is OperationCanceledException))
            {
                logger.Debug(e, $"Failed to ask Steam for the foil cards of {appId}.");
                return new SteamCardsResult { State = SteamCardsState.Failed };
            }
            finally
            {
                asking.Release();
            }
        }

        private const string ItemsUrl = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json={0}";

        /// <summary>Whether the store says that the game has cards, for the profiles that don't show them.</summary>
        private static async Task<SteamCardsState> HasCardsAsync(string appId, CancellationToken token)
        {
            var question = new JObject
            {
                ["ids"] = new JArray(new JObject { ["appid"] = int.Parse(appId) }),
                ["context"] = new JObject { ["language"] = "english", ["country_code"] = "US" },
                ["data_request"] = new JObject { ["include_basic_info"] = true }
            };
            using (var response = await client.GetAsync(string.Format(ItemsUrl, Uri.EscapeDataString(question.ToString(Formatting.None))), token).ConfigureAwait(false))
            {
                return response.IsSuccessStatusCode ? ParseCategories(await response.Content.ReadAsStringAsync().ConfigureAwait(false)) : SteamCardsState.Unknown;
            }
        }

        public static SteamCardsState ParseCategories(string json)
        {
            var item = (JObject.Parse(json)["response"]?["store_items"] as JArray)?.OfType<JObject>().FirstOrDefault();
            if (!(item?["categories"] is JObject categories))
            {
                return SteamCardsState.Unknown;
            }

            return categories.Properties().Select(a => a.Value).OfType<JArray>().SelectMany(a => a).Any(a => a.Type == JTokenType.Integer && (int)a == CardsCategory) ?
                SteamCardsState.Private : SteamCardsState.None;
        }

        /// <summary>
        /// The same page with the session of the browser of Atrio, for what only the account sees: the cards left to get, and
        /// everything when the profile is private. It comes after the rest of the page, a few times a day for each game.
        /// </summary>
        public static async Task<SteamCardsResult> GetFromAccountAsync(string appId, Func<string, SteamSessionPage> loadPage, CancellationToken token)
        {
            var accountId = FindAccountId();
            if (accountId == null || appId == null)
            {
                return null;
            }

            if (!IsAccountDue(ReadSaved(accountId, appId)))
            {
                return null;
            }

            await askingAccount.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // Another page of the same game may have asked it meanwhile.
                if (!IsAccountDue(ReadSaved(accountId, appId)))
                {
                    return null;
                }

                // Without a language: the one the person has in Steam stays as it is.
                var page = await Task.Run(() => loadPage($"https://steamcommunity.com/my/gamecards/{appId}/"), token).ConfigureAwait(false);
                var badge = ParsePage(page?.Source, appId);
                var saved = ReadSaved(accountId, appId) ?? new Saved { State = SteamCardsState.Unknown };
                saved.AccountTried = DateTime.UtcNow;
                saved.AccountAgain = false;
                if (badge == null || badge.Cards.Count == 0 || !IsSameAccount(page?.Address, page?.Source, accountId))
                {
                    Save(accountId, appId, saved);
                    return null;
                }

                badge.Read = DateTime.UtcNow;
                badge.DropsLeft = badge.DropsLeft ?? 0;
                saved.State = SteamCardsState.Ready;
                saved.Badge = badge;
                saved.Read = DateTime.UtcNow;
                saved.DropsRead = DateTime.UtcNow;
                saved.DropsOwned = badge.OwnedTotal;
                saved.ClientStamp = ReadClient(accountId, appId)?.Stamp;
                Save(accountId, appId, saved);
                var result = Build(accountId, appId, saved);
                known[$"{accountId}-{appId}"] = result;
                return result;
            }
            finally
            {
                askingAccount.Release();
            }
        }

        /// <summary>The cards left to get are asked a few times a day, and when the number of cards isn't the one of then.</summary>
        private static bool IsAccountDue(Saved saved)
        {
            if (saved == null || saved.AccountAgain)
            {
                return DateTime.UtcNow >= nextQuestion;
            }

            var changed = saved.Badge != null && saved.Badge.OwnedTotal != saved.DropsOwned;
            return saved.State != SteamCardsState.None && DateTime.UtcNow >= nextQuestion && DateTime.UtcNow - saved.AccountTried > accountRetry &&
                (changed || DateTime.UtcNow - saved.DropsRead > dropsLifetime);
        }

        private static readonly Regex ownIdRegex = new Regex(@"g_steamID\s*=\s*""(?<id>\d{17})""", RegexOptions.Compiled);

        /// <summary>The session of the browser may be of another account than the one of the client: its cards aren't these.</summary>
        public static bool IsSameAccount(string address, string source, string accountId)
        {
            var id = ownIdRegex.Match(source ?? string.Empty);
            return id.Success && id.Groups["id"].Value == GetSteamId(accountId);
        }
    }
}
