using Microsoft.Win32;
using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    /// <summary>What the Steam client says about a game in the file it keeps for each one.</summary>
    public class SteamAppManifest
    {
        private const int FullyInstalled = 4;
        private const int UpdateRequired = 2;
        private const int UpdateRunning = 256;
        private const int UpdatePaused = 512;
        private const int UpdateStarted = 1024;

        private static readonly Regex valueRegex = new Regex(@"""(?<key>[^""]+)""\s+""(?<value>[^""]*)""", RegexOptions.Compiled);

        public int StateFlags { get; set; }
        public string InstallDir { get; set; }
        public long BytesToDownload { get; set; }
        public long BytesDownloaded { get; set; }

        /// <summary>Folder of the library that holds the game.</summary>
        public string Library { get; set; }

        public bool IsInstalled =>
            (StateFlags & FullyInstalled) != 0 &&
            (StateFlags & (UpdateRequired | UpdateRunning | UpdatePaused | UpdateStarted)) == 0;

        public int Percent => BytesToDownload <= 0 ? 0 : (int)Math.Min(100, BytesDownloaded * 100 / BytesToDownload);

        public string InstallPath => InstallDir.IsNullOrEmpty() || Library.IsNullOrEmpty() ? null : Path.Combine(Library, "steamapps", "common", InstallDir);

        public static SteamAppManifest Parse(string text)
        {
            var manifest = new SteamAppManifest();
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in valueRegex.Matches(text ?? string.Empty))
            {
                // The blocks further down repeat some names, the ones of the game itself come first.
                var key = match.Groups["key"].Value;
                if (!found.Add(key))
                {
                    continue;
                }

                var value = match.Groups["value"].Value;
                if (key.Equals("StateFlags", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(value, out var flags);
                    manifest.StateFlags = flags;
                }
                else if (key.Equals("installdir", StringComparison.OrdinalIgnoreCase))
                {
                    manifest.InstallDir = value;
                }
                else if (key.Equals("BytesToDownload", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(value, out var bytes);
                    manifest.BytesToDownload = bytes;
                }
                else if (key.Equals("BytesDownloaded", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(value, out var bytes);
                    manifest.BytesDownloaded = bytes;
                }
            }

            return manifest;
        }
    }

    /// <summary>The files of the Steam client on this computer. They say nothing about the account.</summary>
    public static class SteamClientFiles
    {
        private static readonly Regex libraryRegex = new Regex(@"""path""\s+""(?<path>[^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string Folder => AtrioInfo.GetTestSetting("ATRIO_STEAM_FOLDER") ??
            (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string)?.Replace('/', '\\');

        public static string Executable => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", null) as string;

        /// <summary>Number of the account signed in to the client of this computer, zero if there is none.</summary>
        public static uint ActiveAccount => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null) is int account ? (uint)account : 0;

        public static List<string> ParseLibraryFolders(string text)
        {
            return libraryRegex.Matches(text ?? string.Empty).
                Cast<Match>().
                Select(a => a.Groups["path"].Value.Replace(@"\\", @"\")).
                ToList();
        }

        public static List<string> GetLibraries()
        {
            var folder = Folder;
            if (folder.IsNullOrEmpty())
            {
                return new List<string>();
            }

            var libraries = new List<string> { folder };
            var file = Path.Combine(folder, "steamapps", "libraryfolders.vdf");
            if (File.Exists(file))
            {
                libraries.AddRange(ParseLibraryFolders(ReadShared(file)));
            }

            return libraries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Null when the client has nothing about the game. Throws if it's there but it can't be read now.</summary>
        public static SteamAppManifest FindManifest(string appId)
        {
            foreach (var library in GetLibraries())
            {
                var file = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
                if (File.Exists(file))
                {
                    var manifest = SteamAppManifest.Parse(ReadShared(file));
                    manifest.Library = library;
                    return manifest;
                }
            }

            return null;
        }

        // The client keeps writing to these files.
        private static string ReadShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }
    }

    public class SteamClientSession
    {
        public string client_instanceid { get; set; }
        public string machine_name { get; set; }
    }

    /// <summary>
    /// Asks the Steam client of this computer to install a game: it's the order the Steam app for phones
    /// sends for its remote downloads. It goes with the session of the browser of Atrio, like the friends
    /// of a game, and the client has to be signed in to the same account.
    /// </summary>
    public static class SteamRemoteInstall
    {
        private class LogonResponse
        {
            public LogonInfo response { get; set; }
        }

        private class LogonInfo
        {
            public List<SteamClientSession> sessions { get; set; }
        }

        private const string LogonInfoPath = "/IClientCommService/GetAllClientLogonInfo/v1/?access_token={0}";
        private const string InstallPath = "/IClientCommService/InstallClientApp/v1/";
        private const string SuccessResult = "1";
        private const int NetBiosNameLength = 15;

        private static readonly TimeSpan startPatience = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan runningPatience = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan sessionPause = TimeSpan.FromSeconds(3);
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly Regex accountRegex = new Regex(@"""sub""\s*:\s*""(?<id>\d+)""", RegexOptions.Compiled);

        private static string ApiUrl => AtrioInfo.GetTestSetting("ATRIO_STEAM_API") ?? "https://api.steampowered.com";

        /// <summary>
        /// The session of the client of this computer. An account can be open in several of them, and a
        /// download that starts in another house is worse than one more window. A single session counts
        /// as this one only with the same account signed in here, in case Steam reports the name in another way.
        /// </summary>
        public static SteamClientSession PickSession(List<SteamClientSession> sessions, string machineName, bool accountSignedInHere)
        {
            if (!sessions.HasItems())
            {
                return null;
            }

            var named = sessions.FirstOrDefault(a => IsThisComputer(a.machine_name, machineName));
            if (named != null)
            {
                return named;
            }

            return sessions.Count == 1 && accountSignedInHere ? sessions[0] : null;
        }

        /// <summary>Number of the account of an access of Steam, which carries it inside. Null if it isn't there.</summary>
        public static uint? GetAccount(string accessToken)
        {
            var parts = accessToken?.Split('.');
            if (parts == null || parts.Length < 2)
            {
                return null;
            }

            try
            {
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
                var match = accountRegex.Match(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                return match.Success && ulong.TryParse(match.Groups["id"].Value, out var steamId) ? (uint)(steamId & uint.MaxValue) : (uint?)null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static bool IsThisComputer(string reported, string machineName)
        {
            if (reported.IsNullOrEmpty() || machineName.IsNullOrEmpty())
            {
                return false;
            }

            // Windows cuts the name it gives to programs, Steam can have the whole one.
            return reported.Equals(machineName, StringComparison.OrdinalIgnoreCase) ||
                   (machineName.Length == NetBiosNameLength && reported.StartsWith(machineName, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsClientRunning()
        {
            return AtrioInfo.GetTestSetting("ATRIO_STEAM_TOKEN") != null || Process.GetProcessesByName("steam").Any();
        }

        private static async Task<List<SteamClientSession>> GetSessionsAsync(string accessToken, CancellationToken token)
        {
            var url = ApiUrl + string.Format(LogonInfoPath, Uri.EscapeDataString(accessToken));
            using (var response = await client.GetAsync(url, token).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    logger.Warn($"Steam didn't list its clients: {(int)response.StatusCode}.");
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return Playnite.SDK.Data.Serialization.FromJson<LogonResponse>(content)?.response?.sessions ?? new List<SteamClientSession>();
            }
        }

        private static async Task<SteamClientSession> FindClientAsync(string accessToken, CancellationToken token)
        {
            var running = IsClientRunning();
            var started = DateTime.Now;
            var patience = running ? runningPatience : startPatience;
            if (!running)
            {
                var executable = SteamClientFiles.Executable;
                if (executable.IsNullOrEmpty() || !File.Exists(executable))
                {
                    return null;
                }

                ProcessStarter.StartProcess(executable, "-silent");
            }

            while (true)
            {
                var sessions = await GetSessionsAsync(accessToken, token).ConfigureAwait(false);
                if (sessions == null)
                {
                    return null;
                }

                var session = PickSession(sessions, Environment.MachineName, GetAccount(accessToken) == SteamClientFiles.ActiveAccount);
                if (session != null)
                {
                    return session;
                }

                if (DateTime.Now - started > patience)
                {
                    logger.Info($"No Steam client of this computer among {sessions.Count} sessions.");
                    return null;
                }

                await Task.Delay(sessionPause, token).ConfigureAwait(false);
            }
        }

        /// <summary>False if the order couldn't be given, which only means the client has to be asked in its window.</summary>
        public static async Task<bool> RequestAsync(string appId, CancellationToken token)
        {
            var accessToken = AtrioInfo.GetTestSetting("ATRIO_STEAM_TOKEN") ?? await SteamFriends.GetAccessTokenAsync(token).ConfigureAwait(false);
            if (accessToken.IsNullOrEmpty())
            {
                logger.Info("No Steam session in the browser of Atrio to install directly.");
                return false;
            }

            var session = await FindClientAsync(accessToken, token).ConfigureAwait(false);
            if (session == null)
            {
                return false;
            }

            var order = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "access_token", accessToken },
                { "appid", appId },
                { "client_instanceid", session.client_instanceid }
            });

            using (var response = await client.PostAsync(ApiUrl + InstallPath, order, token).ConfigureAwait(false))
            {
                var result = response.Headers.TryGetValues("x-eresult", out var values) ? values.FirstOrDefault() : null;
                if (!response.IsSuccessStatusCode || (result != null && result != SuccessResult))
                {
                    logger.Warn($"Steam refused to install {appId}: {(int)response.StatusCode}, result {result}.");
                    return false;
                }

                logger.Info($"Steam client told to install {appId}.");
                return true;
            }
        }
    }

    /// <summary>Installs a game of Steam without opening Steam.</summary>
    public class SteamDirectInstallController : DirectInstallController
    {
        // How long the client gets to show that it took the order. It's usually a second or two.
        private static readonly TimeSpan startPatience = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan checkPause = TimeSpan.FromSeconds(2);

        public SteamDirectInstallController(Game game, InstallController store) : base(game, store)
        {
        }

        protected override async Task<bool> InstallDirectlyAsync(InstallActionArgs args)
        {
            return await SteamRemoteInstall.RequestAsync(Game.GameId, Cancellation) && await WatchInstallAsync();
        }

        /// <summary>False if the client never showed that it took the order.</summary>
        private async Task<bool> WatchInstallAsync()
        {
            var ordered = DateTime.Now;
            var seen = false;
            var missing = 0;
            while (true)
            {
                await Task.Delay(checkPause, Cancellation);
                SteamAppManifest manifest;
                try
                {
                    manifest = await Task.Run(() => SteamClientFiles.FindManifest(Game.GameId), Cancellation);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    continue;
                }

                if (manifest == null)
                {
                    if (seen && ++missing > 1)
                    {
                        // Cancelled from Steam, the client removes everything about the game.
                        Abandon();
                        return true;
                    }

                    if (!seen && DateTime.Now - ordered > startPatience)
                    {
                        Logger.Warn($"The Steam client didn't start to install {Game.GameId}.");
                        return false;
                    }

                    continue;
                }

                seen = true;
                missing = 0;
                if (manifest.IsInstalled)
                {
                    Finish(manifest.InstallPath);
                    return true;
                }

                DirectInstallProgress.Set(Game.Id, manifest.Percent);
            }
        }
    }
}
