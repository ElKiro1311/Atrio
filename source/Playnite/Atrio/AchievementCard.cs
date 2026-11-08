using Playnite.Converters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace Playnite.Atrio
{
    /// <summary>
    /// The achievements card of a game page: the first ones, more of them with each click
    /// right there, which ones to show, the easiest ones still missing, and where to find a guide.
    /// </summary>
    public static class AchievementCard
    {
        /// <summary>How many icons the card shows before "Show more".</summary>
        public const int FirstCount = 20;

        /// <summary>How many more each click brings.</summary>
        public const int Step = 40;

        /// <summary>How many of the easiest missing ones the card points out.</summary>
        public const int NextCount = 3;

        /// <summary>From this much progress on, an achievement counts as almost done and goes before the common ones.</summary>
        public const double AlmostDone = 50;

        public const int ShowAll = 0;
        public const int ShowUnlocked = 1;
        public const int ShowLocked = 2;

        public const string SteamProvider = "Steam";
        public const string RetroProvider = "RetroAchievements";

        /// <summary>Which ones the card shows. It stays while Atrio is open, from one game to the next.</summary>
        public static int Filter { get; set; } = ShowAll;

        /// <summary>Whether the rarest ones go first. It stays like <see cref="Filter"/>.</summary>
        public static bool RarestFirst { get; set; }

        /// <summary>What a click on the button under the icons leaves: more while some are left, the first ones again after that.</summary>
        public static int Next(int shown, int total)
        {
            return shown < total ? shown + Step : FirstCount;
        }

        /// <summary>
        /// The achievements the card shows, in its order: only the unlocked or only the locked ones if asked,
        /// and the unlocked first with the newest on top, or the rarest first.
        /// </summary>
        public static List<object> Arrange(object list, int filter, bool rarestFirst)
        {
            IEnumerable<object> items = AchievementItems.Get(list);
            if (filter == ShowUnlocked)
            {
                items = items.Where(AchievementItems.IsUnlocked);
            }
            else if (filter == ShowLocked)
            {
                items = items.Where(a => !AchievementItems.IsUnlocked(a));
            }

            if (rarestFirst)
            {
                return items.OrderBy(a => AchievementItems.Percent(a) ?? double.MaxValue).ToList();
            }

            return items.OrderByDescending(AchievementItems.IsUnlocked).ThenByDescending(AchievementItems.UnlockTime).ToList();
        }

        /// <summary>How many the card has to show with that filter.</summary>
        public static int Count(object list, int filter)
        {
            return Arrange(list, filter, false).Count;
        }

        /// <summary>
        /// The easiest ones still missing: the almost done first, then the ones most players have.
        /// Secret ones stay secret and the ones nobody can get any more are left out. Without
        /// progress or percentages there is nothing to go by, and the list is empty.
        /// </summary>
        public static List<object> PickNext(object list, int count = NextCount)
        {
            return AchievementItems.Get(list).
                Where(a => !AchievementItems.IsUnlocked(a) && !AchievementItems.IsTitleHidden(a) && !AchievementItems.IsUnobtainable(a)).
                Select(a => new { Item = a, Progress = AchievementItems.ProgressPercent(a), Percent = AchievementItems.Percent(a) }).
                Where(a => a.Progress > 0 || a.Percent != null).
                OrderByDescending(a => a.Progress >= AlmostDone).
                ThenByDescending(a => a.Progress >= AlmostDone ? a.Progress : 0).
                ThenByDescending(a => a.Percent ?? -1).
                ThenByDescending(a => a.Progress).
                Take(count).
                Select(a => a.Item).
                ToList();
        }

        /// <summary>What the arrows do with the card of an achievement open: -1 the one before, 1 the one after, 0 nothing.</summary>
        public static int GetKeyStep(Key key)
        {
            switch (key)
            {
                case Key.Left:
                case Key.Up:
                    return -1;
                case Key.Right:
                case Key.Down:
                    return 1;
                default:
                    return 0;
            }
        }

        /// <summary>
        /// A search for the game and the achievement, plus a word like "achievement" in the language of the app.
        /// Google answers the browser of Atrio with its page for unusual traffic, so the search goes to DuckDuckGo.
        /// Without quotes: most guides use the English names, and quotes around a translated name leave one result or none.
        /// </summary>
        public static string GetGuideUrl(string game, string achievement, string words)
        {
            var parts = new[] { game, achievement, words }.Select(a => a?.Trim()).Where(a => !a.IsNullOrEmpty()).ToList();
            return "https://duckduckgo.com/?q=" + Uri.EscapeDataString(string.Join(" ", parts));
        }

        /// <summary>The page of the achievement on RetroAchievements, with the comments and guides of other players. Its id is a number.</summary>
        public static string GetRetroUrl(object achievement)
        {
            var id = AchievementItems.ApiName(achievement)?.Trim();
            return AchievementItems.ProviderKey(achievement) == RetroProvider && !id.IsNullOrEmpty() && id.All(char.IsDigit)
                ? "https://retroachievements.org/achievement/" + id
                : null;
        }

        /// <summary>The achievement guides of the game on Steam, the best rated first.</summary>
        public static string GetSteamGuidesUrl(string appId)
        {
            appId = appId?.Trim();
            return !appId.IsNullOrEmpty() && appId.All(char.IsDigit)
                ? $"https://steamcommunity.com/app/{appId}/guides/?browsefilter=toprated&requiredtags%5B%5D=Achievements"
                : null;
        }

        /// <summary>
        /// Where a guide for the achievement is without searching: its page on RetroAchievements,
        /// or the achievement guides of the game on Steam. Nothing for the other stores.
        /// </summary>
        public static string GetDirectGuideUrl(object achievement, string steamAppId)
        {
            return GetRetroUrl(achievement) ?? GetSteamGuidesUrl(steamAppId);
        }
    }
}
