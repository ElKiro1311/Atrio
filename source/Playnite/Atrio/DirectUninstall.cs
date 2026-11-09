using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    /// <summary>
    /// Uninstalling without going through the window of the store, for the stores that take the order
    /// from outside. The person says yes in Atrio first, since no store asks any more.
    /// </summary>
    public static class DirectUninstall
    {
        public static UninstallController Wrap(Game game, UninstallController controller)
        {
            if (GameUnifier.Current?.Settings.DirectInstall == false || controller == null)
            {
                return controller;
            }

            var addon = controller.GetType().Assembly.GetName().Name;
            if (game.PluginId == SteamContent.SteamPluginId && addon == "SteamLibrary" && int.TryParse(game.GameId, out _))
            {
                return new SteamDirectUninstallController(game, controller);
            }

            try
            {
                // Only the add-ons Atrio knows: another add-on with the same store could keep its games elsewhere.
                if (game.PluginId == EaPluginId && addon == "EaLibrary")
                {
                    var entry = WindowsUninstallEntries.FindEa(WindowsUninstallEntries.Read(), game.InstallDirectory);
                    return entry == null ? controller : new RegisteredUninstallController(game, controller, entry, "EA");
                }

                if (game.PluginId == AmazonPluginId && addon == "AmazonGamesLibrary")
                {
                    var entry = WindowsUninstallEntries.FindAmazon(WindowsUninstallEntries.Read(), game.GameId, game.InstallDirectory);
                    return entry == null ? controller : new RegisteredUninstallController(game, controller, entry, "Amazon", answersDialogs: true);
                }

                if (game.PluginId == ItchPluginId && addon == "ItchioLibrary" && long.TryParse(game.GameId, out _) &&
                    ButlerClient.Executable != null && ButlerClient.Database != null)
                {
                    return new ItchDirectUninstallController(game, controller);
                }

                if (game.PluginId == XboxPluginId && addon == "XboxLibrary" && game.GameId?.Contains("_") == true &&
                    !game.GameId.StartsWith("CONSOLE_", StringComparison.OrdinalIgnoreCase))
                {
                    return new XboxDirectUninstallController(game, controller);
                }

                if (game.PluginId == UbisoftPluginId && addon == "UplayLibrary" && !game.GameId.IsNullOrWhiteSpace())
                {
                    return new UbisoftDirectUninstallController(game, controller);
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                LogManager.GetLogger().Warn(e, $"Failed to prepare the direct uninstall of {game.GameId}.");
            }

            return controller;
        }

        /// <summary>
        /// Whether Atrio would remove this game itself, without the window of its store: what the storage page
        /// tells before deleting several games at once. Same conditions as <see cref="Wrap"/>, without the add-on.
        /// </summary>
        public static bool IsDirect(Game game)
        {
            if (game == null || GameUnifier.Current?.Settings.DirectInstall == false)
            {
                return false;
            }

            try
            {
                if (game.PluginId == SteamContent.SteamPluginId)
                {
                    return int.TryParse(game.GameId, out _);
                }

                if (game.PluginId == EaPluginId)
                {
                    return WindowsUninstallEntries.FindEa(WindowsUninstallEntries.Read(), game.InstallDirectory) != null;
                }

                if (game.PluginId == AmazonPluginId)
                {
                    return WindowsUninstallEntries.FindAmazon(WindowsUninstallEntries.Read(), game.GameId, game.InstallDirectory) != null;
                }

                if (game.PluginId == ItchPluginId)
                {
                    return long.TryParse(game.GameId, out _) && ButlerClient.Executable != null && ButlerClient.Database != null;
                }

                if (game.PluginId == XboxPluginId)
                {
                    return game.GameId?.Contains("_") == true && !game.GameId.StartsWith("CONSOLE_", StringComparison.OrdinalIgnoreCase);
                }

                if (game.PluginId == UbisoftPluginId)
                {
                    return !game.GameId.IsNullOrWhiteSpace();
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                LogManager.GetLogger().Debug(e, $"Failed to tell if {game.GameId} uninstalls without its store.");
            }

            return false;
        }

        public static readonly Guid EaPluginId = Guid.Parse("85dd7072-2f20-4e76-a007-41035e390724");
        public static readonly Guid AmazonPluginId = Guid.Parse("402674cd-4af6-4886-b6ec-0e695bfa0688");
        public static readonly Guid ItchPluginId = Guid.Parse("00000001-ebb2-4eec-abcb-7c89937a42bb");
        public static readonly Guid XboxPluginId = Guid.Parse("7e4fbb5e-2ae3-48d4-8ba0-6b30e7a4e287");
        public static readonly Guid UbisoftPluginId = Guid.Parse("c2f038e5-8b92-4877-91f1-da9094155fc5");

        /// <summary>"Uninstall X? Its files (12.3 GB) are deleted from D:\...", or without the size and the folder when they aren't known.</summary>
        public static string GetQuestion(string name, long size, string folder)
        {
            if (size > 0 && !folder.IsNullOrEmpty())
            {
                return string.Format(ResourceProvider.GetString("LOCAtrioUninstallAsk"), name, FormatSize(size), folder);
            }

            return string.Format(ResourceProvider.GetString("LOCAtrioUninstallAskShort"), name);
        }

        public static string FormatSize(long bytes)
        {
            var culture = Localization.ApplicationLanguageCultureInfo;
            if (bytes >= 1L << 40)
            {
                return (bytes / (double)(1L << 40)).ToString("0.##", culture) + " TB";
            }

            if (bytes >= 1L << 30)
            {
                return (bytes / (double)(1L << 30)).ToString("0.#", culture) + " GB";
            }

            return Math.Max(1, bytes / (1L << 20)).ToString("0", culture) + " MB";
        }
    }

    /// <summary>Something that uninstalls on its own and needs the yes of the person before it starts.</summary>
    public interface IDirectUninstall
    {
        /// <summary>What to ask, with the name, the size and the folder of the game when they are known.</summary>
        string GetQuestion();
    }

    /// <summary>
    /// Uninstalls a game of Steam with the order the remote downloads of Steam use, without opening Steam.
    /// If the client doesn't take the order, Steam opens its own uninstall as always.
    /// </summary>
    public class SteamDirectUninstallController : UninstallController, IDirectUninstall
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        // How long the client gets to show it took the order: it marks the game as being deleted at once.
        private static readonly TimeSpan startPatience = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan checkPause = TimeSpan.FromSeconds(1);

        private readonly UninstallController store;
        private readonly CancellationTokenSource watcher = new CancellationTokenSource();
        private bool usesStore;

        public SteamDirectUninstallController(Game game, UninstallController store) : base(game)
        {
            this.store = store;
            Name = store.Name;
        }

        public string GetQuestion()
        {
            var manifest = SteamClientFiles.FindManifest(Game.GameId);
            return DirectUninstall.GetQuestion(Game.Name, manifest?.SizeOnDisk ?? 0, manifest?.InstallPath);
        }

        public override async void Uninstall(UninstallActionArgs args)
        {
            try
            {
                if (await SteamRemoteInstall.RequestUninstallAsync(Game.GameId, watcher.Token) && await WatchAsync())
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (watcher.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, $"Failed to uninstall {Game.GameId} directly.");
            }

            if (!watcher.IsCancellationRequested)
            {
                UseStore(args);
            }
        }

        /// <summary>False if the client never showed that it took the order.</summary>
        private async Task<bool> WatchAsync()
        {
            var ordered = DateTime.Now;
            var started = false;
            while (true)
            {
                await Task.Delay(checkPause, watcher.Token);
                SteamAppManifest manifest;
                try
                {
                    manifest = await Task.Run(() => SteamClientFiles.FindManifest(Game.GameId), watcher.Token);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    continue;
                }

                if (manifest == null)
                {
                    logger.Info($"Steam uninstalled {Game.GameId}.");
                    InvokeOnUninstalled(new GameUninstalledEventArgs());
                    return true;
                }

                started |= manifest.IsUninstalling;
                if (!started && DateTime.Now - ordered > startPatience)
                {
                    logger.Warn($"The Steam client didn't start to uninstall {Game.GameId}.");
                    return false;
                }
            }
        }

        /// <summary>The add-on does its usual uninstall: Steam asks and the add-on notices when the game is gone.</summary>
        private void UseStore(UninstallActionArgs args)
        {
            if (usesStore)
            {
                return;
            }

            usesStore = true;
            if (AtrioInfo.GetTestSetting("ATRIO_SIN_TIENDA") != null)
            {
                logger.Info("Test run: the store would open now to uninstall.");
                return;
            }

            store.Uninstalled += (_, e) => InvokeOnUninstalled(e);
            try
            {
                store.Uninstall(args);
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                // Nobody upstream catches it after an await: an exception here would close the program.
                logger.Error(e, $"The store couldn't uninstall {Game.GameId}.");
            }
        }

        public override void Dispose()
        {
            watcher.Cancel();
            store.Dispose();
            base.Dispose();
        }
    }
}
