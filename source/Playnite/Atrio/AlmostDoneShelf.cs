using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Playnite.Atrio
{
    /// <summary>
    /// The shelf of Home with the games whose achievements are almost all unlocked, to finish them off.
    /// </summary>
    public static class AlmostDoneShelf
    {
        /// <summary>From this share of achievements on, a game is almost done.</summary>
        public const double MinPercent = 75;

        /// <summary>A game with fewer achievements than this is too quick to count.</summary>
        public const int MinAchievements = 4;

        public static double GetPercent(int unlocked, int total)
        {
            return total > 0 ? unlocked * 100.0 / total : 0;
        }

        /// <summary>Not finished yet, but with most of its achievements unlocked.</summary>
        public static bool Qualifies(int unlocked, int total)
        {
            return total >= MinAchievements && unlocked < total && GetPercent(unlocked, total) >= MinPercent;
        }

        public class Pick
        {
            public GamesCollectionViewEntry Entry { get; set; }
            public int Unlocked { get; set; }
            public int Total { get; set; }
            public string Text => GetText(Unlocked, Total);
        }

        /// <summary>
        /// The games with most of their achievements unlocked, the closest to 100 % first. Each copy of a game
        /// counts for the game the list shows, and the one with more progress wins.
        /// </summary>
        public static List<Pick> Choose(IEnumerable<GamesCollectionViewEntry> entries, IEnumerable<AchievementProgress.Entry> progress, int max)
        {
            var byCopy = new Dictionary<Guid, GamesCollectionViewEntry>();
            foreach (var entry in entries ?? Enumerable.Empty<GamesCollectionViewEntry>())
            {
                byCopy[entry.Id] = entry;
                foreach (var copy in entry.Copies ?? new List<GameCopyEntry>())
                {
                    if (copy?.Game != null && !byCopy.ContainsKey(copy.Game.Id))
                    {
                        byCopy[copy.Game.Id] = entry;
                    }
                }
            }

            var best = new Dictionary<GamesCollectionViewEntry, AchievementProgress.Entry>();
            foreach (var item in progress ?? Enumerable.Empty<AchievementProgress.Entry>())
            {
                if (byCopy.TryGetValue(item.GameId, out var entry) &&
                    (!best.TryGetValue(entry, out var other) || GetPercent(item.Unlocked, item.Total) > GetPercent(other.Unlocked, other.Total)))
                {
                    best[entry] = item;
                }
            }

            return best.
                Where(a => Qualifies(a.Value.Unlocked, a.Value.Total)).
                Select(a => new Pick { Entry = a.Key, Unlocked = a.Value.Unlocked, Total = a.Value.Total }).
                OrderByDescending(a => GetPercent(a.Unlocked, a.Total)).
                ThenBy(a => a.Total - a.Unlocked).
                ThenByDescending(a => a.Entry.LastActivity ?? DateTime.MinValue).
                Take(max).
                ToList();
        }

        /// <summary>"3 left · 92 %", rounded down so that an unfinished game never says 100 %.</summary>
        public static string GetText(int unlocked, int total)
        {
            var left = total - unlocked;
            var percent = Math.Floor(GetPercent(unlocked, total)).ToString("0", Localization.ApplicationLanguageCultureInfo);
            return left == 1 ?
                string.Format(ResourceProvider.GetString("LOCAtrioHomeAchievementLeft"), percent) :
                string.Format(ResourceProvider.GetString("LOCAtrioHomeAchievementsLeft"), left, percent);
        }
    }
}
