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

            return controller;
        }

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
