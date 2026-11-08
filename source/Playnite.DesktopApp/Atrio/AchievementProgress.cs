using Playnite.Atrio;
using Playnite.DesktopApp.ViewModels;
using Playnite.SDK;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Playnite.DesktopApp.Atrio
{
    /// <summary>
    /// How far each game is in its achievements, as the add-on of the achievements keeps it for themes.
    /// The add-on is not a reference of this project, so it is reached by reflection; without it, or if
    /// anything there changes, there is simply nothing.
    /// </summary>
    internal static class AchievementProgress
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public class Entry
        {
            public Guid GameId { get; set; }
            public int Unlocked { get; set; }
            public int Total { get; set; }
        }

        /// <summary>
        /// The add-on puts together its summary of every game the first time a theme asks for it, on the
        /// thread of the window but in the background, so the page does not wait for it.
        /// </summary>
        public static async Task<List<Entry>> GetAsync(DesktopAppViewModel main)
        {
            var result = new List<Entry>();
            try
            {
                if (main?.Extensions?.Plugins == null || !main.Extensions.Plugins.TryGetValue(AtrioInfo.AchievementsPluginId, out var loaded))
                {
                    return result;
                }

                var plugin = loaded.Plugin;
                var service = plugin.GetType().GetProperty("ThemeIntegrationService")?.GetValue(plugin);
                var ensure = service?.GetType().GetMethod("EnsureAllGamesThemeDataLoaded");
                if (ensure != null)
                {
                    await Task.Run(() => ensure.Invoke(service, new object[] { false, false }));
                }

                var settings = plugin.GetType().GetProperty("Settings")?.GetValue(plugin);
                if (!(settings?.GetType().GetProperty("AllGamesWithAchievements")?.GetValue(settings) is IEnumerable games))
                {
                    return result;
                }

                foreach (var game in games)
                {
                    var type = game?.GetType();
                    if (type?.GetProperty("GameId")?.GetValue(game) is Guid id &&
                        type.GetProperty("UnlockedCount")?.GetValue(game) is int unlocked &&
                        type.GetProperty("AchievementCount")?.GetValue(game) is int total)
                    {
                        result.Add(new Entry { GameId = id, Unlocked = unlocked, Total = total });
                    }
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, "Failed to read the achievement progress of the games.");
            }

            return result;
        }
    }
}
