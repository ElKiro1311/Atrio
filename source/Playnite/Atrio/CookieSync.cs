using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Playnite.Atrio
{
    /// <summary>A cookie, the same in both engines of the browser.</summary>
    public class BridgeCookie
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public string Domain { get; set; }
        public string Path { get; set; }
        public DateTime? Expires { get; set; }
        public bool HttpOnly { get; set; }
        public bool Secure { get; set; }

        /// <summary>0 not said, 1 none, 2 lax, 3 strict.</summary>
        public int SameSite { get; set; }

        /// <summary>
        /// A cookie of only one host ("steamcommunity.com") and one of its domain (".steamcommunity.com") are two
        /// different cookies.
        /// </summary>
        public string Key => (Domain ?? string.Empty).ToLowerInvariant() + "|" + (Path.IsNullOrEmpty() ? "/" : Path) + "|" + Name;
    }

    /// <summary>
    /// What changed on one side of the sessions since it was last seen. A side is remembered by the fingerprint of
    /// each cookie, never by its value, so what's kept on disk can't sign in anywhere.
    /// </summary>
    public static class CookieSync
    {
        /// <summary>Cookies to write on the other side, and the keys of the cookies to delete there.</summary>
        public static Tuple<List<BridgeCookie>, List<string>> Changes(IEnumerable<BridgeCookie> source, IDictionary<string, string> before)
        {
            var now = Index(source);
            var write = now.Values.Where(a => !before.TryGetValue(a.Key, out var old) || old != Fingerprint(a)).ToList();
            var delete = before.Keys.Where(a => !now.ContainsKey(a)).ToList();
            return Tuple.Create(write, delete);
        }

        public static Dictionary<string, BridgeCookie> Index(IEnumerable<BridgeCookie> cookies)
        {
            return (cookies ?? Enumerable.Empty<BridgeCookie>()).
                Where(a => a != null && !a.Name.IsNullOrEmpty() && !a.Domain.IsNullOrEmpty()).
                GroupBy(a => a.Key).
                ToDictionary(a => a.Key, a => a.First());
        }

        /// <summary>The picture of a side: each cookie by its fingerprint.</summary>
        public static Dictionary<string, string> Picture(IEnumerable<BridgeCookie> cookies)
        {
            return Index(cookies).ToDictionary(a => a.Key, a => Fingerprint(a.Value));
        }

        /// <summary>The start of the fingerprint of a cookie of the session, which a browser drops when it closes.</summary>
        public const string SessionMark = "S:";

        public static string Fingerprint(BridgeCookie cookie)
        {
            using (var sha = SHA256.Create())
            {
                var text = (cookie.Value ?? string.Empty) + "\n" + (cookie.Expires?.ToUniversalTime().Ticks / TimeSpan.TicksPerSecond ?? 0);
                return (cookie.Expires == null ? SessionMark : string.Empty) + Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text)), 0, 12);
            }
        }

        /// <summary>
        /// The cookies of the session that a browser lost because it closed aren't a sign out: they don't go away from
        /// the other side.
        /// </summary>
        public static List<string> WithoutClosedSession(IEnumerable<string> gone, IDictionary<string, string> before)
        {
            return gone.Where(a => !(before.TryGetValue(a, out var fingerprint) && fingerprint.StartsWith(SessionMark))).ToList();
        }

        /// <summary>
        /// The cookies gone from one side that can go away from the other: not the ones the other side changed since
        /// it was last seen, which would be a session renewed there.
        /// </summary>
        public static List<string> SafeDeletes(IEnumerable<string> gone, IEnumerable<BridgeCookie> otherNow, IDictionary<string, string> otherBefore)
        {
            var now = Index(otherNow);
            return gone.Where(a => !now.TryGetValue(a, out var there) ||
                                   (otherBefore.TryGetValue(a, out var fingerprint) && fingerprint == Fingerprint(there))).ToList();
        }

        /// <summary>
        /// The first time both sides meet, with nothing remembered: each one gets the cookies it lacks, and when both
        /// have one with different values, the one that lasts longer wins. Cookies to write on Edge and on the other.
        /// </summary>
        public static Tuple<List<BridgeCookie>, List<BridgeCookie>> FirstMerge(IEnumerable<BridgeCookie> edge, IEnumerable<BridgeCookie> other)
        {
            var inEdge = Index(edge);
            var inOther = Index(other);
            var toEdge = new List<BridgeCookie>();
            var toOther = new List<BridgeCookie>();
            foreach (var cookie in inOther.Values)
            {
                if (!inEdge.TryGetValue(cookie.Key, out var there))
                {
                    toEdge.Add(cookie);
                }
                else if (Fingerprint(there) != Fingerprint(cookie))
                {
                    var otherLasts = cookie.Expires ?? DateTime.MinValue;
                    var edgeLasts = there.Expires ?? DateTime.MinValue;
                    if (otherLasts.ToUniversalTime() > edgeLasts.ToUniversalTime())
                    {
                        toEdge.Add(cookie);
                    }
                    else if (edgeLasts.ToUniversalTime() > otherLasts.ToUniversalTime())
                    {
                        toOther.Add(there);
                    }
                }
            }

            toOther.AddRange(inEdge.Values.Where(a => !inOther.ContainsKey(a.Key)));
            return Tuple.Create(toEdge, toOther);
        }

        /// <summary>The cookie a key names, without its value: what's needed to delete it.</summary>
        public static BridgeCookie FromKey(string key)
        {
            var parts = key.Split(new[] { '|' }, 3);
            return parts.Length < 3 ? null : new BridgeCookie { Domain = parts[0], Path = parts[1], Name = parts[2], Secure = true };
        }

        /// <summary>The address a cookie belongs to, for the engine that asks for one.</summary>
        public static string GetUrl(BridgeCookie cookie)
        {
            return (cookie.Secure ? "https://" : "http://") + cookie.Domain.TrimStart('.') + (cookie.Path.IsNullOrEmpty() ? "/" : cookie.Path);
        }

        /// <summary>
        /// The picture of the other side after passing changes to it: only the cookies written and deleted change, so
        /// what changed there meanwhile is still seen as a change. A cookie the engine refused isn't in it.
        /// </summary>
        public static void Update(IDictionary<string, string> picture, IEnumerable<BridgeCookie> written, IEnumerable<string> deleted, IEnumerable<BridgeCookie> nowThere)
        {
            var there = Index(nowThere);
            foreach (var cookie in written)
            {
                if (there.TryGetValue(cookie.Key, out var actual))
                {
                    picture[cookie.Key] = Fingerprint(actual);
                }
                else
                {
                    picture.Remove(cookie.Key);
                }
            }

            foreach (var key in deleted)
            {
                if (there.TryGetValue(key, out var stayed))
                {
                    picture[key] = Fingerprint(stayed);
                }
                else
                {
                    picture.Remove(key);
                }
            }
        }
    }
}
