using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Playnite.Atrio
{
    public class SteamCollection
    {
        public string Id { get; set; }
        public string Name { get; set; }

        /// <summary>The games in it, by their id on Steam.</summary>
        public List<string> AppIds { get; set; } = new List<string>();

        /// <summary>Steam fills these with its own filters, so they can't come over as a list of games.</summary>
        public bool IsDynamic { get; set; }
        public bool IsFavorites => Id == "favorite";
    }

    public class SteamCollectionAccount
    {
        public string AccountId { get; set; }

        /// <summary>The public name of the account on this PC, or its number when Steam doesn't say.</summary>
        public string Name { get; set; }
        public List<SteamCollection> Collections { get; set; } = new List<SteamCollection>();
    }

    /// <summary>
    /// The collections of the library of Steam, as its client keeps them on this PC for each account that used it
    /// (userdata\&lt;account&gt;\config\cloudstorage\cloud-storage-namespace-1.json). Only reads: nothing of Steam changes.
    /// </summary>
    public static class SteamCollections
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string KeyPrefix = "user-collections.";
        private const ulong SteamId64Base = 76561197960265728;

        public static string FindSteamPath()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var path = (key?.GetValue("SteamPath") as string)?.Replace('/', '\\');
                    return !path.IsNullOrEmpty() && Directory.Exists(path) ? path : null;
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, "Failed to find Steam.");
                return null;
            }
        }

        /// <summary>The accounts of this PC with collections of their own, the one with more of them first.</summary>
        public static List<SteamCollectionAccount> Read(string steamPath)
        {
            var accounts = new List<SteamCollectionAccount>();
            var userdata = steamPath.IsNullOrEmpty() ? null : Path.Combine(steamPath, "userdata");
            if (userdata == null || !Directory.Exists(userdata))
            {
                return accounts;
            }

            var names = new Dictionary<string, string>();
            try
            {
                var logins = Path.Combine(steamPath, "config", "loginusers.vdf");
                if (File.Exists(logins))
                {
                    names = ParsePersonaNames(File.ReadAllText(logins));
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, "Failed to read the names of the Steam accounts.");
            }

            foreach (var folder in Directory.GetDirectories(userdata))
            {
                var accountId = Path.GetFileName(folder);
                var file = Path.Combine(folder, "config", "cloudstorage", "cloud-storage-namespace-1.json");
                if (!accountId.All(char.IsDigit) || !File.Exists(file))
                {
                    continue;
                }

                try
                {
                    var collections = Parse(File.ReadAllText(file));
                    if (collections.Any(a => a.IsDynamic || a.AppIds.Count > 0))
                    {
                        accounts.Add(new SteamCollectionAccount
                        {
                            AccountId = accountId,
                            Name = names.TryGetValue(accountId, out var name) ? name : accountId,
                            Collections = collections
                        });
                    }
                }
                catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Error(e, $"Failed to read the Steam collections of {accountId}.");
                }
            }

            return accounts.OrderByDescending(a => a.Collections.Count(b => !b.IsFavorites)).ToList();
        }

        /// <summary>
        /// The collections in the file: the ones made by the player and the favorites. Deleted ones, the hidden
        /// games and the ones Steam makes by itself (like those of a subscription) are left out.
        /// </summary>
        public static List<SteamCollection> Parse(string json)
        {
            var result = new List<SteamCollection>();
            foreach (var pair in JArray.Parse(json).OfType<JArray>())
            {
                var key = pair.Count > 0 ? pair[0]?.ToString() : null;
                if (key?.StartsWith(KeyPrefix, StringComparison.Ordinal) != true || !(pair.Count > 1 && pair[1] is JObject entry))
                {
                    continue;
                }

                var text = entry["value"]?.ToString();
                if (entry["is_deleted"]?.Value<bool>() == true || text.IsNullOrEmpty())
                {
                    continue;
                }

                var value = JObject.Parse(text);
                var id = value["id"]?.ToString() ?? key.Substring(KeyPrefix.Length);
                if (!id.StartsWith("uc-", StringComparison.Ordinal) && id != "favorite")
                {
                    continue;
                }

                var removed = new HashSet<string>((value["removed"] as JArray)?.Select(a => a.ToString()) ?? Enumerable.Empty<string>());
                result.Add(new SteamCollection
                {
                    Id = id,
                    Name = value["name"]?.ToString(),
                    IsDynamic = value["filterSpec"] != null,
                    AppIds = ((value["added"] as JArray)?.Select(a => a.ToString()) ?? Enumerable.Empty<string>()).
                        Where(a => !removed.Contains(a)).
                        Distinct().
                        ToList()
                });
            }

            return result.OrderBy(a => a.IsFavorites ? 0 : 1).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>
        /// The public names in loginusers.vdf, by the number of the account (the one of the folders in userdata).
        /// Only the name the account shows to others is taken.
        /// </summary>
        public static Dictionary<string, string> ParsePersonaNames(string vdf)
        {
            var names = new Dictionary<string, string>();
            foreach (Match match in Regex.Matches(vdf ?? string.Empty, "\"(\\d{17})\"\\s*\\{([^{}]*)\\}"))
            {
                var persona = Regex.Match(match.Groups[2].Value, "\"PersonaName\"\\s*\"([^\"]*)\"");
                if (persona.Success && ulong.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var steamId) && steamId > SteamId64Base)
                {
                    names[(steamId - SteamId64Base).ToString(CultureInfo.InvariantCulture)] = persona.Groups[1].Value;
                }
            }

            return names;
        }
    }
}
