using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Atrio.OptiScaler
{
    /// <summary>
    /// What the bridge needs from OptiScaler Client for each function. It's checked when a new version comes, before Atrio
    /// starts using it: a function that lost something isn't offered, and the others stay as they were.
    /// </summary>
    internal static class Contract
    {
        private sealed class Function
        {
            public string Name;
            public bool Essential;
            public Func<IEnumerable<string>> Missing;
        }

        private const string Components = "ComponentManagementService";
        private const string Installer = "GameInstallationService";

        private static readonly string[] installValues = new[]
        {
            "game", "cachePath", "injectionDllName", "installFakenvapi", "fakenvapiCachePath", "installNukemFG", "nukemFGCachePath",
            "optiscalerVersion", "overrideGameDir", "profile", "isRdna4", "isRdna2", "installStreamline", "streamlineCachePath",
            "ensureFakenvapiIfMissing", "installDlssEnabler", "dlssEnablerCachePath", "installRenodx", "renodxAddonCachePath", "gpu",
            "dxgiSpoofing", "@Game"
        };

        private static readonly Function[] functions = new[]
        {
            new Function
            {
                Name = "state",
                Essential = true,
                Missing = () => Values("Game", "Name", "InstallPath", "IsOptiscalerInstalled", "OptiscalerVersion").
                    Concat(Methods("GameAnalyzerService", "AnalyzeGame", "game", "forceRefresh", "@Game")).
                    Concat(Methods(Installer, "DetermineInstallDirectory", "game", "@Game"))
            },
            new Function
            {
                Name = "install",
                Essential = true,
                Missing = () => Values(Components, "OptiScalerAvailableVersions", "LatestStableVersion").
                    Concat(Methods(Components, "DownloadOptiScalerAsync", "version", "progress")).
                    Concat(Methods(Installer, "InstallOptiScaler", installValues)).
                    Concat(Parameters(Installer, "InstallOptiScaler", "cachePath", "injectionDllName", "optiscalerVersion")).
                    Concat(Values("InstallationManifest", "OperationStatus", "InstalledGameDirectory")).
                    Concat(Methods("BackupStoreService", "LoadManifest", "gameDir"))
            },
            new Function
            {
                Name = "uninstall",
                Essential = true,
                Missing = () => Methods(Installer, "UninstallOptiScaler", "game", "@Game")
            },
            new Function
            {
                // The list of games of the client, where the settings of each one are.
                Name = "list",
                Missing = () => Methods("GamePersistenceService", "LoadGames").
                    Concat(Methods("GamePersistenceService", "SaveGames", "games")).
                    Concat(Values("GamePersistenceService", "_filePath"))
            },
            new Function
            {
                Name = "refresh",
                Missing = () => Methods(Components, "CheckForUpdatesAsync")
            },
            new Function
            {
                Name = "profile",
                Missing = () => Methods("ProfileManagementService", "GetAllProfiles", "forceRefresh").
                    Concat(Methods("ProfileManagementService", "GetProfileByName", "name")).
                    Concat(Parameters(Installer, "InstallOptiScaler", "profile")).
                    Concat(Methods(Installer, "ApplyProfileSettings", "game", "profile", "resolvedGameDir", "@Game", "@OptiScalerProfile"))
            },
            new Function
            {
                Name = "spoofing",
                Missing = () => Methods(Installer, "ApplySpoofingSettings", "game", "spoofingValue", "resolvedGameDir", "@Game").
                    Concat(Parameters(Installer, "ApplySpoofingSettings", "spoofingValue")).
                    Concat(Parameters(Installer, "InstallOptiScaler", "dxgiSpoofing"))
            },
            new Function
            {
                Name = "output",
                Missing = () => Values("Game", "OutputUpscalerSettings").
                    Concat(Values("GameOutputUpscalerSettings", "Backend")).
                    Concat(Types("OutputUpscalerBackend")).
                    Concat(Methods(Installer, "ApplyOutputUpscalerSettings", "game", "resolvedGameDir", "@Game"))
            },
            new Function
            {
                Name = "quality",
                Missing = () => Values("Game", "UpscalingQualitySettings").
                    Concat(Values("GameUpscalingQualitySettings", "Preset")).
                    Concat(Types("UpscalingQualityPreset")).
                    Concat(Methods(Installer, "ApplyUpscalingQualitySettings", "game", "resolvedGameDir", "@Game"))
            },
            new Function
            {
                Name = "frames",
                Missing = () => Values("Game", "FrameGenerationSettings").
                    Concat(Values("GameFrameGenerationSettings", "Route", "Output", "MultiFrameMode")).
                    Concat(Types("FrameGenerationRoute", "FrameGenerationOutput", "MultiFrameGenerationMode")).
                    Concat(Methods("FrameGenerationConfigurationService", "DetectCapabilities", "game", "gpu", "@Game", "@GpuInfo")).
                    Concat(Parameters(Installer, "InstallOptiScaler", "gpu", "installStreamline", "streamlineCachePath")).
                    Concat(Methods(Components, "DownloadStreamlineAsync", "version", "progress")).
                    Concat(Methods(Installer, "ApplyFrameGenerationSettings", "game", "resolvedGameDir", "gpu", "optiscalerVersion", "@Game", "@GpuInfo"))
            },
            new Function
            {
                Name = "fsr4",
                Missing = () => Values(Components, "ExtrasAvailableVersions").
                    Concat(Methods(Components, "DownloadExtrasDllAsync", "version", "progress")).
                    Concat(Methods(Components, "GetExtrasPackagedFileNamesAsync", "version", "progress")).
                    Concat(Methods(Components, "GetExtrasDllCachePath", "version")).
                    Concat(Methods("Fsr4Int8DllHelper", "BuildSwapCandidates", "gameDir", "cacheDir", "packagedFiles")).
                    Concat(Methods(Installer, "InjectExtrasDll", "game", "gameDir", "destPath", "sourceContentPath", "@Game"))
            },
            new Function
            {
                Name = "patcher",
                Missing = () => Values(Components, "OptiPatcherAvailableVersions").
                    Concat(Methods(Components, "DownloadOptiPatcherAsync", "version", "progress")).
                    Concat(Methods(Installer, "InstallAsiPlugin", "gameDir", "asiSourcePath"))
            },
            new Function
            {
                // What goes next to OptiScaler in some versions of it.
                Name = "pieces",
                Missing = () => Methods(Components, "DownloadLatestFakenvapiAsync", "progress").
                    Concat(Methods(Components, "DownloadFakenvapiAsync", "version", "progress")).
                    Concat(Methods(Components, "GetNukemFGCachePath", "version")).
                    Concat(Methods(Components, "DownloadDlssEnablerMirrorAsync", "version", "progress")).
                    Concat(Parameters(Installer, "InstallOptiScaler", "installFakenvapi", "fakenvapiCachePath", "installNukemFG", "nukemFGCachePath",
                        "installDlssEnabler", "dlssEnablerCachePath"))
            },
            new Function
            {
                Name = "compatibility",
                Missing = () => Methods("CompatibilityListService", "TryGetForGame", "gameName", "entry")
            },
            new Function
            {
                Name = "window",
                Missing = () => Values("MainWindow", "_games").
                    Concat(Methods("MainWindow", "BtnManage_Click", "sender", "e")).
                    Concat(Types("ManageGameWindow"))
            }
        };

        private static IEnumerable<string> Types(params string[] names)
        {
            return names.Where(a => Client.FindType(a) == null);
        }

        private static IEnumerable<string> Values(string type, params string[] names)
        {
            var found = Client.FindType(type);
            return found == null ? new[] { type } : names.Where(a => !Client.Has(found, a)).Select(a => $"{type}.{a}");
        }

        private static IEnumerable<string> Methods(string type, string method, params string[] known)
        {
            return Client.CanCall(type, method, known) ? Enumerable.Empty<string>() : new[] { $"{type}.{method}" };
        }

        private static IEnumerable<string> Parameters(string type, string method, params string[] names)
        {
            return names.Where(a => !Client.Takes(type, method, a)).Select(a => $"{type}.{method}({a})");
        }

        /// <summary>Where the client keeps its games, its settings and the copies of what it replaces.</summary>
        public static string DataFolder()
        {
            return Client.FindType("AppPaths") == null ? null : Client.Call(Client.Type("AppPaths"), "GetAppDataRoot")?.ToString();
        }

        /// <summary>A name for that folder that fits anywhere.</summary>
        public static string DataName()
        {
            var bytes = SHA1.HashData(Encoding.UTF8.GetBytes((DataFolder() ?? string.Empty).TrimEnd('\\', '/').ToLowerInvariant()));
            return Convert.ToHexString(bytes);
        }

        public static bool Has(string function)
        {
            var found = functions.FirstOrDefault(a => a.Name == function);
            return found != null && !found.Missing().Any();
        }

        public static JsonObject Describe()
        {
            var missing = functions.ToDictionary(a => a, a => a.Missing().Distinct().ToList());
            var details = new JsonObject();
            foreach (var function in missing.Where(a => a.Value.Count > 0))
            {
                details[function.Key.Name] = Service.Array(function.Value);
            }

            return new JsonObject
            {
                ["client"] = Client.Version,
                ["runtime"] = Environment.Version.ToString(),
                ["protocol"] = Service.Protocol,
                ["data"] = DataFolder(),
                ["functions"] = Service.Array(missing.Where(a => a.Value.Count == 0).Select(a => a.Key.Name)),
                ["missing"] = details,
                ["usable"] = missing.All(a => !a.Key.Essential || a.Value.Count == 0)
            };
        }
    }
}
