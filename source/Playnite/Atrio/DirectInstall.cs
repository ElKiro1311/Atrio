using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    /// <summary>How the installs started from Atrio are going, by the id of the game.</summary>
    public static class DirectInstallProgress
    {
        private class Status
        {
            public int? Percent;
            public string Notice;
        }

        private static readonly ConcurrentDictionary<Guid, Status> statuses = new ConcurrentDictionary<Guid, Status>();

        public static event EventHandler<Guid> Changed;

        public static int? Get(Guid game)
        {
            return statuses.TryGetValue(game, out var status) ? status.Percent : null;
        }

        /// <summary>What the store is waiting for from the person, if anything. Already translated.</summary>
        public static string GetNotice(Guid game)
        {
            return statuses.TryGetValue(game, out var status) ? status.Notice : null;
        }

        public static void Set(Guid game, int? percent)
        {
            statuses[game] = new Status { Percent = percent };
            Changed?.Invoke(null, game);
        }

        public static void SetNotice(Guid game, string notice)
        {
            statuses[game] = new Status { Notice = notice };
            Changed?.Invoke(null, game);
        }

        public static void Clear(Guid game)
        {
            statuses.TryRemove(game, out _);
            Changed?.Invoke(null, game);
        }
    }

    /// <summary>
    /// Installs a game without making the person go through the window of its store. Whatever goes
    /// wrong ends in the install of the add-on, which opens that window: the worst case is what always happened.
    /// </summary>
    public abstract class DirectInstallController : InstallController
    {
        protected static readonly ILogger Logger = LogManager.GetLogger();

        private readonly InstallController store;
        private readonly CancellationTokenSource watcher = new CancellationTokenSource();
        private bool usesStore;

        protected DirectInstallController(Game game, InstallController store) : base(game)
        {
            this.store = store;
            Name = store.Name;
        }

        protected CancellationToken Cancellation => watcher.Token;

        /// <summary>False if the store has to take over, for any reason.</summary>
        protected abstract Task<bool> InstallDirectlyAsync(InstallActionArgs args);

        public override async void Install(InstallActionArgs args)
        {
            try
            {
                DirectInstallProgress.Set(Game.Id, 0);
                if (await InstallDirectlyAsync(args))
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                Logger.Warn(e, $"Failed to install {Game.GameId} directly.");
            }

            DirectInstallProgress.Clear(Game.Id);
            UseStore(args);
        }

        protected void Finish(string installDirectory)
        {
            DirectInstallProgress.Clear(Game.Id);
            InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData { InstallDirectory = installDirectory }));
        }

        protected void Abandon()
        {
            DirectInstallProgress.Clear(Game.Id);
            InvokeOnInstallationCancelled(new GameInstallationCancelledEventArgs());
        }

        /// <summary>The add-on does its usual install: it opens the store and notices when the game is there.</summary>
        protected void UseStore(InstallActionArgs args)
        {
            if (usesStore)
            {
                return;
            }

            usesStore = true;
            if (AtrioInfo.GetTestSetting("ATRIO_SIN_TIENDA") != null)
            {
                Logger.Info("Test run: the store would open now.");
                Abandon();
                return;
            }

            store.Installed += (_, e) =>
            {
                DirectInstallProgress.Clear(Game.Id);
                InvokeOnInstalled(e);
            };
            store.InstallCancelled += (_, e) => Abandon();
            store.Install(args);
        }

        public override void Dispose()
        {
            watcher.Cancel();
            DirectInstallProgress.Clear(Game.Id);
            if (usesStore)
            {
                store.Dispose();
            }

            base.Dispose();
        }
    }

    /// <summary>
    /// For the stores that only install through their own window: the add-on opens it and notices when the
    /// game is there, and in the meantime the steps of the window get done. If some step can't be done
    /// the window stays there, and the person finishes it by hand as always.
    /// </summary>
    public abstract class AssistedInstallController : DirectInstallController
    {
        protected AssistedInstallController(Game game, InstallController store) : base(game, store)
        {
        }

        protected abstract Task AssistAsync();

        protected override async Task<bool> InstallDirectlyAsync(InstallActionArgs args)
        {
            UseStore(args);
            try
            {
                await AssistAsync();
            }
            catch (Exception e) when (!(e is OperationCanceledException) && !PlayniteEnvironment.ThrowAllErrors)
            {
                Logger.Warn(e, $"Couldn't go through the install of {Game.GameId}, the store stays open.");
            }

            return true;
        }

        /// <summary>Stops here because the store is waiting for a decision that only the person can make.</summary>
        protected void AskPerson(IntPtr window, string noticeKey, string store)
        {
            DirectInstallProgress.SetNotice(Game.Id, string.Format(ResourceProvider.GetString(noticeKey), store));
            StoreWindows.Show(window);
        }
    }

    /// <summary>Chooses how to install a game. Only for the add-ons Atrio knows, others can have their own way of doing it.</summary>
    public static class DirectInstall
    {
        private static readonly Guid EpicPluginId = Guid.Parse("00000002-DBD1-46C6-B5D0-B1BA559D10E4");
        private static readonly Guid AmazonPluginId = Guid.Parse("402674CD-4AF6-4886-B6EC-0E695BFA0688");
        private static readonly Guid XboxPluginId = Guid.Parse("7E4FBB5E-2AE3-48D4-8BA0-6B30E7A4E287");
        private static readonly Guid ItchPluginId = Guid.Parse("00000001-EBB2-4EEC-ABCB-7C89937A42BB");
        private static readonly Guid UbisoftPluginId = Guid.Parse("C2F038E5-8B92-4877-91F1-DA9094155FC5");
        private static readonly Guid EaPluginId = Guid.Parse("85DD7072-2F20-4E76-A007-41035E390724");
        private static readonly Guid GogPluginId = Guid.Parse("AEBE8B7C-6DC3-4A66-AF31-E7375C6B5E9E");

        /// <summary>The person agreed that Atrio accepts the terms of each game when a store asks for them.</summary>
        internal static bool AcceptsStoreTerms => GameUnifier.Current?.Settings.AcceptStoreTerms == true;

        public static InstallController Wrap(Game game, InstallController controller)
        {
            if (GameUnifier.Current?.Settings.DirectInstall == false)
            {
                return controller;
            }

            var addon = controller.GetType().Assembly.GetName().Name;
            if (game.PluginId == SteamContent.SteamPluginId && addon == "SteamLibrary" && int.TryParse(game.GameId, out _))
            {
                return new SteamDirectInstallController(game, controller);
            }

            if (game.PluginId == XboxPluginId && addon == "XboxLibrary" && XboxDirectInstallController.CanInstall(game))
            {
                return new XboxDirectInstallController(game, controller);
            }

            if (game.PluginId == EpicPluginId && addon == "EpicLibrary")
            {
                return new EpicDirectInstallController(game, controller);
            }

            if (game.PluginId == AmazonPluginId && addon == "AmazonGamesLibrary")
            {
                return new AmazonDirectInstallController(game, controller);
            }

            if (game.PluginId == ItchPluginId && addon == "ItchioLibrary" && int.TryParse(game.GameId, out _))
            {
                return new ItchDirectInstallController(game, controller);
            }

            if (game.PluginId == UbisoftPluginId && addon == "UplayLibrary")
            {
                return new UbisoftDirectInstallController(game, controller);
            }

            if (game.PluginId == EaPluginId && addon == "EaLibrary")
            {
                return new EaDirectInstallController(game, controller);
            }

            if (game.PluginId == GogPluginId && addon == "GogLibrary" && GogDirectInstallController.CanInstall(game))
            {
                return new GogDirectInstallController(game, controller);
            }

            return controller;
        }
    }
}
