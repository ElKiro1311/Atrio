using Playnite.Plugins;
using Playnite.SDK;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    /// <summary>An achievement the person has, with how many players have it too.</summary>
    public class RareAchievement
    {
        public Guid GameId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Icon { get; set; }

        /// <summary>Percent of the players of the game that have it.</summary>
        public double Percent { get; set; }
        public DateTime? Unlocked { get; set; }
    }

    /// <summary>
    /// The rarest achievements the person got, from the data of the add-on of the achievements. Like the
    /// progress of the games, it's reached by reflection: without the add-on, or if it changes, there's nothing.
    /// </summary>
    public static class RareAchievements
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        /// <summary>The rarest first, at most two of a game so that one game doesn't fill the case.</summary>
        public static List<RareAchievement> Choose(IEnumerable<RareAchievement> unlocked, int count, int perGame = 2)
        {
            return (unlocked ?? Enumerable.Empty<RareAchievement>()).
                Where(a => a != null && a.Percent > 0 && !a.Name.IsNullOrEmpty()).
                OrderBy(a => a.Percent).
                ThenByDescending(a => a.Unlocked ?? DateTime.MinValue).
                GroupBy(a => a.GameId).
                SelectMany(a => a.Take(perGame)).
                OrderBy(a => a.Percent).
                ThenByDescending(a => a.Unlocked ?? DateTime.MinValue).
                Take(count).
                ToList();
        }

        private static object Get(object item, string name) => item?.GetType().GetProperty(name)?.GetValue(item);

        public static async Task<List<RareAchievement>> GetAsync(ExtensionFactory extensions, int count)
        {
            try
            {
                if (extensions?.Plugins == null || !extensions.Plugins.TryGetValue(AtrioInfo.AchievementsPluginId, out var loaded))
                {
                    return new List<RareAchievement>();
                }

                var plugin = loaded.Plugin;
                var service = Get(plugin, "ThemeIntegrationService");
                var ensure = service?.GetType().GetMethod("EnsureAllGamesThemeDataLoaded");
                if (ensure != null)
                {
                    await Task.Run(() => ensure.Invoke(service, new object[] { false, false }));
                }

                var settings = Get(plugin, "Settings");
                var data = Get(plugin, "AchievementDataService");
                var read = data?.GetType().GetMethod("GetGameAchievementData", new[] { typeof(Guid) });
                if (read == null || !(Get(settings, "AllGamesWithAchievements") is IEnumerable games))
                {
                    return new List<RareAchievement>();
                }

                // Only the games with something unlocked are opened.
                var candidates = new List<Guid>();
                foreach (var game in games)
                {
                    if (Get(game, "GameId") is Guid id && Get(game, "UnlockedCount") is int unlocked && unlocked > 0)
                    {
                        candidates.Add(id);
                    }
                }

                return await Task.Run(() =>
                {
                    var found = new List<RareAchievement>();
                    foreach (var id in candidates)
                    {
                        if (!(read.Invoke(data, new object[] { id }) is object gameData) || !(Get(gameData, "Achievements") is IEnumerable achievements))
                        {
                            continue;
                        }

                        foreach (var achievement in achievements)
                        {
                            if (Get(achievement, "Unlocked") as bool? != true || !(Get(achievement, "GlobalPercentUnlocked") is double percent) || percent <= 0)
                            {
                                continue;
                            }

                            found.Add(new RareAchievement
                            {
                                GameId = id,
                                Name = Get(achievement, "DisplayName") as string ?? Get(achievement, "Name") as string,
                                Description = Get(achievement, "Description") as string,
                                Icon = Get(achievement, "UnlockedIconDisplay") as string ?? Get(achievement, "UnlockedIconPath") as string ?? Get(achievement, "Icon") as string,
                                Percent = percent,
                                Unlocked = Get(achievement, "UnlockTimeUtc") as DateTime?
                            });
                        }
                    }

                    return Choose(found, count);
                });
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, "Failed to read the rarest achievements.");
                return new List<RareAchievement>();
            }
        }
    }
}
