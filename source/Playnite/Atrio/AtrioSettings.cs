using Playnite.Common;
using Playnite.SDK;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Playnite.Atrio
{
    /// <summary>
    /// Settings owned by Atrio. They live in their own file so that config.json
    /// stays readable by a regular Playnite installation.
    /// </summary>
    public class AtrioSettings
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        public const string FileName = "atrio.json";

        /// <summary>Shows copies of the same game from several stores as a single entry.</summary>
        public bool UnifyDuplicates { get; set; } = true;

        /// <summary>Asks which store to use when a unified game has more than one valid option.</summary>
        public bool AskStoreOnStart { get; set; } = true;

        /// <summary>Tells the Steam client to install a game instead of opening its window.</summary>
        public bool SteamDirectInstall { get; set; } = true;

        /// <summary>Plugin IDs from most to least preferred. Unknown stores go last.</summary>
        public List<Guid> StorePriority { get; set; } = GetDefaultStorePriority();

        /// <summary>Copy the user picked for a group, by group key.</summary>
        public Dictionary<string, Guid> PreferredCopies { get; set; } = new Dictionary<string, Guid>();

        /// <summary>Games the user took out of automatic unification.</summary>
        public List<Guid> SplitGames { get; set; } = new List<Guid>();

        /// <summary>Manually joined games: game ID to manual group ID.</summary>
        public Dictionary<Guid, string> ManualGroups { get; set; } = new Dictionary<Guid, string>();

        /// <summary>Filter presets that are dynamic collections. Any other preset stays out of that page.</summary>
        public List<Guid> DynamicCollections { get; set; } = new List<Guid>();

        /// <summary>Collections shown as a shelf of the start page, in that order.</summary>
        public List<Guid> HomeCollections { get; set; } = new List<Guid>();

        /// <summary>Accent color override, for example #FF4F9DFF. Empty keeps the theme's own.</summary>
        public string AccentColor { get; set; }

        public static string FilePath => Path.Combine(PlaynitePaths.ConfigRootPath, FileName);

        public static List<Guid> GetDefaultStorePriority()
        {
            return new List<Guid>
            {
                Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB"), // Steam
                Guid.Parse("00000002-DBD1-46C6-B5D0-B1BA559D10E4"), // Epic
                Guid.Parse("AEBE8B7C-6DC3-4A66-AF31-E7375C6B5E9E"), // GOG
                Guid.Parse("7e4fbb5e-2ae3-48d4-8ba0-6b30e7a4e287"), // Xbox
                Guid.Parse("E3C26A3D-D695-4CB7-A769-5FF7612C7EDD"), // Battle.net
                Guid.Parse("85DD7072-2F20-4E76-A007-41035E390724"), // EA app
                Guid.Parse("C2F038E5-8B92-4877-91F1-DA9094155FC5"), // Ubisoft Connect
                Guid.Parse("402674cd-4af6-4886-b6ec-0e695bfa0688"), // Amazon Games
                Guid.Parse("96e8c4bc-ec5c-4c8b-87e7-18ee5a690626"), // Humble
                Guid.Parse("00000001-EBB2-4EEC-ABCB-7C89937A42BB")  // itch.io
            };
        }

        public static AtrioSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var settings = Playnite.SDK.Data.Serialization.FromJsonFile<AtrioSettings>(FilePath);
                    if (settings != null)
                    {
                        settings.StorePriority = settings.StorePriority ?? GetDefaultStorePriority();
                        settings.PreferredCopies = settings.PreferredCopies ?? new Dictionary<string, Guid>();
                        settings.SplitGames = settings.SplitGames ?? new List<Guid>();
                        settings.ManualGroups = settings.ManualGroups ?? new Dictionary<Guid, string>();
                        settings.DynamicCollections = settings.DynamicCollections ?? new List<Guid>();
                        settings.HomeCollections = settings.HomeCollections ?? new List<Guid>();
                        return settings;
                    }
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to load Atrio settings, using defaults.");
            }

            return new AtrioSettings();
        }

        public void Save()
        {
            try
            {
                FileSystem.WriteStringToFile(FilePath, Playnite.SDK.Data.Serialization.ToJson(this, true));
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to save Atrio settings.");
            }
        }
    }
}
