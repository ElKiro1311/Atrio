using Playnite.Plugins;
using Playnite.SDK;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    /// <summary>An achievement of the person in the activity of a game.</summary>
    public class UnlockedAchievement
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Icon { get; set; }
        public DateTime When { get; set; }
    }

    /// <summary>What the person did with a game one day.</summary>
    public class PlayerDay
    {
        public DateTime Day { get; set; }

        /// <summary>The last moment of that day, to put it in its place among the posts.</summary>
        public DateTime Last { get; set; }
        public double Seconds { get; set; }
        public List<UnlockedAchievement> Achievements { get; set; } = new List<UnlockedAchievement>();
    }

    /// <summary>What the person did with a game, day by day: the time they played and the achievements they unlocked.</summary>
    public static class PlayerActivity
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        /// <summary>Less than this isn't a day of playing.</summary>
        private const double MinimumSeconds = 60;

        public static List<PlayerDay> Group(IEnumerable<PlaySession> sessions, IEnumerable<UnlockedAchievement> achievements)
        {
            var days = new Dictionary<DateTime, PlayerDay>();
            PlayerDay get(DateTime local)
            {
                if (!days.TryGetValue(local.Date, out var day))
                {
                    days[local.Date] = day = new PlayerDay { Day = local.Date, Last = local };
                }

                if (local > day.Last)
                {
                    day.Last = local;
                }

                return day;
            }

            foreach (var session in sessions ?? Enumerable.Empty<PlaySession>())
            {
                // A session belongs to the day it started: playing until late is still that night.
                var start = PlayHistory.ToUtc(session.Start).ToLocalTime();
                get(start).Seconds += session.Seconds;
            }

            foreach (var achievement in achievements ?? Enumerable.Empty<UnlockedAchievement>())
            {
                get(achievement.When).Achievements.Add(achievement);
            }

            foreach (var day in days.Values)
            {
                day.Achievements = day.Achievements.OrderByDescending(a => a.When).ToList();
            }

            return days.Values.
                Where(a => a.Seconds >= MinimumSeconds || a.Achievements.Count > 0).
                OrderByDescending(a => a.Day).
                ToList();
        }

        /// <summary>The sessions Atrio counted, whatever store the game was played in.</summary>
        public static List<PlaySession> GetSessions(ICollection<Guid> games)
        {
            return PlayHistory.GetSessions(DateTime.UtcNow - PlayHistory.KeepFor, DateTime.UtcNow.AddDays(1)).Where(a => games.Contains(a.GameId)).ToList();
        }

        private static object Get(object item, string name) => item?.GetType().GetProperty(name)?.GetValue(item);

        /// <summary>The achievements unlocked in a game, as the add-on of the achievements has them. Reading them asks no store.</summary>
        public static Task<List<UnlockedAchievement>> GetAchievementsAsync(ExtensionFactory extensions, ICollection<Guid> games)
        {
            return Task.Run(() =>
            {
                var found = new List<UnlockedAchievement>();
                try
                {
                    if (extensions?.Plugins == null || !extensions.Plugins.TryGetValue(AtrioInfo.AchievementsPluginId, out var loaded))
                    {
                        return found;
                    }

                    var data = Get(loaded.Plugin, "AchievementDataService");
                    var read = data?.GetType().GetMethod("GetGameAchievementData", new[] { typeof(Guid) });
                    if (read == null)
                    {
                        return found;
                    }

                    foreach (var id in games)
                    {
                        if (!(read.Invoke(data, new object[] { id }) is object gameData) || !(Get(gameData, "Achievements") is IEnumerable achievements))
                        {
                            continue;
                        }

                        foreach (var achievement in achievements)
                        {
                            if (Get(achievement, "Unlocked") as bool? != true || !(Get(achievement, "UnlockTimeUtc") is DateTime when) || when.Year < 2000)
                            {
                                continue;
                            }

                            found.Add(new UnlockedAchievement
                            {
                                Name = Get(achievement, "DisplayName") as string ?? Get(achievement, "Name") as string,
                                Description = Get(achievement, "Description") as string,
                                Icon = Get(achievement, "UnlockedIconDisplay") as string ?? Get(achievement, "UnlockedIconPath") as string ?? Get(achievement, "Icon") as string,
                                When = DateTime.SpecifyKind(when, DateTimeKind.Utc).ToLocalTime()
                            });
                        }
                    }
                }
                catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Debug(e, "Failed to read the achievements for the activity.");
                }

                // The same achievement in two copies of the game is one.
                return found.GroupBy(a => a.Name).Select(a => a.OrderBy(b => b.When).First()).ToList();
            });
        }
    }
}
