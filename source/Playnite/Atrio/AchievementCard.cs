using System;
using System.Linq;

namespace Playnite.Atrio
{
    /// <summary>
    /// The achievements card of a game page: the first ones, more of them with each click
    /// right there, and a web search for a guide on how to get one.
    /// </summary>
    public static class AchievementCard
    {
        /// <summary>How many icons the card shows before "Show more".</summary>
        public const int FirstCount = 20;

        /// <summary>How many more each click brings.</summary>
        public const int Step = 40;

        /// <summary>What a click on the button under the icons leaves: more while some are left, the first ones again after that.</summary>
        public static int Next(int shown, int total)
        {
            return shown < total ? shown + Step : FirstCount;
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
    }
}
