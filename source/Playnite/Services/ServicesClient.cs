using Playnite.Atrio;
using Playnite.SDK;
using System.Collections.Generic;

namespace Playnite.Services
{
    /// <summary>
    /// Keeps the interface of the backend client, but everything is answered by the public
    /// add-on database: Atrio does not use the servers of Playnite.
    /// </summary>
    public class ServicesClient : BaseServicesClient
    {
        public class RecommendedAddons
        {
            public Dictionary<string, string> Libraries { get; set; }
            public Dictionary<string, string> Generic { get; set; }
        }

        private static ILogger logger = LogManager.GetLogger();

        private readonly AddonCatalog catalog = new AddonCatalog();

        public ServicesClient() : this(string.Empty)
        {
        }

        public ServicesClient(string endpoint) : base(endpoint, Updater.CurrentVersion)
        {
        }

        public List<string> GetPatrons()
        {
            return new List<string>();
        }

        public List<AddonManifest> GetAllAddons(AddonType type, string searchTerm)
        {
            return catalog.Search(type, searchTerm);
        }

        public AddonManifest GetAddon(string addonId)
        {
            return catalog.GetAddon(addonId);
        }

        public AddonInstallerManifest GetAddonInstaller(string addonId)
        {
            return catalog.GetInstaller(addonId);
        }

        public string[] GetAddonBlacklist()
        {
            return catalog.GetBlocklist();
        }

        public RecommendedAddons GetDefaultExtensions()
        {
            // The store add-ons come with the app, there is nothing to recommend.
            return new RecommendedAddons();
        }
    }
}
