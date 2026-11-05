using Playnite.Converters;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Playnite.Atrio
{
    /// <summary>
    /// A store-specific copy of a game, as shown in the store selector.
    /// </summary>
    public class GameCopyEntry
    {
        private static readonly PlayTimeToStringConverter playtimeConverter = new PlayTimeToStringConverter();

        public Game Game { get; }
        public LibraryPlugin Plugin { get; }
        public Emulator Emulator { get; }
        public bool IsPrimary { get; }
        public bool IsUserChoice { get; }

        public Guid GameId => Game.Id;
        public string GameName => Game.Name;
        public string StoreName => Plugin?.Name ?? Emulator?.Name ?? ResourceProvider.GetString("LOCAtrioManualStore");
        public object StoreIcon => Emulator != null ? EmulatorStores.GetIcon(Emulator.Name) : Plugin?.LibraryIcon == null ? null : ImageSourceManager.GetImage(Plugin.LibraryIcon, true);
        public System.Windows.Media.Brush CardBrush => Emulator != null ? EmulatorStores.GetCardBrush(Emulator.Name) : StoreBranding.GetCardBrush(Plugin?.Name);
        public System.Windows.Media.Brush ColorBrush => Emulator != null ? EmulatorStores.GetBrush(Emulator.Name) : StoreBranding.GetBrush(Plugin?.Name);
        public bool IsInstalled => Game.IsInstalled;
        public bool CanInstall => !Game.IsInstalled && Game.PluginId != Guid.Empty;
        public ulong Playtime => Game.Playtime;
        public DateTime? LastActivity => Game.LastActivity;

        public string PlaytimeText => Game.Playtime == 0 ? string.Empty : playtimeConverter.Convert(Game.Playtime, typeof(string), null, null)?.ToString();

        public string StatusText
        {
            get
            {
                if (Game.IsInstalled)
                {
                    return ResourceProvider.GetString("LOCAtrioCopyInstalled");
                }

                return Game.PluginId == Guid.Empty ?
                    ResourceProvider.GetString("LOCAtrioCopyNotAvailable") :
                    ResourceProvider.GetString("LOCAtrioCopyNotInstalled");
            }
        }

        public string ActionText => ResourceProvider.GetString(Game.IsInstalled ? "LOCPlayGame" : "LOCInstallGame");

        public GameCopyEntry(Game game, LibraryPlugin plugin, bool isPrimary, bool isUserChoice)
        {
            Game = game;
            Plugin = plugin;
            Emulator = plugin == null ? EmulatorStores.GetEmulator(game) : null;
            IsPrimary = isPrimary;
            IsUserChoice = isUserChoice;
        }

        public static List<GameCopyEntry> FromGroup(UnifiedGame group, Func<Game, LibraryPlugin> pluginResolver)
        {
            return group.Copies.
                Select(a => new GameCopyEntry(a, pluginResolver?.Invoke(a), a.Id == group.Primary.Id, a.Id == group.Primary.Id && group.HasUserChoice)).
                ToList();
        }
    }
}
