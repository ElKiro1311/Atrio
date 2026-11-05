using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.IO;

namespace Playnite.Atrio
{
    /// <summary>What the achievements add-on knows about the accounts of the user. It only reads.</summary>
    public static class AchievementsAccounts
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private static string ConfigPath => Path.Combine(PlaynitePaths.ExtensionsDataPath, AtrioInfo.AchievementsPluginId.ToString(), "config.json");

        /// <summary>
        /// First start of the add-on: its achievements come in the language of the app, and its button
        /// without text stays out of the top bar because the sidebar already has a labeled entry.
        /// </summary>
        public static void PrepareDefaults(string appLanguage)
        {
            if (File.Exists(ConfigPath))
            {
                return;
            }

            try
            {
                var persisted = new JObject { ["ShowTopMenuBarButton"] = false };
                if (appLanguage?.StartsWith("es", StringComparison.OrdinalIgnoreCase) == true)
                {
                    persisted["GlobalLanguage"] = "spanish";
                }

                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                File.WriteAllText(ConfigPath, new JObject { ["Persisted"] = persisted }.ToString());
            }
            catch (Exception e)
            {
                logger.Warn(e, "Failed to prepare the achievements settings.");
            }
        }

        /// <summary>Null when RetroAchievements is not connected.</summary>
        public static string GetRetroAchievementsUser()
        {
            if (!File.Exists(ConfigPath))
            {
                return null;
            }

            try
            {
                var settings = JObject.Parse(File.ReadAllText(ConfigPath));
                return (string)settings["Persisted"]?["ProviderSettings"]?["RetroAchievements"]?["RaUsername"];
            }
            catch (Exception e)
            {
                logger.Warn(e, "Failed to read the achievements settings.");
                return null;
            }
        }
    }
}
