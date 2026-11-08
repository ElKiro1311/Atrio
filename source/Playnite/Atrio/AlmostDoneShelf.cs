using Playnite.SDK;
using System;
using System.Globalization;

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
