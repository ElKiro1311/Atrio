using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    /// <summary>A version of OptiScaler Client published in its page on GitHub.</summary>
    public class OptiScalerRelease
    {
        public string Version { get; set; }
        public string Url { get; set; }

        /// <summary>The SHA-256 GitHub gives for the file, without which it isn't downloaded.</summary>
        public string Hash { get; set; }
        public long Size { get; set; }
        public string PageUrl { get; set; }
        public DateTime Published { get; set; }
    }

    /// <summary>What one copy of the client can do for Atrio, as the bridge found it.</summary>
    public class OptiScalerContract
    {
        /// <summary>Size and date of the program it belongs to: another program is asked again.</summary>
        public string Stamp { get; set; }
        public bool Usable { get; set; }
        public List<string> Functions { get; set; } = new List<string>();
        public Dictionary<string, List<string>> Missing { get; set; } = new Dictionary<string, List<string>>();

        /// <summary>When it was asked, and until when what was found counts: an antivirus or a slow disk aren't forever.</summary>
        public DateTime Date { get; set; }
        public DateTime? Until { get; set; }

        /// <summary>Why it couldn't even be asked, with the code of the problem.</summary>
        public string Problem { get; set; }

        [JsonIgnore]
        public bool IsOver => Until != null && DateTime.UtcNow > Until;
    }

    /// <summary>A copy of the client on this computer, in the folder of Atrio.</summary>
    public class OptiScalerCopy
    {
        public Version Number { get; set; }
        public string Executable { get; set; }
        public string Folder => Path.GetDirectoryName(Executable);

        /// <summary>The number as the project writes it: 1.0.8, or 1.0.7.2 when there's a fourth one.</summary>
        public string Version => OptiScalerClient.FormatVersion(Number);
    }

    /// <summary>
    /// OptiScaler Client (GPL-3.0), the program that puts OptiScaler into games. It isn't part of Atrio: it's downloaded
    /// from its own page on GitHub when the person asks for it, each version in its folder, and used as it comes. A new
    /// version is tried before it takes the place of the one in use, so that downloading it never breaks what works.
    /// </summary>
    public static class OptiScalerClient
    {
        private class State
        {
            public Dictionary<string, OptiScalerContract> Contracts { get; set; } = new Dictionary<string, OptiScalerContract>();
            public List<OptiScalerRelease> Releases { get; set; } = new List<OptiScalerRelease>();
            public DateTime ReleasesDate { get; set; }

            /// <summary>The hashes of every published Windows package, to recognize one that was put there by hand.</summary>
            public Dictionary<string, string> KnownHashes { get; set; } = new Dictionary<string, string>();

            /// <summary>Packages already looked at, by path, with their size and date.</summary>
            public Dictionary<string, string> Packages { get; set; } = new Dictionary<string, string>();
        }

        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly HttpClient client = CreateClient();
        private static readonly SemaphoreSlim working = new SemaphoreSlim(1, 1);
        private static readonly object stateLock = new object();
        private static readonly Regex versionRegex = new Regex(@"(?<number>\d+\.\d+(?:\.\d+){0,2})(?<letter>[a-z](?![a-z0-9]))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex testVersionRegex = new Regex(@"[-_. ](pre|preview|alpha|beta|rc|dev|nightly)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex packageRegex = new Regex(@"(win(dows)?-x64|portable).*\.zip$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex downloadedRegex = new Regex(@"^optiscaler[-_. ]?client.*\.zip$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private const string ReleasesUrl = "https://api.github.com/repos/Optiscaler-Client/Optiscaler-Client/releases?per_page=30";
        private const string StateFile = "atrio-optiscaler.json";
        private const string OwnMark = "atrio-descargado.txt";
        private const string TrialPrefix = ".prueba-";
        public const string ProjectUrl = "https://github.com/Optiscaler-Client/Optiscaler-Client";

        /// <summary>The games OptiScaler is known to work with, in its wiki.</summary>
        public const string CompatibilityUrl = "https://github.com/optiscaler/OptiScaler/wiki/Compatibility-List";
        public const string ProgramFile = "OptiscalerClient.exe";
        public const string BridgeFile = "Atrio.OptiScaler.dll";

        /// <summary>GitHub is asked for new versions this often, and whenever the person wants.</summary>
        public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(20);

        private static State state;

        /// <summary>Tests point it somewhere else.</summary>
        public static string TestRoot { get; set; }

        /// <summary>Where the browser leaves what it downloads, in a test.</summary>
        public static string TestDownloads { get; set; }

        public static string Root => Path.Combine(TestRoot ?? PlaynitePaths.ConfigRootPath, "Herramientas", "OptiScaler Client");

        /// <summary>Only 64-bit games and a 64-bit Windows: the client is a 64-bit program.</summary>
        public static bool IsSupported => Environment.Is64BitOperatingSystem;

        /// <summary>The bridge comes with Atrio, next to its other files.</summary>
        public static string BridgePath => Path.Combine(PlaynitePaths.ProgramPath, "Resources", "OptiScaler", BridgeFile);

        private static HttpClient CreateClient()
        {
            var result = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            result.DefaultRequestHeaders.UserAgent.ParseAdd("Atrio");
            return result;
        }

        /// <summary>
        /// Where the client keeps its games, its settings and the backups of what it replaces. A person who already used it
        /// keeps one set of backups for both; otherwise it stays inside the folder of Atrio. Null leaves its usual place.
        /// </summary>
        public static string DataFolder
        {
            get
            {
                // A test never touches the games and the backups the person has in the client.
                if (TestRoot == null && !AtrioInfo.IsTestRun)
                {
                    var usual = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OptiscalerClient");
                    if (File.Exists(Path.Combine(usual, "config.json")) && !File.Exists(Path.Combine(OwnDataFolder, "config.json")))
                    {
                        return null;
                    }
                }

                return OwnDataFolder;
            }
        }

        private static string OwnDataFolder => Path.Combine(Root, "Datos");

        /// <summary>The folder of the data, whichever it is.</summary>
        public static string CurrentDataFolder =>
            DataFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OptiscalerClient");

        /// <summary>
        /// The list of compatible games, opened at the row of one: it's a long page without sections, and the browser goes
        /// to the text that comes after the address. A game that isn't in it leaves it at the top.
        /// </summary>
        /// <param name="page">Where OptiScaler Client says it is, when it says so.</param>
        public static string GetCompatibilityUrl(string page, string game)
        {
            var url = LinkBrowsers.IsWebAddress(page) ? page : CompatibilityUrl;
            if (game.IsNullOrWhiteSpace())
            {
                return url;
            }

            // A hyphen means something there, as a comma and an ampersand do.
            var text = Uri.EscapeDataString(game.Trim()).Replace("-", "%2D");
            return $"{url}{(url.Contains("#") ? string.Empty : "#")}:~:text={text}";
        }

        public static string FormatVersion(Version version)
        {
            if (version == null)
            {
                return null;
            }

            return version.Revision > 0 ? version.ToString(4) : version.Build >= 0 ? version.ToString(3) : version.ToString(2);
        }

        /// <summary>The number in a tag or in the name of a package. Null for a test version (1.0.9-pre) or without a number.</summary>
        public static Version ParseVersion(string text)
        {
            var match = versionRegex.Match(text ?? string.Empty);
            if (!match.Success || testVersionRegex.IsMatch(text) || !Version.TryParse(match.Groups["number"].Value, out var version))
            {
                return null;
            }

            // 1.0.2b is the one after the 1.0.2.
            var result = Normalize(version);
            return match.Groups["letter"].Success && result.Revision == 0 ?
                new Version(result.Major, result.Minor, result.Build, char.ToLowerInvariant(match.Groups["letter"].Value[0]) - 'a' + 1) : result;
        }

        private static Version Normalize(Version version)
        {
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        }

        private static State LoadState()
        {
            lock (stateLock)
            {
                if (state != null)
                {
                    return state;
                }

                try
                {
                    var path = Path.Combine(Root, StateFile);
                    state = File.Exists(path) ? JsonConvert.DeserializeObject<State>(File.ReadAllText(path)) : null;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException)
                {
                    logger.Warn(e, "Failed to read what is known of OptiScaler Client.");
                }

                state = state ?? new State();
                state.Contracts = state.Contracts ?? new Dictionary<string, OptiScalerContract>();
                state.Releases = state.Releases ?? new List<OptiScalerRelease>();
                state.KnownHashes = state.KnownHashes ?? new Dictionary<string, string>();
                state.Packages = state.Packages ?? new Dictionary<string, string>();
                return state;
            }
        }

        private static void SaveState()
        {
            lock (stateLock)
            {
                try
                {
                    Directory.CreateDirectory(Root);
                    File.WriteAllText(Path.Combine(Root, StateFile), JsonConvert.SerializeObject(state, Formatting.Indented));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    logger.Warn(e, "Failed to save what is known of OptiScaler Client.");
                }
            }
        }

        /// <summary>For the tests.</summary>
        public static void ForgetState()
        {
            lock (stateLock)
            {
                state = null;
            }
        }

        private static Tuple<string, DateTime, Dictionary<string, string>> recorded;

        /// <summary>
        /// The version of OptiScaler that the client wrote down for a game the last time, from its list of games. Null
        /// without it. Only to show it: what is really in the folder is looked at when the panel opens.
        /// </summary>
        public static string GetRecordedVersion(string gameFolder)
        {
            var path = Path.Combine(CurrentDataFolder, "games.json");
            if (gameFolder.IsNullOrEmpty() || !File.Exists(path))
            {
                return null;
            }

            try
            {
                var date = File.GetLastWriteTimeUtc(path);
                var known = recorded;
                if (known == null || known.Item1 != path || known.Item2 != date)
                {
                    known = Tuple.Create(path, date, ParseRecords(File.ReadAllText(path)));
                    recorded = known;
                }

                return known.Item3.TryGetValue(NormalizeFolder(gameFolder), out var version) ? version : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException)
            {
                logger.Debug(e, "Failed to read the games of OptiScaler Client.");
                return null;
            }
        }

        private static string NormalizeFolder(string folder)
        {
            return folder.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        }

        /// <summary>The folders of the games with OptiScaler, and its version in each one.</summary>
        public static Dictionary<string, string> ParseRecords(string json)
        {
            var result = new Dictionary<string, string>();
            foreach (var game in JArray.Parse(json).OfType<JObject>())
            {
                var folder = (string)game["InstallPath"];
                if (!folder.IsNullOrEmpty() && (bool?)game["IsOptiscalerInstalled"] == true)
                {
                    result[NormalizeFolder(folder)] = (string)game["OptiscalerVersion"] ?? "?";
                }
            }

            return result;
        }

        /// <summary>The copies there are, the newest first: one per folder, downloaded by Atrio or put there by the person.</summary>
        public static List<OptiScalerCopy> GetCopies()
        {
            var result = new List<OptiScalerCopy>();
            if (!Directory.Exists(Root))
            {
                return result;
            }

            try
            {
                // The program itself in the folder, as it comes out of a package, is a copy too.
                foreach (var folder in new[] { Root }.Concat(Directory.GetDirectories(Root)))
                {
                    var program = Path.Combine(folder, ProgramFile);
                    if (!Path.GetFileName(folder).StartsWith(".", StringComparison.Ordinal) && File.Exists(program))
                    {
                        // The folder has the published number, which some versions don't write in their program.
                        var number = (folder == Root ? null : ParseVersion(Path.GetFileName(folder))) ?? ReadVersion(program);
                        if (number != null)
                        {
                            result.Add(new OptiScalerCopy { Number = number, Executable = program });
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                logger.Warn(e, "Failed to look for OptiScaler Client.");
            }

            return result.OrderByDescending(a => a.Number).ToList();
        }

        private static Version ReadVersion(string program)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(program);
                return Version.TryParse(info.FileVersion ?? string.Empty, out var version) ? Normalize(version) : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string GetStamp(string file)
        {
            var info = new FileInfo(file);
            return $"{info.Length}-{info.LastWriteTimeUtc.Ticks}-{GetBridgeStamp()}";
        }

        private static Tuple<string, DateTime, string> bridgeStamp;

        /// <summary>What the bridge is, not how long: another one asks every copy again.</summary>
        private static string GetBridgeStamp()
        {
            if (!File.Exists(BridgePath))
            {
                return "0";
            }

            var date = File.GetLastWriteTimeUtc(BridgePath);
            var known = bridgeStamp;
            if (known == null || known.Item1 != BridgePath || known.Item2 != date)
            {
                known = Tuple.Create(BridgePath, date, Hash(BridgePath).Substring(0, 16));
                bridgeStamp = known;
            }

            return known.Item3;
        }

        /// <summary>What is known of a copy, null until it's tried.</summary>
        public static OptiScalerContract GetContract(OptiScalerCopy copy)
        {
            if (copy == null || !File.Exists(copy.Executable))
            {
                return null;
            }

            return LoadState().Contracts.TryGetValue(copy.Executable, out var contract) && !contract.IsOver && contract.Stamp == GetStamp(copy.Executable) ? contract : null;
        }

        /// <summary>The copies that couldn't be used are asked again: the person wants to try.</summary>
        public static void ForgetProblems()
        {
            lock (stateLock)
            {
                var contracts = LoadState().Contracts;
                foreach (var key in contracts.Where(a => !a.Value.Usable).Select(a => a.Key).ToList())
                {
                    contracts.Remove(key);
                }
            }

            SaveState();
        }

        /// <summary>The copy Atrio works with: the newest one known to understand it.</summary>
        public static OptiScalerCopy GetCurrent()
        {
            return Choose(GetCopies(), GetContract);
        }

        /// <summary>
        /// The newest copy that does everything the older ones do: what worked doesn't go away with a new version. One never
        /// tried goes after the ones that work. Without any, the newest one: its window can always be opened.
        /// </summary>
        public static OptiScalerCopy Choose(IEnumerable<OptiScalerCopy> copies, Func<OptiScalerCopy, OptiScalerContract> contract)
        {
            var all = copies.OrderByDescending(a => a.Number).ToList();
            var usable = all.Where(a => contract(a)?.Usable == true).ToList();
            return usable.FirstOrDefault(a => usable.Where(b => b.Number < a.Number).All(b => contract(b).Functions.All(c => contract(a).Functions.Contains(c)))) ??
                all.FirstOrDefault(a => contract(a) == null) ??
                all.FirstOrDefault();
        }

        /// <summary>The copy ready to work, trying the ones that haven't been tried. Null when there's none.</summary>
        public static async Task<OptiScalerCopy> PrepareAsync(CancellationToken token)
        {
            RemoveTrials();
            await ImportPackagesAsync(token);

            // The newest one that works, and the ones before it until one has everything, to know that nothing is lost.
            var usable = 0;
            foreach (var copy in GetCopies())
            {
                var contract = GetContract(copy) ?? await TryCopyAsync(copy, token);
                usable += contract?.Usable == true ? 1 : 0;
                if (usable >= 2 && contract?.Usable == true && contract.Missing.Count == 0)
                {
                    break;
                }
            }

            return GetCurrent();
        }

        /// <summary>What was left of trying a copy, when Atrio closed right after.</summary>
        private static void RemoveTrials()
        {
            try
            {
                foreach (var folder in Directory.Exists(Root) ? Directory.GetDirectories(Root, TrialPrefix + "*") : new string[0])
                {
                    if (DateTime.UtcNow - Directory.GetCreationTimeUtc(folder) > TimeSpan.FromMinutes(5))
                    {
                        TryDelete(folder);
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                logger.Debug(e, "Failed to clean the folder of OptiScaler Client.");
            }
        }

        /// <summary>A newer copy that is there and isn't used. Null when the one in use is the newest.</summary>
        public static OptiScalerCopy GetUnused()
        {
            var current = GetCurrent();
            return current == null ? null : GetCopies().FirstOrDefault(a => a.Number > current.Number && GetContract(a) != null);
        }

        /// <summary>Starts a copy without a window and asks it what it can do. Null if it couldn't be asked.</summary>
        public static async Task<OptiScalerContract> TryCopyAsync(OptiScalerCopy copy, CancellationToken token)
        {
            // Not on the games and the copies of the person: a version that isn't going to be used must not change them.
            var trial = Path.Combine(Root, $"{TrialPrefix}{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(trial);
                using (var bridge = await OptiScalerBridge.StartAsync(copy.Executable, token, trial))
                {
                    var answer = await bridge.AskAsync("hello", null, TimeSpan.FromSeconds(30), null, token);
                    var contract = ParseContract(answer);
                    contract.Stamp = GetStamp(copy.Executable);
                    contract.Date = DateTime.UtcNow;
                    if (contract.Usable && !string.Equals(((string)answer["data"])?.TrimEnd('\\'), trial, StringComparison.OrdinalIgnoreCase))
                    {
                        // It would look for the copies of the originals in another place.
                        contract.Usable = false;
                        contract.Missing["data"] = new List<string> { (string)answer["data"] ?? "-" };
                    }

                    if (contract.Usable)
                    {
                        await CheckWorksAsync(bridge, contract, token);
                    }

                    Remember(copy, contract);
                    if (!contract.Usable)
                    {
                        logger.Warn($"OptiScaler Client {copy.Version} lacks something Atrio needs: {string.Join("; ", contract.Missing.Select(a => a.Key + " (" + string.Join(", ", a.Value) + ")"))}");
                    }

                    return contract;
                }
            }
            catch (OptiScalerException e) when (e.Code == OptiScalerException.NoAnswer)
            {
                // The program started and the bridge didn't answer: in this version it can't be loaded, or the computer was
                // too busy with something else.
                logger.Warn(e, $"OptiScaler Client {copy.Version} doesn't load the bridge.");
                return RememberProblem(copy, e.Code, TimeSpan.FromDays(1));
            }
            catch (Exception e) when (!(e is OperationCanceledException) && !(e is OutOfMemoryException))
            {
                logger.Error(e, $"Failed to try OptiScaler Client {copy.Version}.");
                return RememberProblem(copy, (e as OptiScalerException)?.Code ?? "failed", TimeSpan.FromMinutes(30));
            }
            finally
            {
                // The program may take a moment to let go of it.
                var ignored = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(8));
                    TryDelete(trial);
                });
            }
        }

        private static OptiScalerContract RememberProblem(OptiScalerCopy copy, string code, TimeSpan time)
        {
            var contract = new OptiScalerContract
            {
                Stamp = GetStamp(copy.Executable),
                Problem = code,
                Date = DateTime.UtcNow,
                Until = DateTime.UtcNow + time
            };
            Remember(copy, contract);
            return contract;
        }

        /// <summary>
        /// Everything being in its place isn't everything working: an empty folder is looked at as if it was a game, which
        /// goes through most of what Atrio asks and touches nothing.
        /// </summary>
        private static async Task CheckWorksAsync(OptiScalerBridge bridge, OptiScalerContract contract, CancellationToken token)
        {
            var folder = Path.Combine(Root, $".{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(folder);
                var question = new JObject { ["name"] = "Atrio", ["path"] = folder, ["platform"] = "Manual" };
                var answer = await bridge.AskAsync("state", question, TimeSpan.FromSeconds(60), null, token);
                if (!(answer["choices"] is JObject) || !(answer["chosen"] is JObject))
                {
                    throw new OptiScalerException("failed", "The answer about a game isn't the expected one.");
                }
            }
            catch (OptiScalerException e) when (e.Code != OptiScalerException.Slow)
            {
                // It may have been a bad moment: it's asked again later.
                contract.Usable = false;
                contract.Missing["state"] = new List<string> { $"{e.Code}: {e.Message}" };
                contract.Until = DateTime.UtcNow + TimeSpan.FromHours(2);
            }
            finally
            {
                TryDelete(folder);
            }
        }

        private static void Remember(OptiScalerCopy copy, OptiScalerContract contract)
        {
            lock (stateLock)
            {
                LoadState().Contracts[copy.Executable] = contract;
            }

            SaveState();
        }

        public static OptiScalerContract ParseContract(JObject answer)
        {
            var contract = new OptiScalerContract
            {
                Usable = (bool?)answer?["usable"] == true,
                Functions = (answer?["functions"] as JArray)?.Select(a => (string)a).ToList() ?? new List<string>()
            };
            foreach (var missing in (answer?["missing"] as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
            {
                contract.Missing[missing.Name] = (missing.Value as JArray)?.Select(a => (string)a).ToList() ?? new List<string>();
            }

            return contract;
        }

        public static List<OptiScalerRelease> ParseReleases(string json, Dictionary<string, string> hashes = null)
        {
            var result = new List<OptiScalerRelease>();
            foreach (var release in JArray.Parse(json).OfType<JObject>())
            {
                var number = ParseVersion((string)release["tag_name"]);
                foreach (var asset in (release["assets"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                {
                    var name = (string)asset["name"];
                    var digest = (string)asset["digest"];
                    var hash = digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest.Substring(7).ToLowerInvariant() : null;
                    if (name == null || !packageRegex.IsMatch(name))
                    {
                        continue;
                    }

                    if (hash != null && hashes != null)
                    {
                        hashes[hash] = name;
                    }

                    if (number != null && (bool?)release["draft"] != true && (bool?)release["prerelease"] != true)
                    {
                        result.Add(new OptiScalerRelease
                        {
                            Version = FormatVersion(number),
                            Url = (string)asset["browser_download_url"],
                            Hash = hash,
                            Size = (long?)asset["size"] ?? 0,
                            PageUrl = (string)release["html_url"],
                            Published = (DateTime?)release["published_at"] ?? DateTime.MinValue
                        });
                    }
                }
            }

            return result.OrderByDescending(a => ParseVersion(a.Version)).ToList();
        }

        /// <summary>The newest published version, as it was known the last time.</summary>
        public static OptiScalerRelease GetKnownLatest()
        {
            return LoadState().Releases.FirstOrDefault();
        }

        /// <summary>The newest published version. GitHub is asked again when it's time, or now if the person wants.</summary>
        public static async Task<OptiScalerRelease> GetLatestAsync(bool now, CancellationToken token)
        {
            var known = LoadState();
            if (!now && known.Releases.Count > 0 && DateTime.UtcNow - known.ReleasesDate < CheckEvery)
            {
                return known.Releases[0];
            }

            try
            {
                using (var response = await client.GetAsync(AtrioInfo.GetTestSetting("ATRIO_OPTISCALER_RELEASES") ?? ReleasesUrl, token))
                {
                    response.EnsureSuccessStatusCode();
                    var hashes = new Dictionary<string, string>();
                    var releases = ParseReleases(await response.Content.ReadAsStringAsync(), hashes);
                    if (releases.Count > 0)
                    {
                        lock (stateLock)
                        {
                            known.Releases = releases;
                            known.ReleasesDate = DateTime.UtcNow;
                            foreach (var hash in hashes)
                            {
                                known.KnownHashes[hash.Key] = hash.Value;
                            }
                        }

                        SaveState();
                    }
                }
            }
            catch (Exception e) when ((e is HttpRequestException || e is JsonException || e is OperationCanceledException) && !token.IsCancellationRequested)
            {
                logger.Warn(e, "Failed to ask GitHub for the versions of OptiScaler Client.");
                if (now || known.Releases.Count == 0)
                {
                    throw new OptiScalerException("network", e.Message);
                }
            }

            return known.Releases.FirstOrDefault();
        }

        private static string Hash(string file)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(file))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        /// <summary>
        /// Downloads a version next to the ones there are, checks that it's the published file and tries it. The one in use
        /// stays until the new one has shown that it works.
        /// </summary>
        public static async Task<OptiScalerCopy> DownloadAsync(OptiScalerRelease release, Action<double> progress, CancellationToken token)
        {
            if (string.IsNullOrEmpty(release?.Hash) || string.IsNullOrEmpty(release.Url))
            {
                throw new OptiScalerException("nohash", "GitHub doesn't give the hash of this version.");
            }

            await working.WaitAsync(token);
            var package = Path.Combine(Root, $".{release.Version}.zip");
            try
            {
                Directory.CreateDirectory(Root);
                await DownloadFileAsync(release, package, progress, token);
                var hash = await Task.Run(() => Hash(package));
                if (hash != release.Hash)
                {
                    throw new OptiScalerException("hash", $"The package of OptiScaler Client {release.Version} isn't the published one.");
                }

                var copy = await Task.Run(() => Unpack(package, true, ParseVersion(release.Version)));
                await TryCopyAsync(copy, token);
                RemoveOldCopies();
                return copy;
            }
            catch (HttpRequestException e)
            {
                throw new OptiScalerException("network", e.Message);
            }
            catch (Exception e) when (e is UnauthorizedAccessException || e is IOException || e is InvalidDataException)
            {
                logger.Error(e, "Failed to put OptiScaler Client in its folder.");
                throw new OptiScalerException("clientfolder", e.Message);
            }
            finally
            {
                FileSystem.DeleteFileSafe(package);
                working.Release();
            }
        }

        private static async Task DownloadFileAsync(OptiScalerRelease release, string path, Action<double> progress, CancellationToken token)
        {
            using (var response = await client.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? release.Size;
                using (var source = await response.Content.ReadAsStreamAsync())
                using (var target = File.Create(path))
                {
                    var buffer = new byte[81920];
                    long done = 0;
                    int read;

                    // Each read has its own limit: the one of the client ended with the headers.
                    while ((read = await ReadAsync(source, buffer, token)) > 0)
                    {
                        await target.WriteAsync(buffer, 0, read, token);
                        done += read;
                        if (total > 0)
                        {
                            progress?.Invoke(Math.Min(100, done * 100.0 / total));
                        }
                    }
                }
            }
        }

        private static async Task<int> ReadAsync(Stream source, byte[] buffer, CancellationToken token)
        {
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var read = source.ReadAsync(buffer, 0, buffer.Length, limit.Token);
                if (await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(60), token)) != read)
                {
                    token.ThrowIfCancellationRequested();
                    limit.Cancel();
                    throw new HttpRequestException("The download stopped.");
                }

                return await read;
            }
        }

        /// <summary>Takes a package out into the folder of its version, all or nothing. A copy that works isn't replaced.</summary>
        public static OptiScalerCopy Unpack(string package, bool own, Version published = null)
        {
            var temporary = Path.Combine(Root, $".{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(temporary);
                var inside = Path.GetFullPath(temporary).TrimEnd('\\') + "\\";
                using (var zip = ZipFile.OpenRead(package))
                {
                    // Some versions come inside a folder: what matters is next to the program.
                    var program = zip.Entries.FirstOrDefault(a => a.Name.Equals(ProgramFile, StringComparison.OrdinalIgnoreCase));
                    if (program == null)
                    {
                        throw new InvalidDataException($"{ProgramFile} isn't in {Path.GetFileName(package)}.");
                    }

                    var prefix = program.FullName.Substring(0, program.FullName.Length - program.Name.Length);
                    foreach (var entry in zip.Entries.Where(a => a.FullName.StartsWith(prefix, StringComparison.Ordinal) && a.Name.Length > 0))
                    {
                        var target = Path.GetFullPath(Path.Combine(temporary, entry.FullName.Substring(prefix.Length)));
                        if (!target.StartsWith(inside, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException($"{entry.FullName} goes outside of its folder.");
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        entry.ExtractToFile(target, true);
                    }
                }

                var number = published ?? ParseVersion(Path.GetFileName(package)) ?? ReadVersion(Path.Combine(temporary, ProgramFile)) ??
                    throw new InvalidDataException("The program doesn't say its version.");
                if (own)
                {
                    File.WriteAllText(Path.Combine(temporary, OwnMark), FormatVersion(number));
                }

                var folder = Path.Combine(Root, FormatVersion(number));
                var there = new OptiScalerCopy { Number = number, Executable = Path.Combine(folder, ProgramFile) };
                if (GetContract(there)?.Usable == true)
                {
                    return there;
                }

                if (Directory.Exists(folder))
                {
                    // The same version again: the new files, without leaving the old ones half replaced.
                    var old = Path.Combine(Root, $".{Guid.NewGuid():N}");
                    Directory.Move(folder, old);
                    Directory.Move(temporary, folder);
                    TryDelete(old);
                }
                else
                {
                    Directory.Move(temporary, folder);
                }

                return there;
            }
            finally
            {
                TryDelete(temporary);
            }
        }

        private static void TryDelete(string folder)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                logger.Debug(e, $"Failed to remove {folder}.");
            }
        }

        /// <summary>The folder of downloads of the person, and the one where download managers put the packages.</summary>
        private static IEnumerable<string> GetDownloadFolders()
        {
            // A test never looks at the downloads of the person.
            var downloads = TestDownloads ?? AtrioInfo.GetTestSetting("ATRIO_OPTISCALER_DOWNLOADS") ??
                (TestRoot != null || AtrioInfo.IsTestRun ? null : CapturePlaces.GetKnownFolder(new Guid("374DE290-123F-4565-9164-39C4925E467B")));
            return downloads.IsNullOrEmpty() ? Enumerable.Empty<string>() :
                new[] { downloads, Path.Combine(downloads, "Compressed"), Path.Combine(downloads, "Comprimidos") };
        }

        private static List<string> GetPackages(string folder, Func<string, bool> accepts)
        {
            try
            {
                return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.zip").Where(a => accepts(Path.GetFileName(a))).ToList() : new List<string>();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                logger.Debug(e, $"Failed to look for packages of OptiScaler Client in {folder}.");
                return new List<string>();
            }
        }

        private static string GetPackageStamp(string package)
        {
            var info = new FileInfo(package);
            return $"{info.Length}-{info.LastWriteTimeUtc.Ticks}";
        }

        /// <summary>One already looked at isn't looked at again: the folder of its version may have been removed on purpose.</summary>
        private static bool IsNew(string package)
        {
            return !LoadState().Packages.TryGetValue(package, out var seen) || seen != GetPackageStamp(package);
        }

        /// <summary>The packages that the person put in the folder of the client and weren't looked at yet.</summary>
        public static List<string> GetOwnPackages()
        {
            return GetPackages(Root, a => !a.StartsWith(".", StringComparison.Ordinal)).Where(IsNew).ToList();
        }

        /// <summary>The packages of the client that the person downloaded with their browser and weren't looked at yet, the newest first.</summary>
        public static List<string> GetDownloadedPackages()
        {
            return GetDownloadFolders().SelectMany(a => GetPackages(a, downloadedRegex.IsMatch)).Where(IsNew).
                OrderByDescending(a => ParseVersion(Path.GetFileName(a)) ?? new Version(0, 0)).
                ToList();
        }

        /// <summary>
        /// A package that the person left in the folder of the client, or among their downloads, is unpacked like a plugin
        /// if it's one that the project published. The file stays where it is. One that isn't known is left alone, and the
        /// log says so.
        /// </summary>
        public static async Task<List<string>> ImportPackagesAsync(CancellationToken token)
        {
            var strange = new List<string>();
            var known = LoadState();
            foreach (var package in await Task.Run(() => GetOwnPackages().Concat(GetDownloadedPackages()).ToList(), token))
            {
                var name = Path.GetFileName(package);
                await working.WaitAsync(token);
                try
                {
                    var stamp = GetPackageStamp(package);
                    var hash = await Task.Run(() => Hash(package), token);
                    if (!known.KnownHashes.ContainsKey(hash))
                    {
                        await GetLatestAsync(true, token);
                    }

                    if (!known.KnownHashes.TryGetValue(hash, out var published))
                    {
                        logger.Warn($"{name} isn't a package published by OptiScaler Client: it isn't used.");
                        strange.Add(name);
                    }
                    else if (IsUseful(ParseVersion(published)))
                    {
                        Directory.CreateDirectory(Root);
                        var copy = await Task.Run(() => Unpack(package, true, ParseVersion(published)), token);
                        logger.Info($"OptiScaler Client {copy.Version} taken out of {name}.");

                        // The new one has to show that it works before an older one goes away.
                        await TryCopyAsync(copy, token);
                        RemoveOldCopies();
                    }

                    lock (stateLock)
                    {
                        known.Packages[package] = stamp;
                    }

                    SaveState();
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException || e is OptiScalerException)
                {
                    // It's tried again the next time.
                    logger.Warn(e, $"Failed to read the package {name}.");
                }
                finally
                {
                    working.Release();
                }
            }

            return strange;
        }

        /// <summary>One older than the copy that works adds nothing, and neither does one that is already there.</summary>
        private static bool IsUseful(Version number)
        {
            var current = GetCurrent();
            return GetCopies().All(a => a.Number != number) &&
                (current == null || number == null || number > current.Number || GetContract(current)?.Usable != true);
        }

        /// <summary>
        /// The copy in use and the last one that works before it stay, and the ones that weren't tried. Only the ones Atrio
        /// downloaded go away.
        /// </summary>
        public static void RemoveOldCopies()
        {
            var current = GetCurrent();
            if (current == null || GetContract(current)?.Usable != true)
            {
                return;
            }

            var older = GetCopies().Where(a => a.Number < current.Number).ToList();
            var kept = older.FirstOrDefault(a => GetContract(a)?.Usable == true);
            foreach (var copy in older.Where(a => a != kept && GetContract(a) != null))
            {
                if (!File.Exists(Path.Combine(copy.Folder, OwnMark)) || copy.Folder.Equals(Root, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    // All or nothing: one that is open can't be moved, and it stays whole.
                    var old = Path.Combine(Root, $".{Guid.NewGuid():N}");
                    Directory.Move(copy.Folder, old);
                    TryDelete(old);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    logger.Debug(e, $"OptiScaler Client {copy.Version} stays for now.");
                }
            }
        }

        private static Process window;

        /// <summary>The window that Atrio opened is still there.</summary>
        public static bool IsWindowOpen
        {
            get
            {
                try
                {
                    return window?.HasExited == false;
                }
                catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// The client with its own window: straight to one game, or on its list when there's no game. Null if Windows doesn't
        /// let it start.
        /// </summary>
        public static Process OpenWindow(OptiScalerCopy copy, JObject game)
        {
            var info = new ProcessStartInfo(copy.Executable)
            {
                UseShellExecute = false,
                WorkingDirectory = copy.Folder
            };
            if (DataFolder != null)
            {
                info.EnvironmentVariables["OPTISCALER_CLIENT_APPDATA"] = DataFolder;
            }

            // A version that doesn't understand it opens on its list, which is what it would do anyway.
            if (game != null && File.Exists(BridgePath) && GetContract(copy)?.Functions.Contains("window") == true)
            {
                info.EnvironmentVariables["DOTNET_STARTUP_HOOKS"] = BridgePath;
                info.EnvironmentVariables["ATRIO_OPTISCALER_MODE"] = "window";
                info.EnvironmentVariables["ATRIO_OPTISCALER_GAME"] = game.ToString(Formatting.None);
            }

            try
            {
                return window = Process.Start(info);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException)
            {
                logger.Error(e, "OptiScaler Client couldn't start.");
                throw new OptiScalerException(OptiScalerException.NoStart, e.Message);
            }
        }
    }
}
