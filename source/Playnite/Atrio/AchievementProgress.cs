using Playnite.Plugins;
using Playnite.SDK;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    /// <summary>
    /// How far each game is in its achievements, as the add-on of the achievements keeps it for themes.
    /// The add-on is not a reference of this project, so it is reached by reflection; without it, or if
    /// anything there changes, there is simply nothing.
    /// </summary>
    public static class AchievementProgress
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
        public static async Task<List<Entry>> GetAsync(ExtensionFactory extensions)
        {
            var result = new List<Entry>();
            try
            {
                if (extensions?.Plugins == null || !extensions.Plugins.TryGetValue(AtrioInfo.AchievementsPluginId, out var loaded))
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

        /// <summary>
        /// How many achievements were unlocked since then, in any game. Only the games whose last unlock is that
        /// recent are opened, so it reads a handful of games, not the whole library. Null without the add-on.
        /// </summary>
        public static async Task<int?> CountUnlockedSinceAsync(ExtensionFactory extensions, DateTime sinceUtc)
        {
            try
            {
                if (extensions?.Plugins == null || !extensions.Plugins.TryGetValue(AtrioInfo.AchievementsPluginId, out var loaded))
                {
                    return null;
                }

                var plugin = loaded.Plugin;
                var service = plugin.GetType().GetProperty("ThemeIntegrationService")?.GetValue(plugin);
                var ensure = service?.GetType().GetMethod("EnsureAllGamesThemeDataLoaded");
                if (ensure != null)
                {
                    await Task.Run(() => ensure.Invoke(service, new object[] { false, false }));
                }

                var settings = plugin.GetType().GetProperty("Settings")?.GetValue(plugin);
                var data = plugin.GetType().GetProperty("AchievementDataService")?.GetValue(plugin);
                var get = data?.GetType().GetMethod("GetGameAchievementData", new[] { typeof(Guid) });
                if (get == null || !(settings?.GetType().GetProperty("AllGamesWithAchievements")?.GetValue(settings) is IEnumerable games))
                {
                    return null;
                }

                // The day before, because the add-on keeps that date in local time.
                var since = sinceUtc.ToLocalTime().Date.AddDays(-1);
                var candidates = new List<Guid>();
                foreach (var game in games)
                {
                    var type = game?.GetType();
                    if (type?.GetProperty("GameId")?.GetValue(game) is Guid id &&
                        type.GetProperty("LastUnlockDate")?.GetValue(game) is DateTime last &&
                        last >= since)
                    {
                        candidates.Add(id);
                    }
                }

                return await Task.Run(() =>
                {
                    var count = 0;
                    foreach (var id in candidates)
                    {
                        if (!(get.Invoke(data, new object[] { id }) is object gameData) ||
                            !(gameData.GetType().GetProperty("Achievements")?.GetValue(gameData) is IEnumerable achievements))
                        {
                            continue;
                        }

                        foreach (var achievement in achievements)
                        {
                            if (achievement?.GetType().GetProperty("UnlockTimeUtc")?.GetValue(achievement) is DateTime unlocked &&
                                unlocked >= sinceUtc)
                            {
                                count++;
                            }
                        }
                    }

                    return (int?)count;
                });
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, "Failed to count the achievements of the week.");
                return null;
            }
        }
    }
}
