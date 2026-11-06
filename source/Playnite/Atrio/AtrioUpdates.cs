using Atrio.Updater;
using Newtonsoft.Json;
using Playnite.Common;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    public class AtrioRelease
    {
        public Version Version { get; set; }
        public string PageUrl { get; set; }
        public string Notes { get; set; }
        public string PackageUrl { get; set; }
        public long PackageSize { get; set; }

        /// <summary>The SHA-256 GitHub keeps of the package. Null if it doesn't give one.</summary>
        public string PackageHash { get; set; }
    }

    /// <summary>
    /// Versions come from the public releases of the project on GitHub, each one with the program in a zip. It works
    /// the same for anybody, with no account. Nothing of this stays in the folder of Atrio once the new version is running.
    /// </summary>
    public static class AtrioUpdates
    {
        private class GitHubAsset
        {
            [JsonProperty("url")]
            public string Url { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("size")]
            public long Size { get; set; }

            [JsonProperty("digest")]
            public string Digest { get; set; }
        }

        private class GitHubRelease
        {
            [JsonProperty("tag_name")]
            public string TagName { get; set; }

            [JsonProperty("html_url")]
            public string Url { get; set; }

            [JsonProperty("body")]
            public string Body { get; set; }

            [JsonProperty("draft")]
            public bool Draft { get; set; }

            [JsonProperty("prerelease")]
            public bool Prerelease { get; set; }

            [JsonProperty("assets")]
            public List<GitHubAsset> Assets { get; set; }
        }

        private const string UpdateFolderName = "update";
        private const string PackageFolderName = "p";
        private const string BackupFolderName = "a";
        private const string DownloadName = "paquete.zip";
        private const string PlanName = "plan.txt";
        private const string MarkerName = "instalada.txt";
        private const string InstallerName = "Atrio.Updater.exe";
        private const string HashPrefix = "sha256:";
        private const int BufferSize = 81920;

        /// <summary>How long a download can go without receiving anything.</summary>
        private static readonly TimeSpan stallTimeout = TimeSpan.FromSeconds(60);

        private static readonly ILogger logger = LogManager.GetLogger();

        private static string ReleasesUrl => AtrioInfo.GetTestSetting("ATRIO_RELEASES_URL") ?? PlayniteSettings.GetAppConfigValue("AtrioReleasesUrl");
        private static string UpdateFolder => Path.Combine(PlaynitePaths.ProgramPath, UpdateFolderName);

        /// <summary>The versions published with their package, the newest first.</summary>
        public static List<AtrioRelease> ParseReleases(string json)
        {
            var releases = new List<AtrioRelease>();
            foreach (var release in JsonConvert.DeserializeObject<List<GitHubRelease>>(json) ?? new List<GitHubRelease>())
            {
                var package = release.Assets?.FirstOrDefault(a => a.Name?.StartsWith("Atrio-", StringComparison.OrdinalIgnoreCase) == true &&
                                                                  a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                if (!release.Draft && !release.Prerelease && package != null &&
                    Version.TryParse(release.TagName?.TrimStart('v', 'V'), out var version))
                {
                    releases.Add(new AtrioRelease
                    {
                        Version = version,
                        PageUrl = release.Url,
                        Notes = release.Body,
                        PackageUrl = package.Url,
                        PackageSize = package.Size,
                        PackageHash = package.Digest?.StartsWith(HashPrefix, StringComparison.OrdinalIgnoreCase) == true ?
                            package.Digest.Substring(HashPrefix.Length) : null
                    });
                }
            }

            return releases.OrderByDescending(a => a.Version).ToList();
        }

        public static List<AtrioRelease> GetNewerReleases(IEnumerable<AtrioRelease> releases, Version current)
        {
            return releases.Where(a => a.Version > current).ToList();
        }

        private static HttpClient CreateClient(HttpMessageHandler handler)
        {
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"Atrio/{AtrioInfo.Version}");
            return client;
        }

        private static Task<HttpResponseMessage> AskGitHub(HttpClient client, string url, string accept, CancellationToken token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd(accept);
            return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        }

        /// <summary>The same question for anybody: no account or login of the person using Atrio takes part in this.</summary>
        public static List<AtrioRelease> GetReleases()
        {
            using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            using (var handler = new HttpClientHandler())
            using (var client = CreateClient(handler))
            using (var response = AskGitHub(client, ReleasesUrl, "application/vnd.github+json", cancel.Token).GetAwaiter().GetResult())
            {
                logger.Info($"GitHub answered {(int)response.StatusCode} to the question about releases.");
                if (!response.IsSuccessStatusCode)
                {
                    return new List<AtrioRelease>();
                }

                return ParseReleases(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            }
        }

        public static AtrioRelease GetNewerRelease()
        {
            return GetNewerReleases(GetReleases(), AtrioInfo.CurrentVersion).FirstOrDefault();
        }

        private static string ToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static Task<Stream> GetContent(HttpResponseMessage response)
        {
            response.EnsureSuccessStatusCode();
            return response.Content.ReadAsStreamAsync();
        }

        private static async Task Download(AtrioRelease release, string path, Action<int> progress, CancellationToken token)
        {
            using (var handler = new HttpClientHandler())
            using (var client = CreateClient(handler))
            using (var hash = SHA256.Create())
            using (var stalled = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                stalled.CancelAfter(stallTimeout);
                try
                {
                    using (var response = await AskGitHub(client, release.PackageUrl, "application/octet-stream", stalled.Token).ConfigureAwait(false))

                    // A read that waits for the network doesn't listen to the token: closing the answer is what stops it.
                    using (stalled.Token.Register(response.Dispose))
                    using (var source = await GetContent(response).ConfigureAwait(false))
                    using (var file = File.Create(path))
                    {
                        var total = response.Content.Headers.ContentLength ?? release.PackageSize;
                        var buffer = new byte[BufferSize];
                        long received = 0;
                        var shown = -1;
                        while (true)
                        {
                            stalled.CancelAfter(stallTimeout);
                            var read = await source.ReadAsync(buffer, 0, buffer.Length, stalled.Token).ConfigureAwait(false);
                            if (read == 0)
                            {
                                break;
                            }

                            hash.TransformBlock(buffer, 0, read, null, 0);
                            await file.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                            received += read;
                            var percent = total > 0 ? (int)(received * 100 / total) : 0;
                            if (percent != shown)
                            {
                                shown = percent;
                                progress(percent);
                            }
                        }

                        if (total > 0 && received != total)
                        {
                            throw new InvalidDataException($"The package arrived incomplete: {received} of {total} bytes.");
                        }
                    }
                }
                catch (Exception e) when (stalled.IsCancellationRequested && !(e is OutOfMemoryException))
                {
                    token.ThrowIfCancellationRequested();
                    throw new TimeoutException("The download stopped.", e);
                }

                hash.TransformFinalBlock(new byte[0], 0, 0);
                var found = ToHex(hash.Hash);
                if (release.PackageHash != null && !found.Equals(release.PackageHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"The package is not the one published: {found} instead of {release.PackageHash}.");
                }
            }
        }

        /// <summary>Unpacks a version, checking that nothing in it points out of the folder.</summary>
        public static void Unpack(string zipPath, string folder, CancellationToken token)
        {
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    var name = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    var destination = Path.GetFullPath(Path.Combine(root, name));
                    if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"The package has a file out of its place: {entry.FullName}");
                    }

                    if (name.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(destination);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination));
                        entry.ExtractToFile(destination, true);
                    }
                }
            }
        }

        /// <summary>Everything that can be done with Atrio open: the new version ends up unpacked, waiting to be put in place.</summary>
        public static async Task Prepare(AtrioRelease release, Action<int> progress, CancellationToken token)
        {
            if (!FileSystem.CanWriteToFolder(PlaynitePaths.ProgramPath))
            {
                throw new UnauthorizedAccessException(string.Format(ResourceProvider.GetString("LOCAtrioUpdateReadOnly"), PlaynitePaths.ProgramPath));
            }

            var folder = UpdateFolder;
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }

            try
            {
                Directory.CreateDirectory(folder);
                var zip = Path.Combine(folder, DownloadName);
                await Download(release, zip, progress, token).ConfigureAwait(false);
                await Task.Run(() => Unpack(zip, Path.Combine(folder, PackageFolderName), token), token).ConfigureAwait(false);
                File.Delete(zip);

                // It runs from out of the package, whose files are about to move.
                var installer = Path.Combine(folder, PackageFolderName, InstallerName);
                if (!File.Exists(installer) || !File.Exists(Path.Combine(folder, PackageFolderName, Path.GetFileName(PlaynitePaths.DesktopExecutablePath))))
                {
                    throw new InvalidDataException("The package doesn't have the program.");
                }

                File.Copy(installer, Path.Combine(folder, InstallerName), true);
            }
            catch
            {
                TryDeleteUpdateFolder();
                throw;
            }
        }

        /// <summary>Atrio has to close right after this, which is what lets the installer do its work.</summary>
        public static string CreatePlan(AtrioRelease release, string startPath, string startArguments)
        {
            var folder = UpdateFolder;
            var path = Path.Combine(folder, PlanName);
            using (var process = Process.GetCurrentProcess())
            {
                new UpdatePlan
                {
                    Target = PlaynitePaths.ProgramPath,
                    Package = Path.Combine(folder, PackageFolderName),
                    Backup = Path.Combine(folder, BackupFolderName),
                    Marker = Path.Combine(folder, MarkerName),
                    WaitFor = process.Id,
                    Start = startPath,
                    StartArguments = startArguments,
                    Version = release.Version.ToString(),
                    WaitingText = ResourceProvider.GetString("LOCAtrioUpdaterWaiting"),
                    InstallingText = ResourceProvider.GetString("LOCAtrioUpdaterInstalling"),
                    FailedText = ResourceProvider.GetString("LOCAtrioUpdaterFailed"),
                    StillOpenText = ResourceProvider.GetString("LOCAtrioUpdaterStillOpen")
                }.Save(path);
            }

            return path;
        }

        public static string InstallerPath => Path.Combine(UpdateFolder, InstallerName);

        private static void TryDeleteUpdateFolder()
        {
            try
            {
                if (Directory.Exists(UpdateFolder))
                {
                    Directory.Delete(UpdateFolder, true);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                logger.Warn(e, "Failed to remove the folder of the last update.");
            }
        }

        /// <summary>Cleans up after an update. Returns the version it installed, if that is what happened since the last start.</summary>
        public static string FinishUpdate()
        {
            var marker = Path.Combine(UpdateFolder, MarkerName);
            var version = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
            TryDeleteUpdateFolder();
            return version;
        }
    }
}
