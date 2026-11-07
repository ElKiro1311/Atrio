using Playnite.SDK;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    public enum SteamFriendsState
    {
        Ready,

        /// <summary>The browser of Atrio has no Steam session.</summary>
        SignedOut,

        /// <summary>There is a session, but Steam doesn't let it see this, which is what Family View does.</summary>
        Locked,

        Failed
    }

    public class SteamFriend
    {
        public string SteamId { get; set; }
        public string Name { get; set; }
        public string AvatarUrl { get; set; }
        public string ProfileUrl => $"https://steamcommunity.com/profiles/{SteamId}";
        public int RecentMinutes { get; set; }
        public int TotalMinutes { get; set; }
        public string DetailText { get; set; }
    }

    public class SteamFriendsResult
    {
        public SteamFriendsState State { get; set; }
        public List<SteamFriend> InGame { get; set; } = new List<SteamFriend>();
        public List<SteamFriend> PlayedRecently { get; set; } = new List<SteamFriend>();
        public List<SteamFriend> PlayedBefore { get; set; } = new List<SteamFriend>();
        public List<SteamFriend> Owners { get; set; } = new List<SteamFriend>();
    }

    public class SteamSessionPage
    {
        public string Address { get; set; }
        public string Source { get; set; }
    }

    public class SteamSession
    {
        public SteamFriendsState State { get; set; }
        public string AccessToken { get; set; }
        public DateTime Created { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Which friends play a game, the same question the Steam client asks for its library. It goes with the
    /// session of the browser of Atrio: the access to the account stays in memory and never reaches a file or
    /// the log, and without a session the page of the game simply offers to open one.
    /// </summary>
    public static class SteamFriends
    {
        private class GameplayResponse
        {
            public GameplayInfo response { get; set; }
        }

        private class GameplayInfo
        {
            public List<GameplayEntry> in_game { get; set; }
            public List<GameplayEntry> played_recently { get; set; }
            public List<GameplayEntry> played_ever { get; set; }
            public List<GameplayEntry> owns { get; set; }
        }

        private class GameplayEntry
        {
            public string steamid { get; set; }
            public int minutes_played { get; set; }
            public int minutes_played_forever { get; set; }
        }

        private class ProfileResponse
        {
            public string persona_name { get; set; }
            public string avatar_url { get; set; }
        }

        /// <summary>Friends shown with their name in each group of the page of a game.</summary>
        public const int NamedLimit = 6;

        /// <summary>Friends shown only with their picture in each of the other groups.</summary>
        public const int PictureLimit = 16;

        private const ulong FirstSteamId = 76561197960265728;
        private const string StoreConfigUrl = "https://store.steampowered.com/pointssummary/ajaxgetasyncconfig";
        private const string CommunityAccountUrl = "https://steamcommunity.com/my/edit/info";
        private const string GameplayUrl = "https://api.steampowered.com/IPlayerService/GetFriendsGameplayInfo/v1/?access_token={0}&appid={1}";
        private const string ProfileJsonUrl = "https://steamcommunity.com/miniprofile/{0}/json";

        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly Regex accessTokenRegex = new Regex(
            @"(?:webapi_token|strWebAPIToken)(?:\\?""|&quot;|')?\s*[:=]\s*(?:\\?""|&quot;|')+(?<token>[A-Za-z0-9_\-.]{30,})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly TimeSpan openSessionLifetime = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan closedSessionLifetime = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan resultLifetime = TimeSpan.FromMinutes(10);
        private static readonly SemaphoreSlim sessionLock = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim profileSlots = new SemaphoreSlim(4, 4);
        private static readonly ConcurrentDictionary<string, Tuple<DateTime, SteamFriendsResult>> results =
            new ConcurrentDictionary<string, Tuple<DateTime, SteamFriendsResult>>();

        private static readonly ConcurrentDictionary<string, ProfileResponse> profiles =
            new ConcurrentDictionary<string, ProfileResponse>();

        private static volatile SteamSession session;

        /// <summary>After a sign in, or a PIN entered in the browser, the next question has to see it.</summary>
        public static void ForgetSession()
        {
            session = null;
            results.Clear();
        }

        /// <summary>The last answer of Steam about a game, while it's still recent.</summary>
        public static SteamFriendsResult GetKnown(string appId)
        {
            return results.TryGetValue(appId, out var cached) && DateTime.Now - cached.Item1 < resultLifetime ? cached.Item2 : null;
        }

        public static async Task<SteamFriendsResult> GetAsync(string appId, CancellationToken token)
        {
            var known = GetKnown(appId);
            if (known != null)
            {
                return known;
            }

            var current = await GetSessionAsync(token).ConfigureAwait(false);
            if (current.State != SteamFriendsState.Ready)
            {
                return new SteamFriendsResult { State = current.State };
            }

            using (var response = await client.GetAsync(string.Format(GameplayUrl, Uri.EscapeDataString(current.AccessToken), appId), token).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    logger.Info($"Steam answered {(int)response.StatusCode} to the question about friends.");
                    if (response.StatusCode != HttpStatusCode.Unauthorized && response.StatusCode != HttpStatusCode.Forbidden)
                    {
                        return new SteamFriendsResult { State = SteamFriendsState.Failed };
                    }

                    session = new SteamSession { State = SteamFriendsState.Locked };
                    return new SteamFriendsResult { State = SteamFriendsState.Locked };
                }

                var result = ParseGameplayInfo(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                await FillProfilesAsync(result, token).ConfigureAwait(false);
                results[appId] = Tuple.Create(DateTime.Now, result);
                return result;
            }
        }

        /// <summary>The access of the session of the browser, null without one. It stays in memory.</summary>
        internal static async Task<string> GetAccessTokenAsync(CancellationToken token)
        {
            var current = await GetSessionAsync(token).ConfigureAwait(false);
            return current.State == SteamFriendsState.Ready ? current.AccessToken : null;
        }

        private static async Task<SteamSession> GetSessionAsync(CancellationToken token)
        {
            await sessionLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var current = session;
                var lifetime = current?.State == SteamFriendsState.Ready ? openSessionLifetime : closedSessionLifetime;
                if (current == null || DateTime.Now - current.Created > lifetime)
                {
                    current = await Task.Run(() => ReadSession(LoadPage), token).ConfigureAwait(false);
                    session = current;
                    logger.Info($"Steam session of the browser: {current.State}.");
                }

                return current;
            }
            finally
            {
                sessionLock.Release();
            }
        }

        private static SteamSessionPage LoadPage(string url)
        {
            using (var view = PlayniteApplication.Current.PlayniteApiGlobal.WebViews.CreateOffscreenView())
            {
                view.NavigateAndWait(url);
                return new SteamSessionPage { Address = view.GetCurrentAddress(), Source = view.GetPageSource() };
            }
        }

        /// <summary>What the session of the browser allows, from the pages that say it.</summary>
        public static SteamSession ReadSession(Func<string, SteamSessionPage> loadPage)
        {
            try
            {
                var accessToken = ParseAccessToken(loadPage(StoreConfigUrl)?.Source);
                if (!accessToken.IsNullOrEmpty())
                {
                    return new SteamSession { State = SteamFriendsState.Ready, AccessToken = accessToken };
                }

                var page = loadPage(CommunityAccountUrl);
                accessToken = ParseAccessToken(page?.Source);
                if (!accessToken.IsNullOrEmpty())
                {
                    return new SteamSession { State = SteamFriendsState.Ready, AccessToken = accessToken };
                }

                var signedOut = page?.Address?.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0;
                return new SteamSession { State = signedOut ? SteamFriendsState.SignedOut : SteamFriendsState.Locked };
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Debug(e, "Failed to read the Steam session of the browser.");
                return new SteamSession { State = SteamFriendsState.Failed };
            }
        }

        public static string ParseAccessToken(string source)
        {
            var match = accessTokenRegex.Match(source ?? string.Empty);
            return match.Success ? match.Groups["token"].Value : null;
        }

        public static SteamFriendsResult ParseGameplayInfo(string json)
        {
            var info = Playnite.Common.Serialization.FromJson<GameplayResponse>(json)?.response;
            var seen = new HashSet<string>();
            List<SteamFriend> read(List<GameplayEntry> entries)
            {
                return (entries ?? new List<GameplayEntry>()).
                    Where(a => !a.steamid.IsNullOrEmpty() && seen.Add(a.steamid)).
                    OrderByDescending(a => a.minutes_played).
                    ThenByDescending(a => a.minutes_played_forever).
                    Select(a => new SteamFriend { SteamId = a.steamid, RecentMinutes = a.minutes_played, TotalMinutes = a.minutes_played_forever }).
                    ToList();
            }

            return new SteamFriendsResult
            {
                State = SteamFriendsState.Ready,
                InGame = read(info?.in_game),
                PlayedRecently = read(info?.played_recently),
                PlayedBefore = read(info?.played_ever),
                Owners = read(info?.owns)
            };
        }

        /// <summary>The number the community pages use for an account. Null for something that isn't a Steam id.</summary>
        public static string GetAccountId(string steamId)
        {
            return ulong.TryParse(steamId, out var id) && id > FirstSteamId ? (id - FirstSteamId).ToString() : null;
        }

        /// <summary>Names and pictures are public: only the friends that will be on the page are asked for.</summary>
        private static Task FillProfilesAsync(SteamFriendsResult result, CancellationToken token)
        {
            var shown = result.InGame.Take(NamedLimit).
                Concat(result.PlayedRecently.Take(NamedLimit)).
                Concat(result.PlayedBefore.Take(PictureLimit)).
                Concat(result.Owners.Take(PictureLimit));
            return Task.WhenAll(shown.Select(a => FillProfileAsync(a, token)));
        }

        private static async Task FillProfileAsync(SteamFriend friend, CancellationToken token)
        {
            if (!profiles.TryGetValue(friend.SteamId, out var profile))
            {
                await profileSlots.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var json = await client.GetStringAsync(string.Format(ProfileJsonUrl, GetAccountId(friend.SteamId))).ConfigureAwait(false);
                    profile = Playnite.Common.Serialization.FromJson<ProfileResponse>(json);
                    profiles[friend.SteamId] = profile;
                }
                catch (Exception e) when (!token.IsCancellationRequested && !(e is OutOfMemoryException))
                {
                    // One friend without a picture is no reason to lose the others.
                    logger.Debug(e, "Failed to read a public Steam profile.");
                    return;
                }
                finally
                {
                    profileSlots.Release();
                }
            }

            friend.Name = profile?.persona_name;
            friend.AvatarUrl = profile?.avatar_url;
        }
    }
}
