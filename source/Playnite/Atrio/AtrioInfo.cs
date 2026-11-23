using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Playnite.Atrio
{
    /// <summary>A store whose add-on can't be bundled because of its license, so it is downloaded on request.</summary>
    public class OnlineStore
    {
        public string AddonId { get; set; }
        public string Name { get; set; }
    }

    public static class AtrioInfo
    {
        /// <summary>
        /// The published versions get theirs from the tag: this one is what a build of the source says. Only the number
        /// (x.y.z): it is what gets compared; the phase is shown after it (<see cref="Phase"/>).
        /// </summary>
        public const string Version = "1.0";

        /// <summary>The phase shown after the number (Semantic Versioning): «1.12.2-beta». Empty once Atrio is stable.</summary>
        public const string Phase = "beta";

        public const string ProjectUrl = "https://github.com/ElKiro1311/Atrio";

        public static Version CurrentVersion => System.Version.Parse(GetTestSetting("ATRIO_VERSION") ?? Version);

        /// <summary>The version of this copy as it is shown: «1.12.2-beta».</summary>
        public static string VersionLabel => Label(GetTestSetting("ATRIO_VERSION") ?? Version);

        /// <summary>A version as it is shown: 1.12.2 becomes «1.12.2-beta».</summary>
        public static string Label(Version version)
        {
            return version == null ? null : Label(version.ToString());
        }

        /// <summary>A version as it is shown: one that already carries its phase (1.12.3-beta) stays as it is.</summary>
        public static string Label(string version)
        {
            if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(Phase) || version.Contains("-"))
            {
                return version;
            }

            return version + "-" + Phase;
        }

        /// <summary>For the messages that come out before any language is loaded: in English, or in Spanish when Windows is.</summary>
        public static string Say(string english, string spanish)
        {
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? spanish : english;
        }

        /// <summary>Playnite Achievements: reads the achievements of the stores and of RetroAchievements.</summary>
        public static readonly Guid AchievementsPluginId = Guid.Parse("e6aad2c9-6e06-4d8d-ac55-ac3b252b5f7b");

        public const string AchievementsAddonId = "PlayniteAchievements";

        /// <summary>Universal Steam Metadata: fills in what a store doesn't provide, with no account or key.</summary>
        public static readonly Guid DefaultMetadataProvider = Guid.Parse("f2db8fb1-4981-4dc4-b087-05c782215b72");

        /// <summary>
        /// Add-ons shipped with the app but turned off until the user enables them in the Stores
        /// page: they are smaller stores, and each enabled add-on is code that runs at startup.
        /// </summary>
        public static readonly List<string> OptionalAddons = new List<string>
        {
            "LegacyGamesLibrary_34c3178f-6e1d-4e27-8885-99d4f031b168",
            "BigFishLibrary",
            "playnite-indiegala-plugin",
            "FanaticalLibrary_ef17cc27-95d4-45e7-bd49-214ba2e5f4b2",
            "GameJoltLibrary_555d58fd-a000-401b-972c-9230bed81aed",
            "HoYoPlayLibrary_faaf4c1f-14ca-487e-8f32-d7a40c6b9c9a",
            "StoveLibrary_2a62a584-2cc3-4220-8da6-cf4ac588a439",
            "RobotCacheLibrary_4de579c6-7179-4229-b776-7a3a9e902052",
            "ViveportLibrary",
            "PlayStationLibrary_Builtin",
            "ATLauncherInstanceImporter_40e56f44-4955-40ec-9bf3-682c4007e55b"
        };

        public static readonly List<OnlineStore> OnlineStores = new List<OnlineStore>
        {
            new OnlineStore { AddonId = "GamersGateLibrary", Name = "GamersGate" },
            new OnlineStore { AddonId = "CanRanBan_Library_GooglePlayGames", Name = "Google Play Games" },
            new OnlineStore { AddonId = "OculusLibraryPlugin_Playnite_Plugin", Name = "Meta Quest" },
            new OnlineStore { AddonId = "NintendoLibrary", Name = "Nintendo" }
        };

        /// <summary>
        /// Each folder is its own app with its own data, so two copies can run side by side.
        /// The desktop and fullscreen programs of one folder share it and find each other.
        /// </summary>
        public static string InstanceId { get; } = GetInstanceId();

        private static string GetInstanceId()
        {
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(PlaynitePaths.ProgramPath.ToLowerInvariant()));
                return BitConverter.ToString(hash, 0, 6).Replace("-", string.Empty);
            }
        }

        /// <summary>A portable app can end up somewhere read only, such as Program Files.</summary>
        public static bool CanWriteUserData()
        {
            var test = Path.Combine(PlaynitePaths.ConfigRootPath, $"atrio-{Guid.NewGuid():N}.tmp");
            try
            {
                Directory.CreateDirectory(PlaynitePaths.ConfigRootPath);
                File.WriteAllText(test, string.Empty);
                File.Delete(test);
                return true;
            }
            catch (Exception e) when (e is UnauthorizedAccessException || e is IOException)
            {
                return false;
            }
        }

        /// <summary>Test builds only, see AtrioCaptura. Regular builds ignore the environment.</summary>
        public static string GetTestSetting(string name)
        {
#if ATRIO_BANCO
            return Environment.GetEnvironmentVariable(name);
#else
            return null;
#endif
        }

        public static bool IsTestRun => !string.IsNullOrEmpty(GetTestSetting("ATRIO_CAPTURA"));
    }
}
