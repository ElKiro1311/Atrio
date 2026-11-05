using Playnite.Common;
using Playnite.Common.Web;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Playnite.Atrio
{
    /// <summary>
    /// The public add-on database, read straight from its repository so that Atrio doesn't depend on
    /// Playnite's servers. The whole database is one small archive: it is cached and refreshed twice a day.
    /// </summary>
    public class AddonCatalog
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly TimeSpan maxAge = TimeSpan.FromHours(12);
        private static readonly TimeSpan retryDelay = TimeSpan.FromMinutes(10);

        private readonly object sync = new object();
        private List<AddonManifest> addons;
        private DateTime lastRefreshAttempt = DateTime.MinValue;
        private string[] blocklist;
        private DateTime blocklistLoaded = DateTime.MinValue;

        private static string CachePath => Path.Combine(PlaynitePaths.DataCachePath, "addon-database.zip");

        private static bool IsCacheFresh()
        {
            return File.Exists(CachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) < maxAge;
        }

        private static void Refresh()
        {
            var temp = CachePath + ".tmp";
            try
            {
                FileSystem.PrepareSaveFile(temp);
                HttpDownloader.DownloadFile(PlayniteSettings.GetAppConfigValue("AddonDatabaseUrl"), temp);

                // A cut download must not replace a good copy.
                using (ZipFile.OpenRead(temp))
                {
                }

                File.Copy(temp, CachePath, true);
            }
            catch (Exception e) when (File.Exists(CachePath))
            {
                logger.Warn(e, "Failed to refresh the add-on database, using the cached copy.");
            }
            finally
            {
                FileSystem.DeleteFile(temp);
            }
        }

        /// <summary>The add-ons were written for the app Atrio is based on, the catalog talks about Atrio.</summary>
        private static void Present(AddonManifest addon)
        {
            addon.Name = Regex.Replace(addon.Name ?? string.Empty, @"\bPlaynite\b\s*", string.Empty).Trim();
            addon.ShortDescription = addon.ShortDescription?.Replace("Playnite", "Atrio");
            addon.Description = addon.Description?.Replace("Playnite", "Atrio");
            if (addon.Author == "Playnite")
            {
                addon.Author = "Josef Nemec";
            }
        }

        private static List<AddonManifest> Read()
        {
            var result = new List<AddonManifest>();
            using (var archive = ZipFile.OpenRead(CachePath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.IndexOf("/addons/", StringComparison.OrdinalIgnoreCase) < 0 ||
                        !entry.Name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
                    {
                        // One broken manifest must not hide the rest of the database.
                        if (Serialization.TryFromYaml<AddonManifest>(reader.ReadToEnd(), out var addon) &&
                            addon != null && !string.IsNullOrEmpty(addon.AddonId))
                        {
                            Present(addon);
                            result.Add(addon);
                        }
                    }
                }
            }

            return result;
        }

        public List<AddonManifest> GetAddons()
        {
            lock (sync)
            {
                var canRetry = DateTime.UtcNow - lastRefreshAttempt > retryDelay;
                if (!IsCacheFresh() && (canRetry || !File.Exists(CachePath)))
                {
                    lastRefreshAttempt = DateTime.UtcNow;
                    Refresh();
                    addons = null;
                }

                return addons ?? (addons = Read());
            }
        }

        public AddonManifest GetAddon(string addonId)
        {
            return GetAddons().FirstOrDefault(a => string.Equals(a.AddonId, addonId, StringComparison.OrdinalIgnoreCase));
        }

        private static bool Contains(string text, string term)
        {
            return text?.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public List<AddonManifest> Search(AddonType type, string term)
        {
            term = term?.Trim();
            return GetAddons().Where(a => a.Type == type && (string.IsNullOrEmpty(term) ||
                Contains(a.Name, term) || Contains(a.Author, term) || Contains(a.ShortDescription, term) ||
                a.Tags?.Any(tag => Contains(tag, term)) == true)).ToList();
        }

        public AddonInstallerManifest GetInstaller(string addonId)
        {
            var addon = GetAddon(addonId);
            if (addon == null)
            {
                throw new Exception($"Add-on {addonId} is not in the add-on database.");
            }

            // Some manifests are indented with tabs, which YAML doesn't allow.
            var text = HttpDownloader.DownloadString(addon.InstallerManifestUrl).TrimStart('﻿');
            text = Regex.Replace(text, @"^\t+", a => new string(' ', a.Length * 2), RegexOptions.Multiline);
            return Serialization.FromYaml<AddonInstallerManifest>(text);
        }

        /// <summary>Add-ons known to be harmful. A failed check must never get in the way of the app.</summary>
        public string[] GetBlocklist()
        {
            lock (sync)
            {
                if (blocklist != null && DateTime.UtcNow - blocklistLoaded < maxAge)
                {
                    return blocklist;
                }

                blocklistLoaded = DateTime.UtcNow;
                try
                {
                    blocklist = HttpDownloader.DownloadString(PlayniteSettings.GetAppConfigValue("AddonBlocklistUrl")).
                        Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).
                        Select(a => a.Trim()).
                        Where(a => a.Length > 0 && !a.StartsWith("#", StringComparison.Ordinal)).
                        ToArray();
                }
                catch (Exception e)
                {
                    logger.Warn(e, "Failed to download the add-on blocklist.");
                    blocklist = blocklist ?? new string[0];
                }

                return blocklist;
            }
        }
    }
}
