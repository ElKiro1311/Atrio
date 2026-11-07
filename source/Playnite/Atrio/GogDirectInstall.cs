using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    /// <summary>
    /// What GOG offers to download for a game: the same offline installers of the page of the account.
    /// The API isn't official but GOG lets it be used: https://gogapidocs.readthedocs.io
    /// </summary>
    public static class GogDownloads
    {
        private const string WindowsSystem = "windows";
        private const string FallbackLanguage = "en";

        private static readonly Regex checksumRegex = new Regex(@"\smd5=""(?<sum>[0-9a-fA-F]{32})""", RegexOptions.Compiled);

        /// <summary>The installer for Windows in that language, or the one in English, or whatever there is.</summary>
        public static JToken PickInstaller(JToken downloads, string language)
        {
            var installers = (downloads?["installers"] as JArray)?.Where(a => a["os"]?.ToString() == WindowsSystem).ToList();
            if (installers == null || installers.Count == 0)
            {
                return null;
            }

            return installers.FirstOrDefault(a => a["language"]?.ToString() == language) ??
                   installers.FirstOrDefault(a => a["language"]?.ToString() == FallbackLanguage) ??
                   installers[0];
        }

        /// <summary>The MD5 GOG publishes of a file, null if the text doesn't have it.</summary>
        public static string ParseChecksum(string text)
        {
            var match = checksumRegex.Match(text ?? string.Empty);
            return match.Success ? match.Groups["sum"].Value.ToLowerInvariant() : null;
        }
    }

    /// <summary>
    /// Downloads the offline installer of a game of GOG and runs it, for those without GOG Galaxy, where
    /// the add-on can only open the web of the account. It goes with the session of the browser of Atrio,
    /// which is the one the add-on signs in with. The installer is the usual one: Windows asks for
    /// permission, and the installer itself shows its license unless the person allowed Atrio to accept them.
    /// </summary>
    public class GogDirectInstallController : DirectInstallController
    {
        private class Account
        {
            public bool isLoggedIn { get; set; }
            public string accessToken { get; set; }
        }

        private const string AccountUrl = "https://menu.gog.com/v1/account/basic";
        private const string ProductUrl = "https://api.gog.com/products/{0}?expand=downloads";
        private const string SilentArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOICONS";
        private const int OperationCancelledByPerson = 1223;
        private const int BufferSize = 1 << 20;

        private static readonly TimeSpan exitPause = TimeSpan.FromSeconds(2);
        private static readonly HttpClient client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        public GogDirectInstallController(Game game, InstallController store) : base(game, store)
        {
        }

        private static bool HasGalaxy =>
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\GOG.com\GalaxyClient\paths", "client", null) != null;

        /// <summary>Galaxy has its own way and the add-on knows it.</summary>
        public static bool CanInstall(Game game) => !HasGalaxy && long.TryParse(game.GameId, out _);

        /// <summary>Where the installers leave the game, null if it isn't there.</summary>
        private string FindInstalledFolder()
        {
            var key = $@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\GOG.com\Games\{Game.GameId}";
            var folder = Registry.GetValue(key, "path", null) as string;
            return !folder.IsNullOrEmpty() && Directory.Exists(folder) ? folder : null;
        }

        private static string ReadAccessToken()
        {
            using (var view = PlayniteApplication.Current.PlayniteApiGlobal.WebViews.CreateOffscreenView())
            {
                view.NavigateAndWait(AccountUrl);
                var account = JsonConvert.DeserializeObject<Account>(view.GetPageText());
                return account?.isLoggedIn == true ? account.accessToken : null;
            }
        }

        private async Task<string> GetTextAsync(string url, string accessToken)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using (var response = await client.SendAsync(request, Cancellation).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }

        private async Task<string> GetChecksumAsync(string url)
        {
            try
            {
                return GogDownloads.ParseChecksum(await client.GetStringAsync(url).ConfigureAwait(false));
            }
            catch (HttpRequestException e)
            {
                // Not all files have one, and HTTPS already protects the way here.
                Logger.Debug($"GOG has no checksum for a file of {Game.GameId}: {e.Message}");
                return null;
            }
        }

        private async Task DownloadAsync(string url, string path, string expectedChecksum, long doneBefore, long total)
        {
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, Cancellation).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true))
                using (var hash = MD5.Create())
                {
                    var buffer = new byte[BufferSize];
                    long written = 0;
                    var reported = -1;
                    int read;
                    while ((read = await source.ReadAsync(buffer, 0, buffer.Length, Cancellation).ConfigureAwait(false)) > 0)
                    {
                        hash.TransformBlock(buffer, 0, read, null, 0);
                        await target.WriteAsync(buffer, 0, read, Cancellation).ConfigureAwait(false);
                        written += read;
                        var percent = (int)Math.Min(99, (doneBefore + written) * 100 / Math.Max(1, total));
                        if (percent != reported)
                        {
                            reported = percent;
                            DirectInstallProgress.Set(Game.Id, percent);
                        }
                    }

                    hash.TransformFinalBlock(new byte[0], 0, 0);
                    var checksum = BitConverter.ToString(hash.Hash).Replace("-", string.Empty).ToLowerInvariant();
                    if (expectedChecksum != null && checksum != expectedChecksum)
                    {
                        throw new InvalidDataException($"The file of {Game.GameId} arrived damaged.");
                    }
                }
            }
        }

        /// <summary>The first file is the program, the rest are the data it takes from its side.</summary>
        private async Task<string> DownloadInstallerAsync(JToken installer, string accessToken, string folder)
        {
            var total = installer["total_size"]?.Value<long>() ?? 0;
            long done = 0;
            string program = null;
            foreach (var file in installer["files"])
            {
                var link = JObject.Parse(await GetTextAsync(file["downlink"].ToString(), accessToken).ConfigureAwait(false));
                var url = link["downlink"].ToString();
                var name = Path.GetFileName(new Uri(url).LocalPath);
                var path = Path.Combine(folder, name);
                var checksum = link["checksum"] == null ? null : await GetChecksumAsync(link["checksum"].ToString()).ConfigureAwait(false);
                await DownloadAsync(url, path, checksum, done, total).ConfigureAwait(false);
                done += file["size"]?.Value<long>() ?? 0;
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && program == null)
                {
                    program = path;
                }
            }

            return program;
        }

        /// <summary>False if the person said no to Windows.</summary>
        private async Task<bool> RunInstallerAsync(string program)
        {
            var silent = DirectInstall.AcceptsStoreTerms;
            var info = new ProcessStartInfo(program, silent ? SilentArguments : string.Empty)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(program)
            };

            if (!silent)
            {
                DirectInstallProgress.SetNotice(Game.Id, ResourceProvider.GetString("LOCAtrioFinishInstaller"));
            }

            try
            {
                using (var installer = Process.Start(info))
                {
                    while (!installer.HasExited)
                    {
                        await Task.Delay(exitPause, Cancellation).ConfigureAwait(false);
                    }
                }

                return true;
            }
            catch (Win32Exception e) when (e.NativeErrorCode == OperationCancelledByPerson)
            {
                return false;
            }
        }

        protected override async Task<bool> InstallDirectlyAsync(InstallActionArgs args)
        {
            var accessToken = await Task.Run(() => ReadAccessToken(), Cancellation).ConfigureAwait(false);
            if (accessToken.IsNullOrEmpty())
            {
                Logger.Info("No GOG session in the browser of Atrio to install directly.");
                return false;
            }

            var product = JObject.Parse(await GetTextAsync(string.Format(ProductUrl, Game.GameId), accessToken).ConfigureAwait(false));
            var installer = GogDownloads.PickInstaller(product["downloads"], CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
            if (installer == null)
            {
                Logger.Info($"GOG has no installer for Windows of {Game.GameId}.");
                return false;
            }

            var folder = Path.Combine(Path.GetTempPath(), "Atrio", "GOG", Game.GameId);
            Directory.CreateDirectory(folder);
            try
            {
                Logger.Info($"Downloading the installer of {Game.GameId} of GOG.");
                var program = await DownloadInstallerAsync(installer, accessToken, folder).ConfigureAwait(false);
                if (program == null)
                {
                    Logger.Warn($"The installer of {Game.GameId} of GOG has no program.");
                    return false;
                }

                if (!await RunInstallerAsync(program).ConfigureAwait(false))
                {
                    Abandon();
                    return true;
                }

                var installed = FindInstalledFolder();
                if (installed == null)
                {
                    // The person closed the installer before the end.
                    Abandon();
                    return true;
                }

                Finish(installed);
                return true;
            }
            finally
            {
                TryDelete(folder);
            }
        }

        private void TryDelete(string folder)
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Logger.Warn($"Couldn't remove the installer of {Game.GameId}: {e.Message}");
            }
        }
    }
}
