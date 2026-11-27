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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
        private const int Uninstalling = 2048;

        /// <summary>How much the game takes on the disk, as the client counted it.</summary>
        public long SizeOnDisk { get; set; }

        /// <summary>The client is deleting it.</summary>
        public bool IsUninstalling => (StateFlags & Uninstalling) != 0;

        private static readonly Regex valueRegex = new Regex(@"""(?<key>[^""]+)""\s+""(?<value>[^""]*)""", RegexOptions.Compiled);

        public int StateFlags { get; set; }
        public string InstallDir { get; set; }
        public long BytesToDownload { get; set; }
        public long BytesDownloaded { get; set; }
        public string AppId { get; set; }
        public string Name { get; set; }

        /// <summary>When the client plans to update it by itself, in its own clock (seconds since 1970). Zero when it doesn't say.</summary>
        public long ScheduledAutoUpdate { get; set; }

        /// <summary>Folder of the library that holds the game.</summary>
        public string Library { get; set; }

        public bool IsInstalled =>
            (StateFlags & FullyInstalled) != 0 &&
            (StateFlags & (UpdateRequired | UpdateRunning | UpdatePaused | UpdateStarted)) == 0;

        /// <summary>Installed, with an update of the client waiting for it, or under way.</summary>
        public bool NeedsUpdate =>
            (StateFlags & FullyInstalled) != 0 &&
            (StateFlags & (UpdateRequired | UpdateRunning | UpdatePaused | UpdateStarted)) != 0;

        /// <summary>The update is downloading right now.</summary>
        public bool IsUpdating => (StateFlags & (UpdateRunning | UpdateStarted)) != 0;

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
                else if (key.Equals("SizeOnDisk", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(value, out var bytes);
                    manifest.SizeOnDisk = bytes;
                }
                else if (key.Equals("appid", StringComparison.OrdinalIgnoreCase))
                {
                    manifest.AppId = value;
                }
                else if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    manifest.Name = value;
                }
                else if (key.Equals("ScheduledAutoUpdate", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(value, out var seconds);
                    manifest.ScheduledAutoUpdate = seconds;
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

        /// <summary>Every game the client has a file for, in every library. A file that can't be read now is left out.</summary>
        public static List<SteamAppManifest> ReadAllManifests()
        {
            var result = new List<SteamAppManifest>();
            foreach (var library in GetLibraries())
            {
                var folder = Path.Combine(library, "steamapps");
                string[] files;
                try
                {
                    files = Directory.Exists(folder) ? Directory.GetFiles(folder, "appmanifest_*.acf") : new string[0];
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var file in files)
                {
                    try
                    {
                        var manifest = SteamAppManifest.Parse(ReadShared(file));
                        manifest.Library = library;
                        if (manifest.AppId.IsNullOrEmpty())
                        {
                            manifest.AppId = Path.GetFileNameWithoutExtension(file).Substring("appmanifest_".Length);
                        }

                        result.Add(manifest);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                    }
                }
            }

            return result;
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

    /// <summary>What the list of the Steam client says about how one game is downloading. Null when it says nothing usable.</summary>
    public class SteamClientProgress
    {
        public long BytesDownloaded { get; set; }
        public long BytesToDownload { get; set; }
        public long Rate { get; set; }
        public bool Paused { get; set; }

        public int Percent => BytesToDownload <= 0 ? 0 : (int)Math.Min(100, BytesDownloaded * 100 / BytesToDownload);

        /// <summary>
        /// The numbers of the answer can arrive as words or as numbers, and any field can be missing:
        /// the client is not strict. Null if the game is not in the list or the answer is broken.
        /// </summary>
        public static SteamClientProgress Parse(string json, string appId)
        {
            JToken answer;
            try
            {
                answer = JToken.Parse(json ?? string.Empty);
            }
            catch (JsonReaderException)
            {
                return null;
            }

            if (appId.IsNullOrEmpty())
            {
                return null;
            }

            var response = (answer as JObject)?["response"] as JObject;
            var apps = response?["apps"] as JArray;
            if (apps == null)
            {
                return null;
            }

            foreach (var app in apps)
            {
                if (ReadWord(app["appid"]) != appId)
                {
                    continue;
                }

                return new SteamClientProgress
                {
                    BytesDownloaded = ReadNumber(app["bytes_downloaded"]),
                    BytesToDownload = ReadNumber(app["bytes_to_download"]),
                    Rate = ReadNumber(app["bytes_download_rate"]),
                    Paused = ReadPaused(app["download_paused"])
                };
            }

            return null;
        }

        private static long ReadNumber(JToken value)
        {
            return value?.Type == JTokenType.Integer ? (long)value :
                value?.Type == JTokenType.String && long.TryParse((string)value, out var number) ? number : 0;
        }

        private static string ReadWord(JToken value)
        {
            return value?.Type == JTokenType.String ? (string)value : value?.ToString();
        }

        private static bool ReadPaused(JToken value)
        {
            return value?.Type == JTokenType.Boolean ? (bool)value : bool.TryParse(value?.ToString(), out var paused) && paused;
        }
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
        private const string UninstallPath = "/IClientCommService/UninstallClientApp/v1/";
        // The whole list of fields: with the short one the client leaves out the byte counts.
        private const string AppListPath = "/IClientCommService/GetClientAppList/v1/?access_token={0}&client_instanceid={1}&fields=all&filters={2}&include_client_info=false&language=english";
        private const string ChangingFilter = "changing";
        private const string DownloadingFilter = "downloading";
        private const string SuccessResult = "1";
        private const int NetBiosNameLength = 15;

        private static readonly TimeSpan startPatience = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan runningPatience = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan sessionPause = TimeSpan.FromSeconds(3);
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static readonly Regex accountRegex = new Regex(@"""sub""\s*:\s*""(?<id>\d+)""", RegexOptions.Compiled);

        private static string ApiUrl => AtrioInfo.GetTestSetting("ATRIO_STEAM_API") ?? "https://api.steampowered.com";

        // The session of the last order given, to ask the progress without looking for the client again.
        private static string chosenToken;
        private static SteamClientSession chosenSession;

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
        public static Task<bool> RequestAsync(string appId, CancellationToken token)
        {
            return SendAsync(InstallPath, "install", appId, token);
        }

        /// <summary>The same order as the remote downloads have to delete a game. False if it couldn't be given.</summary>
        public static Task<bool> RequestUninstallAsync(string appId, CancellationToken token)
        {
            return SendAsync(UninstallPath, "uninstall", appId, token);
        }

        private static async Task<bool> SendAsync(string path, string what, string appId, CancellationToken token)
        {
            var accessToken = AtrioInfo.GetTestSetting("ATRIO_STEAM_TOKEN") ?? await SteamFriends.GetAccessTokenAsync(token).ConfigureAwait(false);
            if (accessToken.IsNullOrEmpty())
            {
                logger.Info($"No Steam session in the browser of Atrio to {what} directly.");
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

            using (var response = await client.PostAsync(ApiUrl + path, order, token).ConfigureAwait(false))
            {
                var result = response.Headers.TryGetValues("x-eresult", out var values) ? values.FirstOrDefault() : null;
                if (!response.IsSuccessStatusCode || (result != null && result != SuccessResult))
                {
                    logger.Warn($"Steam refused to {what} {appId}: {(int)response.StatusCode}, result {result}.");
                    return false;
                }

                logger.Info($"Steam client told to {what} {appId}.");
                chosenToken = accessToken;
                chosenSession = session;
                return true;
            }
        }

        /// <summary>
        /// How the client is downloading a game, from the list it keeps of its own downloads. It uses the
        /// token and the session of the last order given, so no search repeats. Null when there is no
        /// session, when the client says nothing about the game, or when the question fails; a null is
        /// never a reason to stop, the file of the game keeps guiding the watch.
        /// </summary>
        public static async Task<SteamClientProgress> GetProgressAsync(string appId, CancellationToken token, string accessToken = null, SteamClientSession session = null)
        {
            accessToken ??= chosenToken;
            session ??= chosenSession;
            if (accessToken.IsNullOrEmpty() || session?.client_instanceid.IsNullOrEmpty() != false)
            {
                return null;
            }

            // The client hides what it has just started under the other filter, the list says which one it keeps.
            foreach (var filter in new[] { ChangingFilter, DownloadingFilter })
            {
                var url = ApiUrl + string.Format(AppListPath, Uri.EscapeDataString(accessToken), Uri.EscapeDataString(session.client_instanceid), filter);
                try
                {
                    using (var response = await client.GetAsync(url, token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            logger.Debug($"Steam didn't tell the download progress: {(int)response.StatusCode}.");
                            return null;
                        }

                        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var progress = SteamClientProgress.Parse(content, appId);
                        if (progress != null)
                        {
                            return progress;
                        }
                    }
                }
                catch (Exception e) when (!(e is OperationCanceledException) || !token.IsCancellationRequested)
                {
                    // An answer that never comes is no trouble: the file of the game is still there.
                    logger.Debug(e, "Failed to read the download progress of Steam.");
                    return null;
                }
            }

            return null;
        }
    }

    /// <summary>Installs a game of Steam without opening Steam.</summary>
    public class SteamDirectInstallController : DirectInstallController
    {
        // How long the client gets to show that it took the order. It's usually a second or two.
        private static readonly TimeSpan startPatience = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan checkPause = TimeSpan.FromSeconds(2);
        // The list of the client is not asked faster than this, even if the watch goes by in less.
        private static readonly TimeSpan listPause = TimeSpan.FromSeconds(3);

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
            var nextList = DateTime.MinValue;
            // The last word of the client's list, kept between the short checks so the text of the
            // button doesn't blink between a poll and the next one; a failed poll clears it.
            var listed = (int?)null;
            var listedRate = 0L;
            var listedPaused = false;
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
                }
                else
                {
                    seen = true;
                    missing = 0;
                    if (manifest.IsInstalled)
                    {
                        Finish(manifest.InstallPath);
                        return true;
                    }
                }

                // The list of the client knows how the download goes even before the file of the game
                // exists, and its word wins while it lasts; the file only decides installed or gone.
                if (DateTime.Now >= nextList)
                {
                    nextList = DateTime.Now + listPause;
                    var progress = await SteamRemoteInstall.GetProgressAsync(Game.GameId, Cancellation);
                    if (progress != null)
                    {
                        // The last bit of percent only arrives with the manifest saying installed.
                        listed = Math.Min(progress.Percent, 99);
                        listedRate = progress.Rate;
                        listedPaused = progress.Paused;
                    }
                    else
                    {
                        listed = null;
                        listedRate = 0;
                        listedPaused = false;
                    }
                }

                // With nothing from the client, the file says what it can, still under a hundred.
                var percent = listed ?? (manifest == null ? (int?)null : Math.Min(manifest.Percent, 99));
                if (percent != null)
                {
                    DirectInstallProgress.Set(Game.Id, percent, listedRate, listedPaused);
                }
            }
        }
    }
}
