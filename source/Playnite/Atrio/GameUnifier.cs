using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Playnite.Atrio
{
    /// <summary>
    /// One game that the user owns in more than one store.
    /// </summary>
    public class UnifiedGame
    {
        public string Key { get; internal set; }

        /// <summary>The copy that represents the game in the library and that "Play" starts.</summary>
        public Game Primary { get; internal set; }

        /// <summary>All copies. The primary one comes first.</summary>
        public List<Game> Copies { get; internal set; }

        /// <summary>True when the user explicitly picked the primary copy.</summary>
        public bool HasUserChoice { get; internal set; }

        public ulong TotalPlaytime
        {
            get
            {
                ulong total = 0;
                foreach (var copy in Copies)
                {
                    total += copy.Playtime;
                }

                return total;
            }
        }
    }

    /// <summary>
    /// Works out which library entries are the same game sold by different stores.
    /// Nothing is written to the game database: copies stay as regular games, so add-ons
    /// keep seeing the library exactly as they would in Playnite.
    /// </summary>
    public class GameUnifier
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private const string ManualKeyPrefix = "manual:";
        private const string PcBucket = "pc";

        private static readonly string[] editionSuffixes = new string[]
        {
            "game of the year edition", "game of the year", "goty edition", "goty",
            "digital deluxe edition", "digital deluxe", "deluxe edition",
            "standard edition", "gold edition", "premium edition", "ultimate edition",
            "the complete edition", "complete edition", "collectors edition",
            "windows 10 edition", "windows edition", "for windows 10", "for windows",
            "microsoft store edition", "pc edition", "pc version", "for pc",
            "steam edition", "gog edition", "epic edition"
        };

        private static readonly Dictionary<string, string> romanNumerals = new Dictionary<string, string>
        {
            { "ii", "2" }, { "iii", "3" }, { "iv", "4" }, { "v", "5" },
            { "vi", "6" }, { "vii", "7" }, { "viii", "8" }, { "ix", "9" }
        };

        private static readonly Regex nonAlphanumeric = new Regex(@"[^\p{L}\p{Nd}]+", RegexOptions.Compiled);

        private readonly object timerLock = new object();
        private readonly Dictionary<string, string> normalizedNames = new Dictionary<string, string>();
        private Dictionary<Guid, UnifiedGame> groups = new Dictionary<Guid, UnifiedGame>();
        private Func<IEnumerable<Game>> gamesProvider;
        private SynchronizationContext syncContext;
        private System.Threading.Timer rebuildTimer;

        public static GameUnifier Current { get; set; }

        public AtrioSettings Settings { get; }
        public bool IsBuilt { get; private set; }
        public Func<Game, LibraryPlugin> PluginResolver { get; set; }

        /// <summary>Raised after the groups changed, on the UI thread.</summary>
        public event EventHandler GroupsChanged;

        public GameUnifier(AtrioSettings settings)
        {
            Settings = settings;
        }

        public void Attach(Func<IEnumerable<Game>> provider, SynchronizationContext context)
        {
            gamesProvider = provider;
            syncContext = context;
        }

        /// <summary>
        /// Reduces a title to what identifies the game: casing, accents, punctuation, trademark
        /// signs, roman numerals and the edition names that don't make it a different game.
        /// Remasters and "Definitive" or "Enhanced" editions are left alone on purpose.
        /// </summary>
        public static string NormalizeName(string name)
        {
            if (name.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            var cleaned = name.Replace("™", " ").Replace("®", " ").Replace("©", " ").Replace("℠", " ").Replace("’", "").Replace("'", "");
            cleaned = cleaned.Replace("&", " and ");
            cleaned = RemoveDiacritics(cleaned).ToLowerInvariant();
            cleaned = nonAlphanumeric.Replace(cleaned, " ").Trim();

            var stripped = true;
            while (stripped)
            {
                stripped = false;
                foreach (var suffix in editionSuffixes)
                {
                    if (cleaned.Length > suffix.Length && cleaned.EndsWith(" " + suffix, StringComparison.Ordinal))
                    {
                        cleaned = cleaned.Substring(0, cleaned.Length - suffix.Length - 1).TrimEnd();
                        stripped = true;
                        break;
                    }
                }
            }

            var words = cleaned.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new StringBuilder(cleaned.Length);
            for (int i = 0; i < words.Length; i++)
            {
                if (i == 0 && words[i] == "the" && words.Length > 1)
                {
                    continue;
                }

                result.Append(romanNumerals.TryGetValue(words[i], out var digit) ? digit : words[i]);
            }

            return result.ToString();
        }

        private static string RemoveDiacritics(string text)
        {
            var decomposed = text.Normalize(NormalizationForm.FormD);
            var result = new StringBuilder(decomposed.Length);
            foreach (var character in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                {
                    result.Append(character);
                }
            }

            return result.ToString().Normalize(NormalizationForm.FormC);
        }

        private string GetNormalizedName(string name)
        {
            if (name == null)
            {
                return string.Empty;
            }

            if (!normalizedNames.TryGetValue(name, out var normalized))
            {
                normalized = NormalizeName(name);
                normalizedNames[name] = normalized;
            }

            return normalized;
        }

        /// <summary>
        /// Games only match inside the same platform family, so a PC game and a
        /// console or ROM version of it stay as different entries.
        /// </summary>
        public static string GetPlatformBucket(Game game)
        {
            var platforms = game.Platforms;
            if (!platforms.HasItems())
            {
                return PcBucket;
            }

            foreach (var platform in platforms)
            {
                if (platform.SpecificationId?.StartsWith("pc_", StringComparison.Ordinal) == true ||
                    platform.Name?.IndexOf("PC", StringComparison.Ordinal) >= 0 ||
                    platform.Name?.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return PcBucket;
                }
            }

            return string.Join("+", platforms.Select(a => a.SpecificationId ?? a.Name).OrderBy(a => a));
        }

        public static bool IsManualKey(string key)
        {
            return key?.StartsWith(ManualKeyPrefix, StringComparison.Ordinal) == true;
        }

        public string GetGroupKey(Game game)
        {
            if (Settings.ManualGroups.TryGetValue(game.Id, out var manualGroup))
            {
                return ManualKeyPrefix + manualGroup;
            }

            var name = GetNormalizedName(game.Name);
            return name.Length == 0 ? null : name + "|" + GetPlatformBucket(game);
        }

        /// <summary>Position of a store in the user's order of preference, lower is more relevant.</summary>
        public int GetStoreRank(Guid pluginId)
        {
            return GetStoreRank(pluginId, PluginResolver?.Invoke(new Game { PluginId = pluginId })?.Name);
        }

        /// <summary>Stores the user hasn't ordered yet follow them, from the best known to the least.</summary>
        public int GetStoreRank(Guid pluginId, string storeName)
        {
            var index = Settings.StorePriority.IndexOf(pluginId);
            return index >= 0 ? index : 1000 + StoreBranding.GetDefaultRank(storeName);
        }

        /// <summary>
        /// A store never sells two copies of one product, so same-named entries from the
        /// same store are different games ("Prey" 2006 and 2017). Only one of them joins.
        /// </summary>
        private static List<Game> GetOneCopyPerStore(List<Game> candidates)
        {
            var referenceYear = candidates.Where(a => a.ReleaseYear != null).
                GroupBy(a => a.PluginId).
                Where(a => a.Count() == 1).
                Select(a => a.First().ReleaseYear).
                FirstOrDefault();

            var result = new List<Game>();
            foreach (var store in candidates.GroupBy(a => a.PluginId))
            {
                if (referenceYear != null)
                {
                    result.Add(store.OrderBy(a => Math.Abs((a.ReleaseYear ?? 0) - referenceYear.Value)).First());
                }
                else
                {
                    result.Add(store.OrderBy(a => a.Name, StringComparer.Ordinal).First());
                }
            }

            return result;
        }

        private Game PickPrimary(List<Game> copies, string key, out bool userChoice)
        {
            userChoice = false;
            if (Settings.PreferredCopies.TryGetValue(key, out var preferredId))
            {
                var preferred = copies.FirstOrDefault(a => a.Id == preferredId);
                if (preferred != null)
                {
                    userChoice = true;
                    return preferred;
                }
            }

            var installed = copies.Where(a => a.IsInstalled).ToList();
            if (installed.Count > 0)
            {
                return installed.
                    OrderByDescending(a => a.LastActivity ?? DateTime.MinValue).
                    ThenByDescending(a => a.Playtime).
                    ThenBy(a => GetStoreRank(a.PluginId)).
                    First();
            }

            return copies.
                OrderBy(a => GetStoreRank(a.PluginId)).
                ThenByDescending(a => a.Playtime).
                First();
        }

        /// <summary>Builds groups from a list of games. Has no side effects, used directly by tests.</summary>
        public Dictionary<Guid, UnifiedGame> BuildGroups(IEnumerable<Game> games)
        {
            var result = new Dictionary<Guid, UnifiedGame>();
            if (!Settings.UnifyDuplicates)
            {
                return result;
            }

            var split = new HashSet<Guid>(Settings.SplitGames);
            var buckets = new Dictionary<string, List<Game>>();
            foreach (var game in games)
            {
                if (game.Hidden || split.Contains(game.Id))
                {
                    continue;
                }

                var key = GetGroupKey(game);
                if (key == null)
                {
                    continue;
                }

                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = new List<Game>();
                    buckets.Add(key, bucket);
                }

                bucket.Add(game);
            }

            foreach (var bucket in buckets)
            {
                if (bucket.Value.Count < 2)
                {
                    continue;
                }

                var copies = IsManualKey(bucket.Key) ? bucket.Value : GetOneCopyPerStore(bucket.Value);
                if (copies.Count < 2)
                {
                    continue;
                }

                var primary = PickPrimary(copies, bucket.Key, out var userChoice);
                var group = new UnifiedGame
                {
                    Key = bucket.Key,
                    Primary = primary,
                    HasUserChoice = userChoice,
                    Copies = copies.
                        OrderByDescending(a => a.Id == primary.Id).
                        ThenByDescending(a => a.IsInstalled).
                        ThenBy(a => GetStoreRank(a.PluginId)).
                        ThenBy(a => a.Name, StringComparer.Ordinal).
                        ToList()
                };

                foreach (var copy in copies)
                {
                    result[copy.Id] = group;
                }
            }

            return result;
        }

        private static bool AreSame(Dictionary<Guid, UnifiedGame> first, Dictionary<Guid, UnifiedGame> second)
        {
            if (first.Count != second.Count)
            {
                return false;
            }

            foreach (var item in first)
            {
                if (!second.TryGetValue(item.Key, out var other) ||
                    other.Primary.Id != item.Value.Primary.Id ||
                    other.Copies.Count != item.Value.Copies.Count)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Builds the groups the first time, without telling anybody: it runs from inside a filter pass.</summary>
        public void EnsureBuilt()
        {
            if (!IsBuilt)
            {
                Rebuild(false);
            }
        }

        public void Rebuild()
        {
            Rebuild(true);
        }

        /// <summary>Same as Rebuild, for changes that affect how groups look without changing their members.</summary>
        public void RebuildAndNotify()
        {
            Rebuild(false);
            GroupsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Rebuild(bool notify)
        {
            if (gamesProvider == null)
            {
                return;
            }

            List<Game> games;
            try
            {
                games = gamesProvider().ToList();
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to read games for unification.");
                return;
            }

            var newGroups = BuildGroups(games);
            var changed = !AreSame(groups, newGroups);
            groups = newGroups;
            IsBuilt = true;
            if (changed && notify)
            {
                GroupsChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Asks for a rebuild once the current burst of database changes is over.</summary>
        public void ScheduleRebuild()
        {
            lock (timerLock)
            {
                if (rebuildTimer == null)
                {
                    rebuildTimer = new System.Threading.Timer(_ =>
                    {
                        if (syncContext != null)
                        {
                            syncContext.Post(__ => Rebuild(), null);
                        }
                        else
                        {
                            Rebuild();
                        }
                    }, null, 400, Timeout.Infinite);
                }
                else
                {
                    rebuildTimer.Change(400, Timeout.Infinite);
                }
            }
        }

        public UnifiedGame GetGroup(Guid gameId)
        {
            return groups.TryGetValue(gameId, out var group) ? group : null;
        }

        public bool IsSecondaryCopy(Guid gameId)
        {
            return groups.TryGetValue(gameId, out var group) && group.Primary.Id != gameId;
        }

        /// <summary>How many library entries are folded into another one.</summary>
        public int CollapsedCopiesCount => groups.Count - groups.Values.Distinct().Count();

        public IEnumerable<UnifiedGame> GetAllGroups()
        {
            return groups.Values.Distinct();
        }

        public void SetPreferredCopy(Game copy)
        {
            var group = GetGroup(copy.Id);
            if (group == null)
            {
                return;
            }

            Settings.PreferredCopies[group.Key] = copy.Id;
            Settings.Save();
            Rebuild();
        }

        public void ClearPreferredCopy(UnifiedGame group)
        {
            if (Settings.PreferredCopies.Remove(group.Key))
            {
                Settings.Save();
                Rebuild();
            }
        }

        /// <summary>Takes a copy out of its group, it goes back to being a separate entry.</summary>
        public void SplitCopy(Game copy)
        {
            if (Settings.ManualGroups.Remove(copy.Id))
            {
                // A manual group left with a single member has no reason to stay marked.
                var orphans = Settings.ManualGroups.GroupBy(a => a.Value).Where(a => a.Count() < 2).SelectMany(a => a).ToList();
                orphans.ForEach(a => Settings.ManualGroups.Remove(a.Key));
            }

            if (!Settings.SplitGames.Contains(copy.Id))
            {
                Settings.SplitGames.Add(copy.Id);
            }

            Settings.Save();
            Rebuild();
        }

        /// <summary>Joins games the automatic matching missed because their titles differ.</summary>
        public void MergeGames(IEnumerable<Game> games)
        {
            var list = games.ToList();
            if (list.Count < 2)
            {
                return;
            }

            var manualId = list.Select(a => Settings.ManualGroups.TryGetValue(a.Id, out var id) ? id : null).FirstOrDefault(a => a != null);
            manualId = manualId ?? Guid.NewGuid().ToString("N");
            foreach (var group in list.Select(a => GetGroup(a.Id)).Where(a => a != null).Distinct().ToList())
            {
                foreach (var copy in group.Copies)
                {
                    Settings.ManualGroups[copy.Id] = manualId;
                    Settings.SplitGames.Remove(copy.Id);
                }
            }

            foreach (var game in list)
            {
                Settings.ManualGroups[game.Id] = manualId;
                Settings.SplitGames.Remove(game.Id);
            }

            Settings.Save();
            Rebuild();
        }
    }
}
