using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Atrio.OptiScaler
{
    /// <summary>
    /// The functions of OptiScaler Client for one game: what it has, what can be chosen, installing, changing and taking
    /// out. They follow, step by step, what its own «Quick install» and its window of a game do, with its own services.
    /// </summary>
    internal sealed class Games
    {
        private sealed class Reporter : IProgress<double>
        {
            private readonly Action<double> action;
            private int last = int.MinValue;

            public Reporter(Action<double> action)
            {
                this.action = action;
            }

            public void Report(double value)
            {
                // The client reports every piece it reads: one note per percent is enough.
                if ((int)value != last)
                {
                    last = (int)value;
                    action(value);
                }
            }
        }

        private const string None = "none";
        private const string Auto = "auto";
        private const string DefaultInjection = "dxgi.dll";

        // The folders with its name: in the second one, the packages from the 10 on have everything but the main file and its settings.
        private static readonly string[] ownFolders = { "D3D12_Optiscaler", "OptiScaler" };

        // Where its settings can be. The first one is the one that counts when there are both.
        private static readonly string[] iniNames = { "OptiScaler.ini", @"OptiScaler\OptiScaler.ini" };

        // The files that tell what a game has.
        private static readonly HashSet<string> programTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".dll", ".exe", ".asi", ".sys", ".msi" };
        private const string Components = "ComponentManagementService";
        private const string Installer = "GameInstallationService";
        private const string FrameService = "FrameGenerationConfigurationService";
        private const string OptiFg = "OptiFg";
        private const string ByHandName = "by hand";

        private readonly Func<string, string, double?, bool> tell;

        // Once something of the game changes, it goes on to its end whatever happens on the side of Atrio.
        private volatile bool touched;

        public Games(Func<string, string, double?, bool> tell)
        {
            this.tell = tell;
        }

        private void progress(string step, string item, double? percent)
        {
            var later = touched && (step == "download" || step == "extract" || step == "look");
            var there = tell(later ? "piece" : step, item, percent);
            if (!there && !touched)
            {
                throw new StoppedException();
            }

            if (step == "install" || step == "uninstall")
            {
                touched = true;
            }
        }

        public static void CheckNoWindow()
        {
            if (Safety.IsWindowOpen())
            {
                throw new ServiceException("clientopen", "OptiScaler Client is open.");
            }
        }

        private static void CheckFree(string folder, Record record)
        {
            var busy = Safety.FindBusy(folder, record);
            if (busy != null)
            {
                throw new ServiceException("gameopen", busy);
            }
        }

        private static string Parameter(JsonObject parameters, string name)
        {
            return parameters?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        }

        private static bool Switch(JsonObject parameters, string name)
        {
            return parameters?[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
        }

        private static string FullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path).TrimEnd('\\', '/');
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
            {
                return path.TrimEnd('\\', '/');
            }
        }

        private static bool SamePath(string one, string other)
        {
            return FullPath(one).Equals(FullPath(other), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The game in the list of the client, with the settings it keeps for it, or a new one that isn't in the list yet.
        /// By its folder, which is the same for any launcher, and then by its number in Steam.
        /// </summary>
        public static object Find(JsonObject parameters, IEnumerable games)
        {
            var path = Parameter(parameters, "path");
            var appId = Parameter(parameters, "appId");
            var platform = Parameter(parameters, "platform");
            var all = games?.Cast<object>().ToList() ?? new List<object>();
            return all.FirstOrDefault(a => SamePath(Client.Text(a, "InstallPath"), path)) ??
                (string.IsNullOrEmpty(appId) || platform != "Steam" ? null :
                    all.FirstOrDefault(a => Client.Text(a, "AppId") == appId && Client.Text(a, "Platform") == platform));
        }

        public static object Create(JsonObject parameters)
        {
            var game = Client.New("Game");
            var platform = Client.EnumValue("GamePlatform", Parameter(parameters, "platform")) ?? Client.EnumValue("GamePlatform", "Manual");
            var appId = Parameter(parameters, "appId");
            Client.Set(game, "Name", Parameter(parameters, "name") ?? string.Empty);
            Client.Set(game, "InstallPath", Parameter(parameters, "path") ?? string.Empty);
            if (platform != null)
            {
                Client.Set(game, "Platform", platform);
            }

            // The client names this way the games added by hand, and knows them by it.
            Client.Set(game, "AppId", platform?.ToString() == "Manual" || string.IsNullOrEmpty(appId) ? $"Manual_{Guid.NewGuid()}" : appId);
            return game;
        }

        private static IList LoadList()
        {
            return Client.FindType("GamePersistenceService") == null ? null :
                Client.Call(Client.New("GamePersistenceService"), "LoadGames") as IList;
        }

        private static object FindOrCreate(JsonObject parameters, out bool known)
        {
            var path = Parameter(parameters, "path");
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                throw new ServiceException("folder", $"The folder of the game isn't there: {path}");
            }

            var game = Find(parameters, LoadList());
            known = game != null;
            if (game == null)
            {
                return Create(parameters);
            }

            if (!SamePath(Client.Text(game, "InstallPath"), path))
            {
                // Moved to another disk since the client scanned it.
                Client.Set(game, "InstallPath", path);
            }

            return game;
        }

        /// <summary>
        /// The list of the client gets the game, and only it: the rest of the file stays as it is, also what another version
        /// of the client wrote in it. Nothing is written over a list that can't be read, or with the client open.
        /// </summary>
        private static void Save(object game, bool add)
        {
            var type = Client.FindType("GamePersistenceService");
            var field = type?.GetField("_filePath", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (field == null || field.FieldType != typeof(string) || Safety.IsWindowOpen())
            {
                return;
            }

            var service = Client.New("GamePersistenceService");
            var path = field.GetValue(service) as string;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var temporary = $"{path}.atrio-{Guid.NewGuid():N}";
            try
            {
                // The client writes it its way, apart.
                field.SetValue(service, temporary);
                var alone = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(game.GetType()));
                alone.Add(game);
                Client.Call(service, "SaveGames", "games", alone);
                if (!(JsonNode.Parse(File.ReadAllText(temporary)) is JsonArray written) || written.Count != 1 || !(written[0] is JsonObject own))
                {
                    return;
                }

                var list = new JsonArray();
                if (File.Exists(path))
                {
                    if (!(JsonNode.Parse(File.ReadAllText(path)) is JsonArray before))
                    {
                        return;
                    }

                    list = before;
                }

                var entry = list.OfType<JsonObject>().FirstOrDefault(a => SamePath((string)a["InstallPath"], (string)own["InstallPath"])) ??
                    list.OfType<JsonObject>().FirstOrDefault(a => !string.IsNullOrEmpty(a["AppId"]?.ToString()) &&
                        a["AppId"].ToString() == own["AppId"]?.ToString() && a["Platform"]?.ToString() == own["Platform"]?.ToString());
                if (entry == null && !add)
                {
                    return;
                }

                if (entry == null)
                {
                    list.Add(own.DeepClone());
                }
                else
                {
                    foreach (var value in own.ToList())
                    {
                        entry[value.Key] = value.Value?.DeepClone();
                    }

                    // The client doesn't write what is empty: what it had before goes away.
                    foreach (var property in game.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(a => !own.ContainsKey(a.Name)))
                    {
                        entry.Remove(property.Name);
                    }
                }

                File.WriteAllText(temporary, list.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                File.Move(temporary, path, true);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                // What was done in the game is done: the list is only where its settings are remembered.
                Console.Error.WriteLine($"The list of games wasn't changed: {e.Message}");
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                }
            }
        }

        /// <summary>The game goes to the list of the client, with what it finds in its folder, if it isn't there yet.</summary>
        public static void Remember(JsonObject parameters)
        {
            var game = FindOrCreate(parameters, out var known);
            if (!known)
            {
                Analyze(game, true);
                Save(game, true);
            }
        }

        private static void Analyze(object game, bool force)
        {
            var type = Client.Type("GameAnalyzerService");
            if (Client.CanCall("GameAnalyzerService", "LoadCacheFromDisk"))
            {
                // What the client knows of the other games: its file is written whole.
                Client.Call(type, "LoadCacheFromDisk");
            }

            if (force && Client.CanCall("GameAnalyzerService", "InvalidateCacheForPath", "installPath"))
            {
                Client.Call(type, "InvalidateCacheForPath", "installPath", Client.Text(game, "InstallPath"));
            }

            Client.Call(Client.New("GameAnalyzerService"), "AnalyzeGame", "game", game, "forceRefresh", force);
            if (force && Client.CanCall("GameAnalyzerService", "FlushCacheToDisk"))
            {
                Client.Call(type, "FlushCacheToDisk");
            }
        }

        private static string Directory_(object game)
        {
            return Client.Call(Client.New(Installer), "DetermineInstallDirectory", "game", game) as string;
        }

        private static string FolderOf(object game, Record record)
        {
            return !string.IsNullOrEmpty(record.Folder) && Directory.Exists(record.Folder) ? record.Folder : Directory_(game);
        }

        private static bool IsHalf(object game, Record record)
        {
            return record.IsHalf || Checkpoint.FolderOf(Client.Text(game, "InstallPath")) != null;
        }

        /// <summary>
        /// A change that was cut in the middle is undone first: installing over it would take files of OptiScaler for the
        /// originals of the game. Null when there was nothing to undo, or what was left because it isn't known whose it is.
        /// </summary>
        private List<string> Recover(object game, string step)
        {
            var key = Client.Text(game, "InstallPath");
            var kept = Checkpoint.FolderOf(key);
            var record = Record.Find(game);
            if (kept == null && !record.IsHalf)
            {
                return null;
            }

            if (kept == null)
            {
                // It was the client by itself: what it was installing says what to take out.
                FindPackage(record);
            }

            var folder = kept ?? FolderOf(game, record);
            CheckFree(folder, record);
            progress(step, null, null);
            var left = (kept != null ? Checkpoint.PutBack(key) : record.Undo(folder)) ??
                throw new ServiceException("half", "A change was cut in the middle and there's no way to know what it did.");
            Guests.BringBack(folder, Record.Find(game).Files);
            return left;
        }

        private void FindPackage(Record record)
        {
            var version = Client.Text(record.Manifest, "OptiscalerVersion");
            if (string.IsNullOrEmpty(version) || HasFiles(record.Package))
            {
                return;
            }

            try
            {
                Download(Client.New(Components), version);
            }
            catch (Exception e) when (!(e is OutOfMemoryException) && !(e is StoppedException))
            {
                Console.Error.WriteLine($"OptiScaler {version} couldn't be downloaded: {e.Message}");
                throw new ServiceException("halfpackage", version);
            }
        }

        /// <summary>
        /// The client looks first in the folder of the program of the game, when it knows it, and the bridge in the one where
        /// OptiScaler is or would go: this way it's the same one for both.
        /// </summary>
        private static object CallInstaller(object game, string method, Dictionary<string, object> values)
        {
            var program = Client.TryGet(game, "ExecutablePath");
            Client.Set(game, "ExecutablePath", program == null ? null : string.Empty);
            try
            {
                return Client.CallWith(Client.New(Installer), method, values);
            }
            finally
            {
                Client.Set(game, "ExecutablePath", program);
            }
        }

        /// <summary>What the client takes for files of OptiScaler when it takes them out.</summary>
        private static Plan GetPlan(string folder, Record record)
        {
            var plan = new Plan();
            var installer = Client.Type(Installer);
            foreach (var name in record.Files.Concat(Client.Strings(Client.TryGet(installer, "KnownOptiscalerArtifacts"))))
            {
                plan.Add(name);
            }

            // It removes these ones whole.
            var directories = Client.Strings(Client.TryGet(installer, "KnownOptiscalerDirectories"));
            foreach (var directory in directories.Count > 0 ? directories : new List<string> { "D3D12_Optiscaler", "Licenses", "plugins", "OptiScaler" })
            {
                foreach (var file in Plan.Files(Path.Combine(folder, directory)))
                {
                    plan.Add(Path.GetRelativePath(folder, file));
                }
            }

            return plan;
        }

        /// <summary>Everything that the client may write or remove in the folder of the game when it installs this.</summary>
        private static Plan GetPlan(string folder, Record record, Dictionary<string, object> values)
        {
            var plan = GetPlan(folder, record);
            var package = values["cachePath"] as string;
            var injection = values["injectionDllName"] as string;
            var main = new HashSet<string>(InjectionNames().Append("OptiScaler.dll").Append("nvngx.dll"), StringComparer.OrdinalIgnoreCase);
            plan.AddFolder(package, string.Empty);
            plan.Add(injection);
            foreach (var file in Plan.Files(package))
            {
                var name = Path.GetFileName(file);
                if (main.Contains(name))
                {
                    plan.Add(injection, file);
                }

                // The ones of the game with the same name as the ones that go in the folder of OptiScaler.
                if (File.Exists(Path.Combine(folder, name)))
                {
                    plan.Add(name, file);
                }
            }

            // A ReShade in the place of OptiScaler gets another name.
            plan.Add("ReShade64.dll", Path.Combine(folder, injection ?? string.Empty));
            foreach (var piece in values.Where(a => a.Key != "cachePath" && a.Key.EndsWith("CachePath", StringComparison.OrdinalIgnoreCase)))
            {
                if (piece.Key.StartsWith("streamline", StringComparison.OrdinalIgnoreCase))
                {
                    plan.AddFolder(piece.Value as string, "streamline", @"OptiScaler\streamline");
                    continue;
                }

                foreach (var file in Plan.Files(piece.Value as string))
                {
                    plan.Add(Path.GetFileName(file), file);
                    plan.Add(Path.Combine("OptiScaler", Path.GetFileName(file)), file);
                }
            }

            return plan;
        }

        private static object PreferredGpu(object config)
        {
            try
            {
                var service = Client.Call(Client.Type("PlatformServiceFactory"), "CreateGpuDetectionService");
                return Client.Call(Client.Type("GpuSelectionHelper"), "GetPreferredGpu", "gpuService", service, "defaultGpuId", Client.Text(config, "DefaultGpuId"));
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                // The client works without knowing the graphics card too.
                return null;
            }
        }

        private static bool Gpu(string question, object gpu)
        {
            return gpu != null && Client.CanCall("GpuSelectionHelper", question, "gpu", "@GpuInfo") &&
                Client.Call(Client.Type("GpuSelectionHelper"), question, "gpu", gpu) is bool value && value;
        }

        /// <summary>The value of a key of the OptiScaler.ini of the game. Null when it isn't there.</summary>
        private static string ReadIni(string directory, string section, string key)
        {
            return IniFile.Read(IniPath(directory))?.Get(section, key);
        }

        private static IEnumerable<string> IniPaths(string directory)
        {
            return string.IsNullOrEmpty(directory) ? Enumerable.Empty<string>() : iniNames.Select(a => Path.Combine(directory, a)).Where(File.Exists);
        }

        private static string IniPath(string directory)
        {
            return IniPaths(directory).FirstOrDefault();
        }

        /// <summary>
        /// The upscaler OptiScaler takes by itself for each kind of game with this graphics card, with the names of the newest
        /// versions. Without a name it isn't known, and the one there is counts as chosen.
        /// </summary>
        private static Dictionary<string, string> AutomaticUpscalers(object gpu, IniFile settings)
        {
            // Up to the 0.9 its file tells the one of DirectX 12. From the 10 on it's FSR 4 wherever it can be.
            var older = settings?.Default("Upscalers", "Dx12Upscaler") != null;
            var rtx = Client.Text(gpu, "Name")?.Contains("RTX", StringComparison.OrdinalIgnoreCase) == true;
            string dx12 = null;
            switch (Client.Text(gpu, "Vendor"))
            {
                case "AMD":
                    dx12 = Gpu("IsRdna4", gpu) || Gpu("IsRdna3", gpu) ? "ffx" : older ? "xess" : Gpu("IsRdna2", gpu) ? "ffx" : null;
                    break;
                case "NVIDIA":
                    dx12 = rtx ? (older || !Gpu("IsTuringRtx", gpu) ? "dlss" : null) : older ? "xess" : null;
                    break;
                case "Intel":
                    dx12 = "xess";
                    break;
            }

            var other = rtx ? "dlss" : "fsr22";
            var result = new Dictionary<string, string> { ["upscalers|dx11upscaler"] = other, ["upscalers|vulkanupscaler"] = other };
            if (dx12 != null)
            {
                result["upscalers|dx12upscaler"] = dx12;
            }

            return result;
        }

        /// <summary>
        /// What the person changed in the settings of OptiScaler. When it isn't known how they were left, because an earlier
        /// Atrio or the client by itself did it, what the client may have written isn't taken for theirs.
        /// </summary>
        private static List<Setting> OwnOf(object game, Record record, string folder, object gpu)
        {
            var now = IniFile.Read(IniPath(folder));
            if (!record.IsOfOptiScaler)
            {
                return OwnSettings.Find(now, null, AutomaticUpscalers(gpu, now));
            }

            var left = LeftOf(game, record, now);
            if (left != null)
            {
                return OwnSettings.Find(now, left, AutomaticUpscalers(gpu, left));
            }

            var profile = ProfileSettings(record);
            return OwnSettings.Find(now, null, AutomaticUpscalers(gpu, now)).
                Where(a => OwnSettings.OptionOf(a) == null && !OwnSettings.IsOfTheClient(a) && !(profile.TryGetValue(a.Id, out var value) && OwnSettings.IsSame(a, value))).ToList();
        }

        /// <summary>How the settings of a game were left. Null when it isn't known.</summary>
        private static IniFile LeftOf(object game, Record record, IniFile now)
        {
            return new SettingsStore(Client.Text(game, "InstallPath")).Left(record, now, () => ProfileSettings(record).Keys);
        }

        /// <summary>What the profile of an install says, which the client wrote too.</summary>
        private static Dictionary<string, string> ProfileSettings(Record record)
        {
            var result = new Dictionary<string, string>();
            if (Client.Text(record.Manifest, "AppliedProfileName") is string name && name.Length > 0 && Contract.Has("profile"))
            {
                var profile = Client.Call(Client.New("ProfileManagementService"), "GetProfileByName", "name", name);
                foreach (var section in Client.TryGet(profile, "IniSettings") as IEnumerable<KeyValuePair<string, Dictionary<string, string>>> ?? Enumerable.Empty<KeyValuePair<string, Dictionary<string, string>>>())
                {
                    foreach (var setting in section.Value)
                    {
                        result[Setting.GetId(section.Key, setting.Key)] = setting.Value;
                    }
                }
            }

            return result;
        }

        /// <summary>An option of the panel stays as the person has it in the settings of the game: it isn't chosen in the panel.</summary>
        /// <param name="fresh">They start over: only the ones that are told to stay do.</param>
        private static bool IsOwn(JsonObject options, string option, bool fresh = false)
        {
            var value = options?[option] is JsonObject frames ? Parameter(frames, "output") : Parameter(options, option);
            return value == OwnSettings.Own || (value == null && !fresh);
        }

        /// <summary>
        /// With the settings of the new version there, as the client left them, the ones of the person go in: the ones it
        /// doesn't have wait for another that does.
        /// </summary>
        /// <param name="was">The file there was before.</param>
        private static void KeepSettings(object game, string folder, string package, IniFile was, List<Setting> own, List<Setting> waiting, List<string> notes)
        {
            var path = IniPath(folder);
            var ini = IniFile.Read(path);
            var record = Record.Find(game);
            if (ini == null || !record.IsOfOptiScaler)
            {
                return;
            }

            ini.Tidy();
            var fitting = Fitting(package, was, own, waiting);
            var mine = fitting.Keys.ToList();
            var left = mine.Where(a => fitting[a] == null && !Setting.IsAuto(a.Value)).ToList();
            var clean = IniFile.Parse(ini.ToString());
            foreach (var setting in fitting.Values.Where(a => a != null))
            {
                ini.Set(setting.Section, setting.Key, setting.Value);
            }

            // If they can't go in, all of them are still waiting.
            ini.Save(path);
            new SettingsStore(Client.Text(game, "InstallPath")).Keep(record, clean, left, true);
            if (mine.Count > left.Count)
            {
                notes.Add($"kept: {mine.Count - left.Count}");
            }

            // The ones that were already waiting were told before.
            var dropped = left.Where(own.Contains).ToList();
            if (dropped.Count > 0)
            {
                notes.Add($"dropped: {string.Join(", ", dropped.Take(8))}{(dropped.Count > 8 ? $" (+{dropped.Count - 8})" : string.Empty)}");
            }
        }

        /// <summary>How each one is written in the version of a package. Null for the ones it doesn't have.</summary>
        private static Dictionary<Setting, Setting> Fitting(string package, IniFile was, List<Setting> own, List<Setting> waiting)
        {
            var version = IniFile.Read(Path.Combine(package ?? string.Empty, iniNames[0]));
            return Together(own, waiting).ToDictionary(a => a, a => OwnSettings.Fit(a, version, own.Contains(a) ? was : null));
        }

        private static List<Setting> Together(List<Setting> own, List<Setting> waiting)
        {
            return own.Concat(waiting.Where(a => own.All(b => b.Id != a.Id))).ToList();
        }

        /// <summary>A copy of the settings there are, when they aren't just as they were left.</summary>
        private static void CopySettings(object game, Record record, string folder, List<string> notes)
        {
            Try(notes, "copy", () =>
            {
                var now = IniFile.Read(IniPath(folder));
                if (OwnSettings.IsWorthKeeping(now, record.IsOfOptiScaler ? LeftOf(game, record, now) : null))
                {
                    new SettingsStore(Client.Text(game, "InstallPath")).Copy(IniPath(folder), record.IsOfOptiScaler ? Client.Text(record.Manifest, "OptiscalerVersion") : ByHandName);
                }
            });
        }

        /// <summary>The client has its own place for it, where OptiScaler doesn't look.</summary>
        private static void PlaceHudFix(string folder)
        {
            var path = IniPath(folder);
            var ini = IniFile.Read(path);
            if (ini?.Get("HUDFix", "HUDFix") is string value && !Setting.IsAuto(value) && ini.Has("OptiFG", "HUDFix") && Setting.IsAuto(ini.Get("OptiFG", "HUDFix")))
            {
                ini.Set("OptiFG", "HUDFix", value);
                ini.Save(path);
            }
        }

        private static bool HasPlugin(string directory, string name)
        {
            return !string.IsNullOrEmpty(directory) && new[] { directory, Path.Combine(directory, "OptiScaler") }.Any(a => File.Exists(Path.Combine(a, "plugins", name)));
        }

        private sealed class Compatibility
        {
            public object Entry;
            public object Details;
        }

        private static Compatibility FindCompatibility(object game, bool ask)
        {
            if (!Contract.Has("compatibility"))
            {
                return new Compatibility();
            }

            try
            {
                var service = Client.New("CompatibilityListService");
                if (ask && Client.CanCall("CompatibilityListService", "CheckForUpdatesAsync"))
                {
                    Client.Call(service, "CheckForUpdatesAsync");
                }

                var method = Client.Type("CompatibilityListService").GetMethod("TryGetForGame");
                var arguments = new object[] { Client.Text(game, "Name"), null };
                if (method == null || !(method.Invoke(service, arguments) is bool found) || !found)
                {
                    return new Compatibility();
                }

                var result = new Compatibility { Entry = arguments[1] };
                if (Client.CanCall("CompatibilityListService", "GetCachedGameWikiDetails", "entry", "@CompatibilityListEntry"))
                {
                    result.Details = Client.Call(service, "GetCachedGameWikiDetails", "entry", result.Entry);
                }

                if (result.Details == null && ask && Client.CanCall("CompatibilityListService", "GetGameWikiDetailsAsync", "entry", "@CompatibilityListEntry"))
                {
                    result.Details = Client.Call(service, "GetGameWikiDetailsAsync", "entry", result.Entry);
                }

                return result;
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                // It's only advice: the rest works without it.
                return new Compatibility();
            }
        }

        private static List<string> InjectionNames()
        {
            // The client keeps its list in the window of a game. Without it, the one of the documentation of OptiScaler.
            var type = Client.FindType("ManageGameWindow");
            var names = type == null ? new List<string>() : Client.Strings(Client.TryGet(type, "KnownInjectionDllNames"));
            return names.Count > 0 ? names : new List<string> { "dxgi.dll", "winmm.dll", "d3d12.dll", "dbghelp.dll", "version.dll", "wininet.dll", "winhttp.dll" };
        }

        /// <summary>The name under which an OptiScaler that was put by hand is in the folder. Null without one.</summary>
        private static string FindByHand(string folder)
        {
            return string.IsNullOrEmpty(folder) ? null : MainNames().FirstOrDefault(a => IsMain(Path.Combine(folder, a)));
        }

        /// <summary>The names a game loads OptiScaler by: the ones of the client and the ones that only its own script gives.</summary>
        private static IEnumerable<string> MainNames()
        {
            return InjectionNames().Concat(new[] { "nvngx.dll", "OptiScaler.asi" }).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsMain(string path)
        {
            return File.Exists(path) &&
                System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductName?.StartsWith("OptiScaler", StringComparison.OrdinalIgnoreCase) == true;
        }

        /// <summary>
        /// What was put by hand and is of OptiScaler: its file, under any of the names it may have, the ones that nobody else
        /// calls that way, the ones its packages bring in the folder with its own name, and the ones that came with all of
        /// them next to the game. The rest could be of the game or of somebody else. Nothing when its file isn't there:
        /// whatever the game loads it by would be left without the rest.
        /// </summary>
        /// <param name="package">The one that takes its place, when there's one.</param>
        private static List<string> FilesByHand(string folder, string package)
        {
            var result = SureByHand(folder, package);
            return result.Count == 0 ? result : result.Concat(ByHand.CameTogether(folder, result)).ToList();
        }

        private static List<string> SureByHand(string folder, string package)
        {
            var result = string.IsNullOrEmpty(folder) || !Directory.Exists(folder) ? new List<string>() :
                MainNames().Append("OptiScaler.dll").Select(a => Path.Combine(folder, a)).Where(IsMain).ToList();
            if (result.Count == 0)
            {
                return result;
            }

            result.AddRange(Directory.GetFiles(folder).Where(a => ByHand.IsCertain(Path.GetFileName(a))));
            var brought = new HashSet<string>(Plan.Files(package).Select(a => Path.GetRelativePath(package, a)).Where(Guests.IsInside), StringComparer.OrdinalIgnoreCase);
            foreach (var file in ownFolders.SelectMany(a => Plan.Files(Path.Combine(folder, a))))
            {
                var name = Path.GetRelativePath(folder, file);
                if (brought.Contains(name) || Guests.IsPiece(name) || Guests.IsOfTheClient(name))
                {
                    result.Add(file);
                }
            }

            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void RemoveByHand(Checkpoint checkpoint, string folder, List<string> files)
        {
            Unlock(checkpoint, folder, files.Select(a => Path.GetRelativePath(folder, a)));
            foreach (var file in files.Where(File.Exists))
            {
                File.Delete(file);
            }

            Checkpoint.RemoveEmpty(ownFolders.Append("Licenses").Select(a => Path.Combine(folder, a)).SelectMany(a => Plan.Folders(a).Append(a)));
        }

        private static void Unlock(Checkpoint checkpoint, string folder, IEnumerable<string> names)
        {
            var plan = new Plan();
            foreach (var name in names)
            {
                plan.Add(name);
            }

            checkpoint.Unlock(folder, plan);
        }

        /// <summary>What is of OptiScaler in the game: what its record says, and what the client puts without writing it down.</summary>
        private static IEnumerable<string> OwnFiles(object game, string folder)
        {
            const string companion = @"OptiScaler\amdxc64.dll";
            var files = Record.Find(game).Files.ToList();
            var version = Client.Text(game, "Fsr4ExtraVersion");
            if (!string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(version) && Client.CanCall(Components, "GetCachedCustomAmdxc64Path", "version"))
            {
                // Only when it's the same one: a person can have put their own.
                var there = Path.Combine(folder, companion);
                var kept = Client.Call(Client.New(Components), "GetCachedCustomAmdxc64Path", "version", version) as string;
                if (File.Exists(there) && !string.IsNullOrEmpty(kept) && File.Exists(kept) && Checkpoint.IsSame(there, kept))
                {
                    files.Add(companion);
                }
            }

            return files;
        }

        /// <summary>The name of the file of OptiScaler that the wiki recommends for the game, as the client chooses it.</summary>
        private static string SuggestedInjection(Compatibility compatibility, List<string> names)
        {
            var listed = (Client.Text(compatibility.Details, "Filename") ?? string.Empty).
                Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
            var chosen = listed.Count == 0 ? null :
                listed.Count == 1 ? listed[0] :
                listed.FirstOrDefault(a => a.Equals(DefaultInjection, StringComparison.OrdinalIgnoreCase)) ?? listed[0];
            return names.FirstOrDefault(a => a.Equals(chosen, StringComparison.OrdinalIgnoreCase));
        }

        private sealed class Defaults
        {
            public string Version;
            public string Injection;
            public string Profile;
            public string Spoofing;
            public string Output;
            public string Quality;
            public string Fsr4;
            public string Patcher;
        }

        private static string NotLatest(string value, Func<string> latest)
        {
            return value == "__latest__" ? latest() : value;
        }

        private static Defaults ReadDefaults(object components, object gpu, Compatibility compatibility)
        {
            var config = Client.TryGet(components, "Config");
            var injection = Client.Text(config, "DefaultInjectionMethod");
            if (string.IsNullOrEmpty(injection) || injection.Equals(Auto, StringComparison.OrdinalIgnoreCase))
            {
                injection = SuggestedInjection(compatibility, InjectionNames()) ?? DefaultInjection;
            }

            var patcher = Client.Text(config, "DefaultOptiPatcherVersion");
            if (string.IsNullOrEmpty(patcher) || patcher.Equals(Auto, StringComparison.OrdinalIgnoreCase))
            {
                patcher = Client.Flag(compatibility.Entry, "OptiPatcherSupported") ? Client.Text(components, "LatestOptiPatcherVersion") ?? None : None;
            }

            var version = Client.Text(components, "EffectiveDefaultOptiScalerVersion");
            return new Defaults
            {
                Version = string.IsNullOrEmpty(version) ? Client.Text(components, "LatestStableVersion") : version,
                Injection = injection,
                Profile = Client.Text(config, "DefaultProfileName"),
                Spoofing = Client.Text(config, "DefaultDxgiSpoofing") ?? Auto,
                Output = Client.Text(config, "DefaultOutputUpscalerBackend") ?? "Default",
                Quality = Client.Text(config, "DefaultUpscalingQualityPreset") ?? "GameControlled",
                Fsr4 = DefaultFsr4(components, config, gpu),
                Patcher = NotLatest(patcher, () => Client.Text(components, "LatestOptiPatcherVersion") ?? None)
            };
        }

        /// <summary>
        /// The file of FSR 4 for the cards that don't have it in their driver: the choice of the client, or what it
        /// proposes for the card. A card of NVIDIA has DLSS and a recent one of AMD the official FSR 4: none for them.
        /// </summary>
        private static string DefaultFsr4(object components, object config, object gpu)
        {
            if (!Contract.Has("fsr4"))
            {
                return None;
            }

            var versions = Client.Strings(Client.TryGet(components, "ExtrasAvailableVersions"));
            var chosen = Client.Text(config, "DefaultExtrasVersion");
            if (chosen == "__latest__")
            {
                return Client.Text(components, "LatestExtrasVersion") ?? versions.FirstOrDefault() ?? None;
            }

            if (!string.IsNullOrEmpty(chosen))
            {
                return versions.Contains(chosen) ? chosen : None;
            }

            if (Gpu("IsRdna2", gpu) && Client.CanCall(Components, "GetRdna2PreferredExtrasVersion"))
            {
                return Client.Call(components, "GetRdna2PreferredExtrasVersion") as string ?? None;
            }

            return None;
        }

        private static JsonObject Frames(object settings)
        {
            return settings == null ? null : new JsonObject
            {
                ["route"] = Client.Text(settings, "Route"),
                ["output"] = Client.Text(settings, "Output"),
                ["mode"] = Client.Text(settings, "MultiFrameMode"),
                ["advanced"] = Client.Flag(settings, "AdvancedMode"),
                ["replacement"] = Client.Text(settings, "NvngxReplacement")
            };
        }

        /// <summary>The files of OptiScaler there are in the game, whoever put them.</summary>
        private static HashSet<string> OfOptiScaler(object game, string folder, Record record)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                result.UnionWith(ownFolders.SelectMany(a => Plan.Files(Path.Combine(folder, a))));
                result.UnionWith(record.IsWhole ? OwnFiles(game, folder).Select(a => Path.Combine(folder, a)) : Enumerable.Empty<string>());
                result.UnionWith(record.IsOfOptiScaler ? Enumerable.Empty<string>() : FilesByHand(folder, null));
            }

            return result;
        }

        /// <summary>
        /// What the game has by itself for frame generation. The client looks at the names of the files in its folder, also
        /// the ones OptiScaler brings: with them there, any game has the frames of FSR 3 and XeSS, and what it wrote then was
        /// no use and stayed for the next version. It looks at the same names without the ones of OptiScaler.
        /// </summary>
        private static object DetectFrames(object game, object gpu, string folder, Record record)
        {
            var program = Client.TryGet(game, "ExecutablePath") as string;
            var root = string.IsNullOrWhiteSpace(program) ? Client.Text(game, "InstallPath") : Path.GetDirectoryName(program) ?? Client.Text(game, "InstallPath");
            var service = Client.New(FrameService);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return Client.Call(service, "DetectCapabilities", "game", game, "gpu", gpu);
            }

            // The stores write their paths in their own way.
            root = FullPath(root) + Path.DirectorySeparatorChar;
            var ours = OfOptiScaler(game, FullPath(folder), record);
            var search = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            var names = Directory.EnumerateFiles(root, "*", search).Where(a => programTypes.Contains(Path.GetExtension(a)) && !ours.Contains(a)).
                Concat(record.IsWhole ? record.Originals.Keys : Enumerable.Empty<string>()).
                Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var before = new[] { "ExecutablePath", "InstallPath", "DlssFrameGenVersion" }.ToDictionary(a => a, a => Client.TryGet(game, a));
            var place = Path.Combine(Path.GetTempPath(), $"atrio-optiscaler-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(place);
                foreach (var name in names)
                {
                    File.WriteAllBytes(Path.Combine(place, name), Array.Empty<byte>());
                }

                Client.Set(game, "InstallPath", place);
                Client.Set(game, "ExecutablePath", program == null ? null : string.Empty);
                if (Client.Text(game, "DlssFrameGenPath") is string path && path.Length > 0 && (ours.Contains(FullPath(path)) || !File.Exists(path)))
                {
                    // The client doesn't forget the one that isn't there anymore.
                    Client.Set(game, "DlssFrameGenVersion", null);
                }

                return Client.Call(service, "DetectCapabilities", "game", game, "gpu", gpu);
            }
            finally
            {
                foreach (var value in before)
                {
                    Client.Set(game, value.Key, value.Value);
                }

                try
                {
                    Directory.Delete(place, true);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"A temporary folder couldn't be removed: {e.Message}");
                }
            }
        }

        private static object TryDetectFrames(object game, object gpu, string folder, Record record)
        {
            try
            {
                return DetectFrames(game, gpu, folder, record);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                Console.Error.WriteLine($"What the game has for frame generation isn't known: {e.Message}");
                return null;
            }
        }

        private static object Recommend(object capabilities)
        {
            return Client.CanCall(FrameService, "GetRecommendation", "c", "@FrameGenerationCapabilities") ?
                Client.Call(Client.New(FrameService), "GetRecommendation", "c", capabilities) : null;
        }

        /// <summary>The game has what that way takes the frames from. The client has others that it only does when they are asked for.</summary>
        private static bool IsUsable(object capabilities, object route)
        {
            var routes = Client.Items(Client.TryGet(capabilities, "AvailableRoutes"));
            return route != null && route.ToString() != "Disabled" && (routes.Count == 0 || routes.Contains(route));
        }

        private static JsonArray FrameChoices(object capabilities, out JsonObject recommendation)
        {
            recommendation = null;
            var result = new JsonArray();
            if (capabilities == null)
            {
                return result;
            }

            try
            {
                var service = Client.New(FrameService);
                var proposal = Recommend(capabilities);
                var route = Client.Text(proposal, "Route");
                if (proposal != null)
                {
                    recommendation = new JsonObject
                    {
                        ["route"] = route,
                        ["output"] = Client.Text(proposal, "Output"),
                        ["level"] = Client.Text(proposal, "Level")
                    };
                }

                var outputs = Client.Items(Client.TryGet(capabilities, "AvailableOutputs"));
                var usable = proposal == null || IsUsable(capabilities, Client.TryGet(proposal, "Route"));
                if (!usable)
                {
                    // There's nothing they could be taken from in this game.
                    outputs = outputs.Where(a => a.ToString().StartsWith("DlssG", StringComparison.Ordinal)).ToList();
                }
                else if (proposal != null)
                {
                    // OptiScaler brings these two, whatever the game has.
                    outputs.AddRange(new[] { "FsrFg", "XeFg" }.Select(a => Client.EnumValue("FrameGenerationOutput", a)).Where(a => a != null && !outputs.Contains(a)));
                }

                var modesKnown = Client.CanCall(FrameService, "GetAvailableMfgModes", "route", "output", "capabilities", "nvngxReplacement", "@FrameGenerationCapabilities");
                foreach (var output in outputs)
                {
                    var effective = output.ToString() == "Auto" && proposal != null ? Client.TryGet(proposal, "Output", output) : output;
                    var modes = modesKnown ?
                        Client.Strings(Client.Call(
                            service, "GetAvailableMfgModes", "route", Client.EnumValue("FrameGenerationRoute", "Auto"), "output", effective, "capabilities", capabilities)) :
                        new List<string>();
                    result.Add(new JsonObject { ["id"] = output.ToString(), ["modes"] = Service.Array(modes) });
                }

                if (HasOptiFg && !Client.Flag(capabilities, "IsAntiCheatDetected"))
                {
                    result.Add(new JsonObject { ["id"] = OptiFg, ["modes"] = Service.Array(new[] { "X2" }) });
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                return new JsonArray();
            }

            return result;
        }

        /// <summary>The frames out of what the upscaler gets, for the games without frame generation of their own.</summary>
        private static bool HasOptiFg => Client.EnumValue("FrameGenerationRoute", OptiFg) != null && Client.EnumValue("FrameGenerationOutput", "FsrFg") != null &&
            Client.FindType("GameFrameGenerationSettings") is Type settings && Client.Has(settings, "AdvancedMode");

        private static object CopyFrames(object original)
        {
            var copy = Client.New("GameFrameGenerationSettings");
            foreach (var property in original.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(a => a.CanRead && a.CanWrite && a.Name != "AppliedAtUtc"))
            {
                property.SetValue(copy, property.GetValue(original));
            }

            return copy;
        }

        /// <summary>The frame generation chosen for a game, and the same one with the way to do it already decided.</summary>
        private sealed class FramePlan
        {
            public object Chosen;
            public object Applied;
            public object Capabilities;

            /// <summary>The game has nothing to decide between: it will be off.</summary>
            public bool IsNothing;
        }

        /// <summary>Decided here, with what the game has: inside an install the client would do it with what OptiScaler has just brought.</summary>
        private static FramePlan PlanFrames(object game, object gpu, string folder, Record record)
        {
            var plan = new FramePlan { Chosen = Client.TryGet(game, "FrameGenerationSettings") };
            plan.Applied = plan.Chosen;
            if (plan.Chosen == null || !Contract.Has("frames"))
            {
                return plan;
            }

            plan.Capabilities = DetectFrames(game, gpu, folder, record);
            var recommendation = Recommend(plan.Capabilities);
            if (Client.Text(plan.Chosen, "Route") != "Auto" || recommendation == null || !Client.Has(plan.Chosen, "AdvancedMode") ||
                !Client.CanCall(FrameService, "ResolveEffectiveRoute", "settings", "recommendation", "capabilities",
                    "@GameFrameGenerationSettings", "@FrameGenerationRecommendation", "@FrameGenerationCapabilities"))
            {
                return plan;
            }

            var route = Client.Call(Client.New(FrameService), "ResolveEffectiveRoute", "settings", plan.Chosen, "recommendation", recommendation, "capabilities", plan.Capabilities);
            var output = Client.Text(plan.Chosen, "Output");
            if (route?.ToString() == "Disabled" || (output == "Auto" && !IsUsable(plan.Capabilities, route)))
            {
                plan.Applied = NoFrames();
                plan.IsNothing = true;
                return plan;
            }

            if (!IsUsable(plan.Capabilities, route))
            {
                // The client itself says whether it can be.
                return plan;
            }

            plan.Applied = CopyFrames(plan.Chosen);
            Client.Set(plan.Applied, "Route", route);
            if (output == "Auto")
            {
                // That way goes only with its own output.
                var nukem = route.ToString() == "Nukem" ? Client.EnumValue("FrameGenerationOutput", "Nukem") : null;
                Client.Set(plan.Applied, "Output", nukem ?? Client.TryGet(recommendation, "Output"));
            }

            // The client only takes a way that is already decided from its advanced ones.
            Client.Set(plan.Applied, "AdvancedMode", true);
            return plan;
        }

        private static object NoFrames()
        {
            var settings = Client.New("GameFrameGenerationSettings");
            Client.Set(settings, "Route", Client.EnumValue("FrameGenerationRoute", "Disabled"));
            Client.Set(settings, "Output", Client.EnumValue("FrameGenerationOutput", "Auto"));
            Client.Set(settings, "MultiFrameMode", Client.EnumValue("MultiFrameGenerationMode", "Auto"));
            return settings;
        }

        /// <summary>What stays written of a game is what the person chose, and not how it was decided.</summary>
        private static void KeepChosenFrames(object game, FramePlan plan)
        {
            if (plan.Chosen == null || ReferenceEquals(plan.Chosen, plan.Applied))
            {
                return;
            }

            Client.Set(plan.Chosen, "AppliedAtUtc", Client.TryGet(plan.Applied, "AppliedAtUtc"));
            Client.Set(game, "FrameGenerationSettings", plan.Chosen);
            var key = Client.Text(game, "InstallPath");
            if (!Client.CanCall("BackupStoreService", "LoadManifest", "gameDir") || !Client.CanCall("BackupStoreService", "SaveManifest", "gameDir", "manifest", "@InstallationManifest"))
            {
                return;
            }

            var store = Client.New("BackupStoreService");
            var manifest = Client.Call(store, "LoadManifest", "gameDir", key);
            if (manifest != null && Client.Has(manifest, "FrameGenerationRouteApplied"))
            {
                Client.Set(manifest, "FrameGenerationRouteApplied", Client.Text(plan.Chosen, "Route"));
                Client.Set(manifest, "FrameGenerationOutputApplied", Client.Text(plan.Chosen, "Output"));
                Client.Set(manifest, "MfgModeApplied", Client.Text(plan.Chosen, "MultiFrameMode"));
                Client.Call(store, "SaveManifest", "gameDir", key, "manifest", manifest);
            }
        }

        private static JsonArray EnumChoices(string type)
        {
            var found = Client.FindType(type);
            return Service.Array(found == null ? Enumerable.Empty<string>() : Enum.GetNames(found));
        }

        private static JsonArray Versions(object components)
        {
            var beta = new HashSet<string>(Client.Strings(Client.TryGet(components, "BetaVersions")), StringComparer.OrdinalIgnoreCase);
            var nightly = new HashSet<string>(Client.Strings(Client.TryGet(components, "NightlyVersions")), StringComparer.OrdinalIgnoreCase);
            var custom = new HashSet<string>(Client.Strings(Client.TryGet(components, "CustomVersions")), StringComparer.OrdinalIgnoreCase);
            var latest = new[] { "LatestStableVersion", "LatestBetaVersion", "LatestNightlyVersion" }.Select(a => Client.Text(components, a)).ToList();
            var wrappers = Client.CanCall(Components, "IsAmdWrapperVersion", "version");
            var result = new JsonArray();
            foreach (var version in Client.Strings(Client.TryGet(components, "OptiScalerAvailableVersions")))
            {
                if (wrappers && Client.Call(Client.Type(Components), "IsAmdWrapperVersion", "version", version) is bool own && own)
                {
                    continue;
                }

                result.Add(new JsonObject
                {
                    ["id"] = version,
                    ["channel"] = custom.Contains(version) ? "custom" : nightly.Contains(version) ? "nightly" : beta.Contains(version) ? "beta" : "stable",
                    ["latest"] = latest.Contains(version)
                });
            }

            return result;
        }

        private static JsonArray Profiles()
        {
            var result = new JsonArray();
            if (Contract.Has("profile"))
            {
                foreach (var profile in Client.Items(Client.Call(Client.New("ProfileManagementService"), "GetAllProfiles", "forceRefresh", true)))
                {
                    result.Add(new JsonObject { ["id"] = Client.Text(profile, "Name"), ["description"] = Client.Text(profile, "Description") });
                }
            }

            return result;
        }

        private static JsonArray Fsr4Versions(object components)
        {
            var result = new JsonArray();
            if (Contract.Has("fsr4"))
            {
                var variants = Client.CanCall(Components, "GetExtrasDllVariant", "version");
                foreach (var version in Client.Strings(Client.TryGet(components, "ExtrasAvailableVersions")))
                {
                    result.Add(new JsonObject
                    {
                        ["id"] = version,
                        ["variant"] = variants ? Client.Call(components, "GetExtrasDllVariant", "version", version)?.ToString() : null
                    });
                }
            }

            return result;
        }

        public JsonObject State(JsonObject parameters)
        {
            var game = FindOrCreate(parameters, out var known);
            var record = Record.Find(game);
            var folder = FolderOf(game, record);
            var path = Client.Text(game, "InstallPath");
            if ((Guests.Any(folder) || Checkpoint.IsBroken(path)) && !IsHalf(game, record))
            {
                // What was left of the last time without anything to undo, if it isn't that someone is at it now.
                Safety.Alone(TimeSpan.Zero, () =>
                {
                    Checkpoint.Clean(path);
                    Guests.BringBack(folder, record.Files);
                });
            }

            Analyze(game, Switch(parameters, "force"));
            return Describe(game, known, Switch(parameters, "ask") && AreSettingsRight());
        }

        private static bool AreSettingsRight()
        {
            try
            {
                CheckSettings();
                return true;
            }
            catch (ServiceException)
            {
                return false;
            }
        }

        private JsonObject Describe(object game, bool known, bool ask)
        {
            var components = Client.New(Components);
            var config = Client.TryGet(components, "Config");
            var gpu = PreferredGpu(config);
            var compatibility = FindCompatibility(game, ask);
            var defaults = ReadDefaults(components, gpu, compatibility);
            var record = Record.Find(game);
            var installed = Client.Flag(game, "IsOptiscalerInstalled");
            var manifest = installed && record.IsOfOptiScaler ? record.Manifest : null;
            var directory = FolderOf(game, record);
            var byHand = installed && manifest == null;
            var mine = installed ? OwnOf(game, record, directory, gpu) : new List<Setting>();
            var store = new SettingsStore(Client.Text(game, "InstallPath"));
            var waiting = installed ? store.Waiting(record) : new List<Setting>();
            var mineOf = mine.Where(a => OwnSettings.OptionOf(a) != null).GroupBy(OwnSettings.OptionOf).ToDictionary(a => a.Key, a => a.ToList());

            var frames = FrameChoices(Contract.Has("frames") ? TryDetectFrames(game, gpu, directory, record) : null, out var recommendation);
            var result = new JsonObject
            {
                ["known"] = known,
                ["name"] = Client.Text(game, "Name"),
                ["path"] = Client.Text(game, "InstallPath"),
                ["directory"] = directory,
                ["ini"] = installed ? IniPath(directory) : null,
                ["gpu"] = gpu == null ? null : new JsonObject
                {
                    ["name"] = Client.Text(gpu, "Name"),
                    ["vendor"] = Client.Text(gpu, "Vendor"),
                    ["generation"] = Gpu("IsRdna4", gpu) ? "RDNA 4" : Gpu("IsRdna3", gpu) ? "RDNA 3" : Gpu("IsRdna2", gpu) ? "RDNA 2" : null
                },
                ["detected"] = new JsonObject
                {
                    ["dlss"] = Client.Text(game, "DlssVersion"),
                    ["dlssFrames"] = Client.Text(game, "DlssFrameGenVersion"),
                    ["fsr"] = Client.Text(game, "FsrVersion"),
                    ["xess"] = Client.Text(game, "XessVersion"),
                    ["dlssOwn"] = !Client.Flag(game, "DlssViaOptiscaler"),
                    ["fsrOwn"] = !Client.Flag(game, "FsrViaOptiscaler") && !Client.Flag(game, "IsFsr4DllSwapped"),
                    ["xessOwn"] = !Client.Flag(game, "XessViaOptiscaler"),
                    ["antiCheat"] = Client.Flag(game, "HasAntiCheat")
                },
                ["installed"] = installed,
                ["version"] = Client.Text(game, "OptiscalerVersion"),
                ["managed"] = manifest != null,
                ["half"] = IsHalf(game, record),
                ["swapped"] = Client.Flag(game, "IsFsr4DllSwapped"),
                ["neural"] = Client.Flag(game, "IsDlssNrOnAmdInstalled"),
                ["compatibility"] = DescribeCompatibility(compatibility),
                ["list"] = Client.FindType("CompatibilityListService") is Type list ? Client.Text(list, "WikiUrl") : null,
                ["recommendation"] = recommendation,
                ["choices"] = new JsonObject
                {
                    ["versions"] = Versions(components),
                    ["injections"] = Service.Array(InjectionNames()),
                    ["profiles"] = Profiles(),
                    ["spoofing"] = Service.Array(Contract.Has("spoofing") ? new[] { Auto, "true", "false" } : new string[0]),
                    ["outputs"] = Contract.Has("output") ? EnumChoices("OutputUpscalerBackend") : new JsonArray(),
                    ["qualities"] = Contract.Has("quality") ? EnumChoices("UpscalingQualityPreset") : new JsonArray(),
                    ["frames"] = frames,
                    ["fsr4"] = Fsr4Versions(components),
                    ["patchers"] = Service.Array(Contract.Has("patcher") ? Client.Strings(Client.TryGet(components, "OptiPatcherAvailableVersions")) : new List<string>())
                }
            };

            // What the client remembers of a game isn't of the OptiScaler that somebody put by hand after.
            var settings = byHand ? null : Client.TryGet(game, "FrameGenerationSettings");
            var applied = Client.Text(manifest, "FrameGenerationRouteApplied");
            var chosenFrames = applied != null ?
                new JsonObject
                {
                    ["route"] = applied,
                    ["output"] = Client.Text(manifest, "FrameGenerationOutputApplied"),
                    ["mode"] = Client.Text(manifest, "MfgModeApplied"),
                    ["advanced"] = Client.Flag(settings, "AdvancedMode"),
                    ["replacement"] = Client.Text(settings, "NvngxReplacement")
                } :
                Frames(installed ? settings : Client.TryGet(config, "DefaultFrameGenerationSettings"));
            if (mineOf.ContainsKey("frames"))
            {
                chosenFrames = chosenFrames ?? new JsonObject();
                chosenFrames[OwnSettings.Own] = true;
            }

            result["chosen"] = new JsonObject
            {
                ["version"] = installed && !string.IsNullOrEmpty(Client.Text(game, "OptiscalerVersion")) ? Client.Text(game, "OptiscalerVersion") : defaults.Version,
                ["injection"] = Client.Text(manifest, "InjectionMethod") is string injection && injection.Length > 0 ? injection :
                    (installed ? FindByHand(directory) : null) ?? defaults.Injection,
                ["profile"] = Client.Text(manifest, "AppliedProfileName") ?? defaults.Profile,
                ["spoofing"] = mineOf.ContainsKey("spoofing") ? OwnSettings.Own : (installed ? ReadIni(directory, "Spoofing", "Dxgi") : null) ?? defaults.Spoofing,
                ["output"] = mineOf.ContainsKey("output") ? OwnSettings.Own : !installed ? defaults.Output : byHand ? "Default" :
                    Client.Text(manifest, "OutputUpscalerBackendApplied") ?? Client.Text(Client.TryGet(game, "OutputUpscalerSettings"), "Backend") ?? "Default",
                ["quality"] = mineOf.ContainsKey("quality") ? OwnSettings.Own : !installed ? defaults.Quality : byHand ? "GameControlled" :
                    Client.Text(manifest, "UpscalingQualityPresetApplied") ?? Client.Text(Client.TryGet(game, "UpscalingQualitySettings"), "Preset") ?? "GameControlled",
                ["frames"] = chosenFrames,
                ["fsr4"] = installed || Client.Flag(game, "IsFsr4DllSwapped") ? Client.Text(game, "Fsr4ExtraVersion") ?? None : defaults.Fsr4,
                ["patcher"] = installed ? (HasPlugin(directory, "OptiPatcher.asi") ? "installed" : None) : defaults.Patcher
            };
            result["own"] = new JsonObject
            {
                ["count"] = mine.Count + waiting.Count,
                ["waiting"] = Service.Array(waiting.Select(OwnSettings.OptionOf).Where(a => a != null).Distinct()),
                ["options"] = new JsonObject(mineOf.Select(a => KeyValuePair.Create(a.Key, (JsonNode)JsonValue.Create(OwnSettings.Summarize(a.Key, a.Value, IniFile.Read(IniPath(directory))))))),
                ["copies"] = store.Copies
            };

            // Nobody wrote them down: whoever takes them out has to say yes to them.
            result["beside"] = Service.Array(byHand ?
                FilesByHand(directory, null).Select(a => Path.GetRelativePath(directory, a)).Where(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).
                    Where(a => !a.Contains('\\') && !ByHand.IsCertain(a) && !IsMain(Path.Combine(directory, a))) :
                Enumerable.Empty<string>());
            result["defaults"] = new JsonObject
            {
                ["version"] = defaults.Version,
                ["injection"] = defaults.Injection,
                ["profile"] = defaults.Profile,
                ["fsr4"] = defaults.Fsr4,
                ["patcher"] = defaults.Patcher
            };
            return result;
        }

        private static JsonObject DescribeCompatibility(Compatibility compatibility)
        {
            if (compatibility.Entry == null)
            {
                return null;
            }

            var page = Client.Text(compatibility.Details, "PageUrl");
            return new JsonObject
            {
                ["name"] = Client.Text(compatibility.Entry, "GameName"),
                ["status"] = Client.Text(compatibility.Entry, "Status"),
                ["inputs"] = Client.Text(compatibility.Entry, "UpscalerInputs"),
                ["notes"] = Client.Text(compatibility.Entry, "Notes"),
                ["patcher"] = Client.Flag(compatibility.Entry, "OptiPatcherSupported"),
                ["filename"] = Client.Text(compatibility.Details, "Filename"),
                ["frames"] = Client.Text(compatibility.Details, "FgInputs"),
                ["issues"] = Client.TryGet(compatibility.Details, "KnownIssuesCount") is int issues ? issues : 0,
                ["url"] = string.IsNullOrEmpty(page) ? Client.Text(Client.Type("CompatibilityListService"), "WikiUrl") : page
            };
        }

        /// <summary>Asks GitHub for the versions there are, with the pauses the client already keeps.</summary>
        public JsonObject Refresh()
        {
            if (!Contract.Has("refresh"))
            {
                return new JsonObject { ["asked"] = false };
            }

            CheckSettings();
            var components = Client.New(Components);
            Client.Call(components, "CheckForUpdatesAsync");
            return new JsonObject { ["asked"] = true, ["latest"] = Client.Text(components, "LatestStableVersion") };
        }

        /// <summary>The client starts with its own settings when it can't read the ones there are, and looking for versions saves them.</summary>
        private static void CheckSettings()
        {
            var folder = Contract.DataFolder();
            var path = string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, "config.json");
            if (path == null || !File.Exists(path))
            {
                return;
            }

            try
            {
                JsonNode.Parse(File.ReadAllText(path));
            }
            catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
            {
                throw new ServiceException("settings", $"The settings of OptiScaler Client can't be read: {e.Message}");
            }
        }

        private static bool HasFiles(string folder)
        {
            return !string.IsNullOrEmpty(folder) && Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any();
        }

        /// <summary>From the 0.9 on, OptiScaler brings inside the pieces that used to be installed next to it.</summary>
        private static bool BringsPieces(string version)
        {
            var match = Regex.Match(version ?? string.Empty, @"^v?(\d+(?:\.\d+)*)");
            return match.Success && Version.TryParse(match.Groups[1].Value.Contains(".") ? match.Groups[1].Value : match.Groups[1].Value + ".0", out var parsed) &&
                (parsed.Major > 0 || parsed.Minor >= 9);
        }

        private static string Choice(JsonObject options, string name, string otherwise)
        {
            return Parameter(options, name) is string chosen && chosen.Length > 0 ? chosen : otherwise;
        }

        /// <summary>The settings of frame generation the person asked for, put into the game. False if there was nothing to change.</summary>
        private static bool SetFrames(object game, JsonObject options)
        {
            if (!Contract.Has("frames") || !(options?["frames"] is JsonObject frames))
            {
                return false;
            }

            var output = Parameter(frames, "output");
            var mode = Parameter(frames, "mode");
            if (output == OwnSettings.Own)
            {
                // The client writes nothing about it: what goes in is what the person has.
                Client.Set(game, "FrameGenerationSettings", null);
                return true;
            }

            var off = output == "Disabled" || string.IsNullOrEmpty(output);
            var optiFg = output == OptiFg && HasOptiFg;
            var settings = Client.New("GameFrameGenerationSettings");
            foreach (var name in new[] { "StreamlineVersion", "DlssEnablerVersion" })
            {
                // Which one of each is chosen in the window of the client: it stays.
                if (Client.TryGet(Client.TryGet(game, "FrameGenerationSettings"), name) is string chosen)
                {
                    Client.Set(settings, name, chosen);
                }
            }

            Client.Set(settings, "Route", Client.EnumValue("FrameGenerationRoute", off ? "Disabled" : optiFg ? OptiFg : "Auto"));
            Client.Set(settings, "Output", Client.EnumValue("FrameGenerationOutput", off ? "Auto" : optiFg ? "FsrFg" : output) ?? Client.EnumValue("FrameGenerationOutput", "Auto"));
            Client.Set(settings, "MultiFrameMode", Client.EnumValue("MultiFrameGenerationMode", off ? "Auto" : mode) ?? Client.EnumValue("MultiFrameGenerationMode", "Auto"));
            if (optiFg)
            {
                // The client has it among its advanced ones.
                Client.Set(settings, "AdvancedMode", true);
            }

            Client.Set(game, "FrameGenerationSettings", settings);
            return true;
        }

        private static void SetQuality(object game, string preset)
        {
            var value = Client.EnumValue("UpscalingQualityPreset", preset);
            if (value != null || preset == OwnSettings.Own)
            {
                var settings = value == null ? null : Client.New("GameUpscalingQualitySettings");
                if (settings != null)
                {
                    Client.Set(settings, "Preset", value);
                }

                Client.Set(game, "UpscalingQualitySettings", settings);
            }
        }

        private static void SetOutput(object game, string backend)
        {
            var value = Client.EnumValue("OutputUpscalerBackend", backend);
            if (value != null || backend == OwnSettings.Own)
            {
                var settings = value == null ? null : Client.New("GameOutputUpscalerSettings");
                if (settings != null)
                {
                    Client.Set(settings, "Backend", value);
                }

                Client.Set(game, "OutputUpscalerSettings", settings);
            }
        }

        /// <summary>
        /// Whether the client accepts the frame generation chosen for this game. It's asked before: inside an install it
        /// would make the whole install fail.
        /// </summary>
        private static bool FramesAccepted(FramePlan plan, string version)
        {
            if (plan.Applied == null || Client.Text(plan.Applied, "Route") == "Disabled")
            {
                return true;
            }

            try
            {
                if (Client.CanCall(FrameService, "BuildIniSettings", "settings", "c", "optiscalerVersion", "@GameFrameGenerationSettings", "@FrameGenerationCapabilities"))
                {
                    Client.Call(Client.New(FrameService), "BuildIniSettings", "settings", plan.Applied, "c", plan.Capabilities, "optiscalerVersion", version);
                }

                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static FramePlan FramesOff(FramePlan plan)
        {
            var settings = NoFrames();
            return new FramePlan { Chosen = settings, Applied = settings, Capabilities = plan.Capabilities };
        }

        private static void Wait(Func<bool> busy)
        {
            for (var i = 0; i < 600 && busy(); i++)
            {
                System.Threading.Thread.Sleep(500);
            }
        }

        public JsonObject Install(JsonObject parameters)
        {
            touched = false;
            var options = parameters["options"] as JsonObject ?? new JsonObject();
            var game = FindOrCreate(parameters, out _);
            progress("look", null, null);
            CheckSettings();
            var notes = new List<string>();
            var left = Recover(game, "install");
            if (left?.Count > 0)
            {
                notes.Add($"left: {string.Join(", ", left)}");
            }

            var record = Record.Find(game);
            var target = FolderOf(game, record);
            Guests.BringBack(target, record.Files);
            Analyze(game, true);
            if (Client.Flag(game, "IsDlssNrOnAmdInstalled"))
            {
                throw new ServiceException("neural", "The game has the neural rendering mod: its window in OptiScaler Client takes care of both.");
            }

            var components = Client.New(Components);
            var config = Client.TryGet(components, "Config");
            var gpu = PreferredGpu(config);
            var compatibility = FindCompatibility(game, true);
            var defaults = ReadDefaults(components, gpu, compatibility);
            var version = Choice(options, "version", defaults.Version);
            if (string.IsNullOrEmpty(version))
            {
                // Nothing known yet, which is how a first time starts: GitHub is asked.
                Refresh();
                components = Client.New(Components);
                defaults = ReadDefaults(components, gpu, compatibility);
                version = defaults.Version;
                if (string.IsNullOrEmpty(version))
                {
                    throw new ServiceException("noversions", "No version of OptiScaler is known.");
                }
            }

            var service = Client.New(Installer);
            CheckFree(target, record);
            if (!Client.Flag(game, "IsOptiscalerInstalled"))
            {
                CheckLeftovers(game, record, target, Switch(options, "clean"), notes);
            }

            var nightly = Client.CanCall(Components, "IsNightlyOptiScalerVersion", "version") &&
                Client.Call(components, "IsNightlyOptiScalerVersion", "version", version) is bool isNightly && isNightly;

            // What the person changed inside the game goes along, unless they said otherwise here.
            var byHand = record.IsOfOptiScaler ? null : FindByHand(target);
            var there = record.IsOfOptiScaler || byHand != null || (Client.Flag(game, "IsOptiscalerInstalled") && IniPath(target) != null);
            var key = Client.Text(game, "InstallPath");
            var kept = new SettingsStore(key);
            var fresh = Parameter(options, "settings") == "fresh";
            bool stays(Setting setting) => OwnSettings.OptionOf(setting) is string option ? IsOwn(options, option, fresh) : !fresh;
            IniFile was = null;
            List<Setting> own = null;
            List<Setting> waiting = null;
            void look()
            {
                was = IniFile.Read(IniPath(target));
                own = OwnSettings.Whole(there ? OwnOf(game, record, target, gpu).Where(stays) : Enumerable.Empty<Setting>(), was);
                waiting = kept.Waiting(record).Where(stays).ToList();
            }

            look();
            var enabler = Client.Text(Client.TryGet(game, "FrameGenerationSettings"), "DlssEnablerVersion");
            if (there && !record.IsOfOptiScaler)
            {
                // What the client remembers of the game is of another one that was there before.
                SetQuality(game, OwnSettings.Own);
                SetOutput(game, OwnSettings.Own);
                Client.Set(game, "FrameGenerationSettings", null);
            }

            // What the game keeps from before stays as it is, unless the person chose something else.
            if (!SetFrames(game, options) && own.All(a => OwnSettings.OptionOf(a) != "frames") && Client.TryGet(game, "FrameGenerationSettings") is null)
            {
                CopyDefaultFrames(game, config);
            }

            if (Contract.Has("quality") && Parameter(options, "quality") is string quality)
            {
                SetQuality(game, quality);
            }

            if (Contract.Has("output") && Parameter(options, "output") is string output)
            {
                SetOutput(game, output);
            }

            var frames = PlanFrames(game, gpu, target, record);
            if (Contract.Has("frames") && !FramesAccepted(frames, version))
            {
                frames = FramesOff(frames);
                notes.Add("frames");
            }
            else if (frames.IsNothing)
            {
                notes.Add("framesnone");
            }

            Client.Set(game, "FrameGenerationSettings", frames.Applied);

            // What isn't told goes on as it was.
            var before = record.IsOfOptiScaler ? Client.Text(record.Manifest, "InjectionMethod") : byHand;
            var injection = Choice(options, "injection", !string.IsNullOrEmpty(before) && InjectionNames().Contains(before, StringComparer.OrdinalIgnoreCase) ? before : defaults.Injection);
            var spoofing = Choice(options, "spoofing", (there ? was?.Get("Spoofing", "Dxgi") : null) ?? defaults.Spoofing);
            if (spoofing == OwnSettings.Own || own.Any(a => OwnSettings.OptionOf(a) == "spoofing"))
            {
                // The client writes the same in the four: the ones of the person go in after it.
                spoofing = Auto;
            }

            object profile = null;
            if (Contract.Has("profile"))
            {
                var name = Choice(options, "profile", (record.IsOfOptiScaler ? Client.Text(record.Manifest, "AppliedProfileName") : null) ?? defaults.Profile);
                profile = string.IsNullOrEmpty(name) ? null : Client.Call(Client.New("ProfileManagementService"), "GetProfileByName", "name", name);
            }

            string folder = null;
            string package = null;
            Guests guests = null;
            Extras extras = null;
            try
            {
                for (var attempt = 0; folder == null; attempt++)
                {
                    var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["game"] = game,
                        ["injectionDllName"] = injection,
                        ["optiscalerVersion"] = version,
                        ["overrideGameDir"] = target,
                        ["profile"] = profile,
                        ["isRdna4"] = Gpu("IsRdna4", gpu),
                        ["isRdna2"] = Gpu("IsRdna2", gpu),
                        ["gpu"] = gpu,
                        ["dxgiSpoofing"] = spoofing
                    };
                    package = Download(components, version);
                    values["cachePath"] = package;
                    var ownFrames = Fitting(package, was, own, waiting).Values.Where(a => a != null && OwnSettings.OptionOf(a) == "frames").ToList();
                    AddPieces(values, components, config, target, record, version, nightly, frames, ownFrames, enabler);
                    extras = extras ?? FetchExtras(game, components, options, defaults, record, target, ownFrames, notes);

                    // The game may have been opened while it downloaded, and even played.
                    CheckFree(target, record);
                    look();
                    progress("install", version, null);
                    try
                    {
                        var old = byHand == null ? new List<string>() : FilesByHand(target, package);
                        guests = new Guests(target, () => OwnFiles(game, target).Concat(old.Select(a => Path.GetRelativePath(target, a))), package);
                        var plan = GetPlan(target, record, values);
                        old.Select(a => Path.GetRelativePath(target, a)).Concat(iniNames).ToList().ForEach(a => plan.Add(a));
                        using (var checkpoint = Checkpoint.Begin(key, key, target, plan))
                        {
                            checkpoint.Unlock(target, plan);
                            if (attempt == 0)
                            {
                                CopySettings(game, record, target, notes);
                            }

                            if (old.Count > 0)
                            {
                                // They aren't of the game: they would come back when OptiScaler goes out.
                                RemoveByHand(checkpoint, target, old);
                            }

                            // Otherwise the client puts all the settings there were over the ones of the new version.
                            IniPaths(target).ToList().ForEach(File.Delete);
                            folder = CallInstaller(game, "InstallOptiScaler", values) as string ?? target;
                            checkpoint.CheckOriginals();

                            // From here on they are only here, until they go in: if it's cut before, they wait for the next install.
                            Try(notes, "settings", () => kept.Keep(Record.Find(game), IniFile.Read(IniPath(folder)), Together(own, waiting).Where(a => !Setting.IsAuto(a.Value)), false));
                            checkpoint.End();
                        }
                    }
                    catch (Exception e) when (attempt == 0 && IsBrokenDownload(e) && !Client.Strings(Client.TryGet(components, "CustomVersions")).Contains(version))
                    {
                        // Something of the download isn't whole: it's downloaded again, once.
                        guests?.Dispose();
                        guests = null;
                        Discard(values);
                    }
                }

                AddExtras(game, components, config, gpu, folder, extras, spoofing, notes);

                // The install writes them in the file of OptiScaler, but only doing it again leaves them in the record of the game.
                Try(notes, "settings", () => ApplyChosen(game, gpu, folder, version));
            }
            finally
            {
                guests?.Dispose();
            }

            progress("check", null, null);
            if (extras.Fsr4 != None && Client.Text(game, "Fsr4ExtraVersion") == extras.Fsr4)
            {
                // A file of the person that it depends on was aside until now.
                Try(notes, "fsr4", () => ConfigureFsr4(components, gpu, folder, extras.Fsr4));
            }

            Client.Set(game, "IsOptiscalerInstalled", true);
            Client.Set(game, "OptiscalerVersion", version);
            Try(notes, "ini", () =>
            {
                if (!IniIsRight(folder))
                {
                    notes.Add("ini");
                }
            });

            // From here on the client doesn't write in them: what it checks is only what it wrote.
            Try(notes, "settings", () => KeepChosenFrames(game, frames));
            Try(notes, "settings", () =>
            {
                PlaceHudFix(folder);
                KeepSettings(game, folder, package, was, own, waiting, notes);
            });
            if (Guests.Any(target))
            {
                notes.Add("guests");
            }

            Save(game, true);
            Analyze(game, true);
            var result = Describe(game, true, false);
            result["notes"] = Service.Array(notes);
            return result;
        }

        /// <summary>What goes with OptiScaler. What couldn't be downloaded is as if it wasn't chosen, but it's told.</summary>
        private sealed class Extras
        {
            public bool WantsFsr4;
            public string Fsr4 = None;
            public object Patcher;
            public object Frames;
        }

        /// <summary>They are downloaded before the game is touched: what is of others in its folder is aside only while it's copied.</summary>
        private Extras FetchExtras(object game, object components, JsonObject options, Defaults defaults, Record record, string folder, List<Setting> ownFrames, List<string> notes)
        {
            var extras = new Extras();
            var fsr4 = Choice(options, "fsr4", record.IsOfOptiScaler ? Client.Text(game, "Fsr4ExtraVersion") ?? None : defaults.Fsr4);
            extras.WantsFsr4 = fsr4 != None;
            if (extras.WantsFsr4 && Contract.Has("fsr4"))
            {
                Try(notes, "fsr4", () =>
                {
                    var reporter = new Reporter(a => progress("download", "FSR 4", a));
                    Client.Call(components, "DownloadExtrasDllAsync", "version", fsr4, "progress", reporter);
                    Client.Call(components, "GetExtrasPackagedFileNamesAsync", "version", fsr4, "progress", reporter);
                    extras.Fsr4 = fsr4;
                });
            }

            var patcher = Choice(options, "patcher", record.IsOfOptiScaler ? (HasPlugin(folder, "OptiPatcher.asi") ? "installed" : None) : defaults.Patcher);
            if (patcher == "installed")
            {
                patcher = Client.Text(components, "LatestOptiPatcherVersion") ?? None;
            }

            if (patcher != None && Contract.Has("patcher"))
            {
                Try(notes, "patcher", () => extras.Patcher = Client.Call(
                    components, "DownloadOptiPatcherAsync", "version", patcher, "progress", new Reporter(a => progress("download", "OptiPatcher", a))));
            }

            var settings = Client.TryGet(game, "FrameGenerationSettings");
            var xeFg = (Client.Text(settings, "Output") == "XeFg" && Client.Text(settings, "Route") != "Disabled") || (ownFrames.Count > 0 && HasPlugin(folder, "XeFGUnlock.asi"));
            if (xeFg && Client.CanCall(Components, "DownloadXeFGUnlockAsync", "version", "progress"))
            {
                Try(notes, "xefg", () => extras.Frames = Client.Call(
                    components, "DownloadXeFGUnlockAsync", "version", Client.Text(components, "LatestXeFGUnlockVersion") ?? string.Empty,
                    "progress", new Reporter(a => progress("download", "XeFG", a))));
            }

            return extras;
        }

        /// <summary>Once OptiScaler is in its place. If something of this can't be, it's told and the rest goes on.</summary>
        private void AddExtras(object game, object components, object config, object gpu, string folder, Extras extras, string spoofing, List<string> notes)
        {
            if (extras.Fsr4 != None)
            {
                Try(notes, "fsr4", () => InstallFsr4(game, components, config, gpu, folder, extras.Fsr4));
            }
            else if (!extras.WantsFsr4)
            {
                Client.Set(game, "Fsr4ExtraVersion", null);
            }

            if (extras.Patcher != null)
            {
                Try(notes, "patcher", () =>
                {
                    Client.Call(Client.New(Installer), "InstallAsiPlugin", "gameDir", folder, "asiSourcePath", extras.Patcher);

                    // With the patch in place, the client decides again about spoofing.
                    Client.Call(Client.New(Installer), "ApplySpoofingSettings", "game", game, "spoofingValue", spoofing, "resolvedGameDir", folder);
                });
            }

            if (extras.Frames != null)
            {
                Try(notes, "xefg", () => Client.Call(Client.New(Installer), "InstallAsiPlugin", "gameDir", folder, "asiSourcePath", extras.Frames));
            }
        }

        private static void ApplyChosen(object game, object gpu, string folder, string version)
        {
            var service = Client.New(Installer);
            if (Contract.Has("frames") && Client.TryGet(game, "FrameGenerationSettings") != null)
            {
                Client.Call(service, "ApplyFrameGenerationSettings", "game", game, "resolvedGameDir", folder, "gpu", gpu, "optiscalerVersion", version);
            }

            if (Contract.Has("quality") && Client.TryGet(game, "UpscalingQualitySettings") != null)
            {
                Client.Call(service, "ApplyUpscalingQualitySettings", "game", game, "resolvedGameDir", folder);
            }

            if (Contract.Has("output") && Client.TryGet(game, "OutputUpscalerSettings") != null)
            {
                Client.Call(service, "ApplyOutputUpscalerSettings", "game", game, "resolvedGameDir", folder);
            }
        }

        private static void Try(List<string> notes, string note, Action action)
        {
            try
            {
                action();
            }
            catch (Exception e) when (!(e is OutOfMemoryException) && !(e is StoppedException))
            {
                // OptiScaler itself goes on: the client says it the same way, as a warning.
                notes.Add($"{note}: {e.Message}");
            }
        }

        private static bool IniIsRight(string folder)
        {
            if (!Client.CanCall(Installer, "VerifyIniSettings", "gameDir"))
            {
                return true;
            }

            var service = Client.New(Installer);
            if (Client.Strings(Client.Call(service, "VerifyIniSettings", "gameDir", folder)).Count == 0)
            {
                return true;
            }

            return Client.CanCall(Installer, "ReapplyIniSettings", "gameDir") &&
                Client.Strings(Client.Call(service, "ReapplyIniSettings", "gameDir", folder)).Count == 0;
        }

        private void CheckLeftovers(object game, Record record, string folder, bool clean, List<string> notes)
        {
            if (string.IsNullOrEmpty(folder) || !Client.CanCall(Installer, "HasCorruptArtifacts", "gameDir") ||
                !(Client.Call(Client.Type(Installer), "HasCorruptArtifacts", "gameDir", folder) is bool leftovers) || !leftovers)
            {
                return;
            }

            if (!clean)
            {
                throw new ServiceException("leftovers", "There are files of an earlier OptiScaler in the folder of the game.");
            }

            progress("install", null, null);
            CopySettings(game, record, folder, notes);
            using (new Guests(folder, Enumerable.Empty<string>, null, true))
            {
                CallInstaller(game, "ForceFolderCleanup", new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["game"] = game });
            }
        }

        private static void CopyDefaultFrames(object game, object config)
        {
            var original = Client.TryGet(config, "DefaultFrameGenerationSettings");
            if (original == null || Client.FindType("GameFrameGenerationSettings") == null)
            {
                return;
            }

            // A copy: the client writes on it when it applies it.
            Client.Set(game, "FrameGenerationSettings", CopyFrames(original));
        }

        private string Download(object components, string version)
        {
            if (Client.CanCall(Components, "IsOptiScalerDownloadActive", "version"))
            {
                Wait(() => Client.Call(Client.Type(Components), "IsOptiScalerDownloadActive", "version", version) is bool active && active);
            }

            var path = Client.Call(
                components, "DownloadOptiScalerAsync", "version", version, "progress", new Reporter(a => progress(a < 0 ? "extract" : "download", $"OptiScaler {version}", a < 0 ? (double?)null : a))) as string;
            return path ?? Client.Call(components, "GetOptiScalerCachePath", "version", version) as string;
        }

        /// <summary>What goes next to OptiScaler depending on its version and on the frame generation of the game.</summary>
        private void AddPieces(
            Dictionary<string, object> values, object components, object config, string folder, Record record, string version, bool nightly, FramePlan plan, List<Setting> ownFrames, string enabler)
        {
            var frames = plan.Applied;
            var active = frames != null && Client.Text(frames, "Route") != "Disabled";
            var asked = active && Client.CanCall(FrameService, "RequiresStreamline", "settings", "capabilities", "optiscalerVersion", "@GameFrameGenerationSettings", "@FrameGenerationCapabilities") &&
                Client.Call(Client.New(FrameService), "RequiresStreamline", "settings", frames, "capabilities", plan.Capabilities, "optiscalerVersion", version) is bool needed && needed;

            // What the person chose inside the game goes on having what it needs.
            bool uses(string name, params string[] chosen) => ownFrames.Any(a => a.Key.Equals(name, StringComparison.OrdinalIgnoreCase) && chosen.Contains(a.Value, StringComparer.OrdinalIgnoreCase));
            if (asked || uses("FGOutput", "dlssg", "dlssgwithnvngx"))
            {
                var streamline = Client.Text(frames, "StreamlineVersion");
                progress("download", "Streamline", null);
                values["installStreamline"] = true;
                values["streamlineCachePath"] = Client.Call(
                    components, "DownloadStreamlineAsync", "version", string.IsNullOrEmpty(streamline) ? Client.Text(components, "LatestStreamlineVersion") ?? string.Empty : streamline,
                    "progress", new Reporter(a => progress("download", "Streamline", a)));
            }

            if (active && Client.Text(frames, "Output") == "DlssG" && Client.Text(frames, "NvngxReplacement") is string replacement &&
                (replacement == "Arturs" || replacement == "Combo"))
            {
                AddEnabler(values, components, Client.Text(frames, "DlssEnablerVersion"));
            }
            else if (uses("FGNvngxReplacement", "Arturs", "Combo") && !string.IsNullOrEmpty(enabler))
            {
                AddEnabler(values, components, enabler);
            }

            var bundled = BringsPieces(version);
            if (nightly)
            {
                // The one there is goes away with the OptiScaler it came with.
                var fake = string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, "fakenvapi.dll");
                var stays = fake != null && File.Exists(fake) && record.IsOfOptiScaler && !record.Files.Contains("fakenvapi.dll", StringComparer.OrdinalIgnoreCase);
                if (!stays && Mentions(values["cachePath"] as string, "fakenvapi.dll"))
                {
                    progress("download", "Fakenvapi", null);
                    values["installFakenvapi"] = true;
                    values["fakenvapiCachePath"] = Client.Call(components, "DownloadLatestFakenvapiAsync", "progress", new Reporter(a => progress("download", "Fakenvapi", a)));
                }
            }
            else if (!bundled)
            {
                AddOldPieces(values, components, config);
            }
        }

        /// <summary>Whether OptiScaler itself looks for a file. It stopped looking for some when it took them inside.</summary>
        private static bool Mentions(string package, string name)
        {
            var main = Plan.Files(package).FirstOrDefault(a => Path.GetFileName(a).Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase)) ??
                Plan.Files(package).FirstOrDefault(a => MainNames().Contains(Path.GetFileName(a), StringComparer.OrdinalIgnoreCase));
            if (main == null)
            {
                return true;
            }

            ReadOnlySpan<byte> content = File.ReadAllBytes(main);
            return content.IndexOf(System.Text.Encoding.ASCII.GetBytes(name)) >= 0 || content.IndexOf(System.Text.Encoding.Unicode.GetBytes(name)) >= 0;
        }

        private void AddEnabler(Dictionary<string, object> values, object components, string version)
        {
            if (string.IsNullOrEmpty(version))
            {
                throw new ServiceException("enabler", "No version of DLSS Enabler is chosen for this game.");
            }

            var mirror = Client.CanCall(Components, "IsDlssEnablerMirrorTag", "tag") &&
                Client.Call(Client.Type(Components), "IsDlssEnablerMirrorTag", "tag", version) is bool isMirror && isMirror;
            values["installDlssEnabler"] = true;
            if (!mirror)
            {
                values["dlssEnablerCachePath"] = Client.Call(components, "GetDlssEnablerCachePath", "name", version);
                return;
            }

            var number = Client.Call(Client.Type(Components), "StripDlssEnablerMirrorTag", "tag", version) as string;
            values["dlssEnablerCachePath"] = Client.Call(components, "IsDlssEnablerMirrorCached", "version", number) is bool cached && cached ?
                Client.Call(components, "GetDlssEnablerMirrorCachePath", "version", number) :
                Client.Call(components, "DownloadDlssEnablerMirrorAsync", "version", number, "progress", new Reporter(a => progress("download", "DLSS Enabler", a)));
        }

        /// <summary>The versions before the 0.9 need two pieces next to them, the ones chosen in the client.</summary>
        private void AddOldPieces(Dictionary<string, object> values, object components, object config)
        {
            var fake = Client.Text(config, "DefaultFakenvapiVersion");
            if (fake == "__latest__" && Client.CanCall(Components, "ResolveLatestFakenvapiVersionAsync"))
            {
                fake = Client.Call(components, "ResolveLatestFakenvapiVersionAsync") as string;
            }

            if (!string.IsNullOrEmpty(fake) && !fake.Equals(None, StringComparison.OrdinalIgnoreCase))
            {
                values["installFakenvapi"] = true;
                values["fakenvapiCachePath"] = Client.Call(components, "IsFakenvapiCached", "version", fake) is bool cached && cached ?
                    Client.Call(components, "GetFakenvapiCachePath", "version", fake) :
                    Client.Call(components, "DownloadFakenvapiAsync", "version", fake, "progress", new Reporter(a => progress("download", "Fakenvapi", a)));
            }

            var nukem = Client.Text(config, "DefaultNukemFGVersion");
            if (nukem == "__latest__")
            {
                nukem = Client.Strings(Client.Call(components, "GetDownloadedNukemFGVersions")).FirstOrDefault();
            }

            if (!string.IsNullOrEmpty(nukem) && !nukem.Equals(None, StringComparison.OrdinalIgnoreCase))
            {
                var folder = Client.Call(components, "GetNukemFGCachePath", "version", nukem) as string;
                if (HasFiles(folder))
                {
                    values["installNukemFG"] = true;
                    values["nukemFGCachePath"] = folder;
                }
            }
        }

        private static bool IsBrokenDownload(Exception exception)
        {
            return exception.Message.Contains("corrupt or incomplete", StringComparison.OrdinalIgnoreCase) ||
                exception.Message.Contains("not found in the downloaded package", StringComparison.OrdinalIgnoreCase);
        }

        private static void Discard(Dictionary<string, object> values)
        {
            // Only what the client downloads by itself: a file the person imported can't be downloaded again.
            foreach (var key in new[] { "cachePath", "fakenvapiCachePath" })
            {
                try
                {
                    if (values.TryGetValue(key, out var folder) && folder is string path && Directory.Exists(path))
                    {
                        Directory.Delete(path, true);
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                }
            }
        }

        private void InstallFsr4(object game, object components, object config, object gpu, string folder, string version)
        {
            if (!Contract.Has("fsr4"))
            {
                return;
            }

            var reporter = new Reporter(a => progress("download", "FSR 4", a));
            var file = Client.Call(components, "DownloadExtrasDllAsync", "version", version, "progress", reporter) as string;
            var packaged = Client.Call(components, "GetExtrasPackagedFileNamesAsync", "version", version, "progress", reporter);
            var root = Client.CanCall(Installer, "ResolveExtrasRoot", "gameDir") ?
                Client.Call(Client.Type(Installer), "ResolveExtrasRoot", "gameDir", folder) as string ?? folder : folder;
            var files = new List<Tuple<string, string>>();
            if (Client.Strings(packaged).Count > 0)
            {
                var cache = Client.Call(components, "GetExtrasDllCachePath", "version", version) as string;
                cache = File.Exists(cache) ? Path.GetDirectoryName(cache) : cache;
                var candidates = Client.Call(Client.Type("Fsr4Int8DllHelper"), "BuildSwapCandidates", "gameDir", root, "cacheDir", cache, "packagedFiles", packaged);
                var keys = Client.TryGet(config, "Fsr4SwapDefaultFileKeys");
                if (!Client.Flag(config, "Fsr4SwapAskEveryTime") && Client.Items(candidates).Count > 1 &&
                    Client.CanCall("Fsr4Int8DllHelper", "FilterCandidatesByDefaultKeys", "candidates", "allowedKeys"))
                {
                    candidates = Client.Call(Client.Type("Fsr4Int8DllHelper"), "FilterCandidatesByDefaultKeys", "candidates", candidates, "allowedKeys", keys);
                }

                files.AddRange(Client.Items(candidates).Select(a => Tuple.Create((string)Client.Get(a, "Item1"), (string)Client.Get(a, "Item2"))));
            }
            else if (!string.IsNullOrEmpty(file))
            {
                files.Add(Tuple.Create(Path.Combine(folder, Path.GetFileName(file)), file));
            }

            var service = Client.New(Installer);
            var companion = IsInt8(components, version) && Client.CanCall(Components, "GetCachedCustomAmdxc64Path", "version") ?
                Client.Call(components, "GetCachedCustomAmdxc64Path", "version", version) as string : null;
            var plan = new Plan();
            foreach (var pair in files)
            {
                if (!File.Exists(pair.Item2))
                {
                    throw new FileNotFoundException("The package of FSR 4 isn't whole.", pair.Item2);
                }

                // The one of the game with the same name is replaced too.
                plan.Add(Path.GetRelativePath(folder, pair.Item1), pair.Item2);
                plan.Add(Path.GetFileName(pair.Item1), pair.Item2);
            }

            foreach (var name in new[] { "OptiScaler.ini", companion == null ? null : Path.GetFileName(companion), companion == null ? null : "amdxc64.dll" }.Where(a => a != null))
            {
                plan.Add(name, name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? companion : null);
                plan.Add(Path.Combine("OptiScaler", name), name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? companion : null);
            }

            var key = Client.Text(game, "InstallPath");
            using (var checkpoint = Checkpoint.Begin(key, key, folder, plan))
            {
                checkpoint.Unlock(folder, plan);
                foreach (var pair in files)
                {
                    Client.Call(service, "InjectExtrasDll", "game", game, "gameDir", folder, "destPath", pair.Item1, "sourceContentPath", pair.Item2);
                }

                if (companion != null)
                {
                    Client.Call(service, "InstallCustomAmdxc64", "gameDir", folder, "sourcePath", companion);
                }

                ConfigureFsr4(components, gpu, folder, version);
                checkpoint.End();
            }

            Client.Set(game, "Fsr4ExtraVersion", version);
        }

        private static bool IsInt8(object components, string version)
        {
            return Client.CanCall(Components, "GetExtrasDllVariant", "version") && Client.Call(components, "GetExtrasDllVariant", "version", version)?.ToString() == "Int8";
        }

        private static void ConfigureFsr4(object components, object gpu, string folder, string version)
        {
            if (IsInt8(components, version) && Client.CanCall(Installer, "ConfigureFsr4IntFallback", "gameDir", "isRdna4", "isRdna2"))
            {
                Client.Call(Client.New(Installer), "ConfigureFsr4IntFallback", "gameDir", folder, "isRdna4", Gpu("IsRdna4", gpu), "isRdna2", Gpu("IsRdna2", gpu));
            }
        }

        /// <summary>Changes the settings of the OptiScaler that is already in the game, without downloading or copying anything.</summary>
        public JsonObject Apply(JsonObject parameters)
        {
            touched = false;
            var options = parameters["options"] as JsonObject ?? new JsonObject();
            var game = FindOrCreate(parameters, out var known);
            if (IsHalf(game, Record.Find(game)))
            {
                throw new ServiceException("half", "A change was cut in the middle.");
            }

            Analyze(game, true);
            var service = Client.New(Installer);
            if (!Client.Flag(game, "IsOptiscalerInstalled"))
            {
                throw new ServiceException("notinstalled", "OptiScaler isn't installed in this game.");
            }

            if (Client.CanCall(Installer, "CanApplyConfigOnly", "game", "@Game") && Client.Call(service, "CanApplyConfigOnly", "game", game) is bool possible && !possible)
            {
                throw new ServiceException("reinstall", "OptiScaler was put in this game some other way: it has to be installed again to change it.");
            }

            var config = Client.TryGet(Client.New(Components), "Config");
            var gpu = PreferredGpu(config);
            var record = Record.Find(game);
            var folder = FolderOf(game, record);
            var version = Client.Text(game, "OptiscalerVersion");
            var notes = new List<string>();
            var spoofingBefore = ReadIni(folder, "Spoofing", "Dxgi") ?? Auto;
            progress("install", null, null);

            // The way the game writes them, the client wouldn't find the ones it changes and would add them again.
            var path = IniPath(folder);
            var before = IniFile.Read(path);
            if (before != null)
            {
                var written = before.ToString();
                before.Tidy();
                if (before.ToString() != written)
                {
                    before.Save(path);
                }
            }

            var chosen = OwnSettings.Options.Where(a => !IsOwn(options, a)).ToList();

            // The ones of the person, and every one of the options in which they changed something.
            var mine = OwnOf(game, record, folder, gpu).Where(a => !chosen.Contains(OwnSettings.OptionOf(a))).ToList();
            var own = mine.Select(OwnSettings.OptionOf).Where(a => a != null).Distinct().ToList();
            var back = mine.Where(a => OwnSettings.OptionOf(a) == null).
                Concat(own.SelectMany(OwnSettings.OfOption).Where(a => before.Has(a.Section, a.Key)).Select(a => new Setting(a.Section, a.Key, before.Get(a.Section, a.Key)))).ToList();
            if (Parameter(options, "profile") is string profileName && Contract.Has("profile"))
            {
                var profile = Client.Call(Client.New("ProfileManagementService"), "GetProfileByName", "name", profileName) ??
                    throw new ServiceException("profile", $"There is no profile called {profileName}.");
                CopySettings(game, record, folder, notes);
                Client.Call(service, "ApplyProfileSettings", "game", game, "profile", profile, "resolvedGameDir", folder);

                // A profile writes over the settings of the game, which go after it: the client does it in this order.
                var frames = PlanFrames(game, gpu, folder, record);
                Client.Set(game, "FrameGenerationSettings", frames.Applied);
                ApplyChosen(game, gpu, folder, version);
                KeepChosenFrames(game, frames);
                PlaceHudFix(folder);
                if (Contract.Has("spoofing") && !(options["spoofing"] is JsonValue) && !own.Contains("spoofing"))
                {
                    Client.Call(service, "ApplySpoofingSettings", "game", game, "spoofingValue", spoofingBefore, "resolvedGameDir", folder);
                }

                // And so do the ones that are as the person has them inside the game.
                Write(path, back);
            }

            if (Parameter(options, "spoofing") is string spoofing && spoofing != OwnSettings.Own && Contract.Has("spoofing"))
            {
                Client.Call(service, "ApplySpoofingSettings", "game", game, "spoofingValue", spoofing, "resolvedGameDir", folder);
            }

            if (Parameter(options, "output") is string output && output != OwnSettings.Own && Contract.Has("output"))
            {
                SetOutput(game, output);
                FromScratch(path, "output", () => Client.Call(service, "ApplyOutputUpscalerSettings", "game", game, "resolvedGameDir", folder));
            }

            if (Parameter(options, "quality") is string quality && quality != OwnSettings.Own && Contract.Has("quality"))
            {
                SetQuality(game, quality);
                Client.Call(service, "ApplyUpscalingQualitySettings", "game", game, "resolvedGameDir", folder);
            }

            if (options["frames"] is JsonObject && Contract.Has("frames") && !ApplyFrames(game, options, gpu, folder, record, version, notes))
            {
                chosen.Remove("frames");
            }

            new SettingsStore(Client.Text(game, "InstallPath")).Update(Record.Find(game), before, IniFile.Read(path), chosen);

            // What was changed inside the game since the install stays: only what was chosen now is written.
            Save(game, true);
            var result = Describe(game, known, false);
            result["notes"] = Service.Array(notes);
            return result;
        }

        /// <summary>
        /// The client only writes some of the settings of an option, depending on what is chosen, and takes the others from
        /// what there was: automatic would go on being what it was before.
        /// </summary>
        private static void FromScratch(string path, string option, Action apply)
        {
            var before = IniFile.Read(path);
            Write(path, OwnSettings.OfOption(option).Where(a => before?.Has(a.Section, a.Key) == true));
            try
            {
                apply();
            }
            catch (Exception e) when (before != null && !(e is OutOfMemoryException))
            {
                before.Save(path);
                throw;
            }
        }

        private static void Write(string path, IEnumerable<Setting> settings)
        {
            var ini = IniFile.Read(path);
            var all = settings.ToList();
            if (ini != null && all.Count > 0)
            {
                all.ForEach(a => ini.Set(a.Section, a.Key, a.Value));
                ini.Save(path);
            }
        }

        /// <summary>False when the frame generation stays as it was.</summary>
        private static bool ApplyFrames(object game, JsonObject options, object gpu, string folder, Record record, string version, List<string> notes)
        {
            var before = Client.TryGet(game, "FrameGenerationSettings");
            SetFrames(game, options);
            var plan = PlanFrames(game, gpu, folder, record);
            if (plan.Chosen == null)
            {
                return false;
            }

            if (!FramesAccepted(plan, version))
            {
                Client.Set(game, "FrameGenerationSettings", before);
                notes.Add("framesbefore");
                return false;
            }

            Client.Set(game, "FrameGenerationSettings", plan.Applied);
            FromScratch(IniPath(folder), "frames", () => Client.Call(
                Client.New(Installer), "ApplyFrameGenerationSettings", "game", game, "resolvedGameDir", folder, "gpu", gpu, "optiscalerVersion", version));
            PlaceHudFix(folder);
            KeepChosenFrames(game, plan);
            if (plan.IsNothing)
            {
                notes.Add("framesnone");
            }

            return true;
        }

        public JsonObject Uninstall(JsonObject parameters)
        {
            touched = false;
            var notes = new List<string>();
            var game = FindOrCreate(parameters, out var known);
            var remaining = Recover(game, "uninstall");
            var record = Record.Find(game);
            var target = FolderOf(game, record);
            Guests.BringBack(target, record.Files);
            Analyze(game, true);
            var undone = remaining != null && !record.IsWhole;
            if (!undone)
            {
                if (Client.Flag(game, "IsDlssNrOnAmdInstalled"))
                {
                    throw new ServiceException("neural", "The game has the neural rendering mod: its window in OptiScaler Client takes both out.");
                }

                if (!Client.Flag(game, "IsOptiscalerInstalled") && !Client.Flag(game, "IsFsr4DllSwapped"))
                {
                    throw new ServiceException("notinstalled", "OptiScaler isn't installed in this game.");
                }

                CheckFree(target, record);
                progress("uninstall", null, null);
                var originals = record.IsWhole ? record.Originals : new Dictionary<string, string>();
                var written = record.Files.ToList();
                var byHand = record.IsOfOptiScaler || !Client.Flag(game, "IsOptiscalerInstalled") ? new List<string>() : FilesByHand(target, null);
                if (Client.Flag(game, "IsOptiscalerInstalled"))
                {
                    CopySettings(game, record, target, notes);
                }

                using (new Guests(target, () => OwnFiles(game, target).Concat(byHand.Select(a => Path.GetRelativePath(target, a)))))
                {
                    // Without a record, what the client takes out has no copy: it's kept aside until it ends.
                    var plan = string.IsNullOrEmpty(target) || (record.IsWhole && byHand.Count == 0) ? new Plan() : GetPlan(target, record);
                    byHand.ForEach(a => plan.Add(Path.GetRelativePath(target, a)));
                    using (var checkpoint = Checkpoint.Begin(Client.Text(game, "InstallPath"), record.Key, target, plan))
                    {
                        // The client can't delete the ones that can only be read, and with its settings there the game still has OptiScaler.
                        Unlock(checkpoint, target, plan.Names.Select(a => a.Key).Concat(written).Where(Checkpoint.IsOwn));
                        var answer = CallInstaller(game, "UninstallOptiScaler", new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) { ["game"] = game });
                        remaining = Client.Strings(Client.TryGet(answer, "RemainingSensitiveFiles"));
                        if (byHand.Count > 0)
                        {
                            // The client leaves every file a game could have with the same name. These ones are known, and the game
                            // would go on loading the first. They go after it: it looks at what there is to know the folder.
                            RemoveByHand(checkpoint, target, byHand);
                            remaining = remaining.Where(a => !byHand.Contains(Path.Combine(target, a), StringComparer.OrdinalIgnoreCase)).ToList();
                        }

                        if (record.IsOfOptiScaler)
                        {
                            // The client names every one that could be of OptiScaler: the ones it never wrote are of the game.
                            remaining = remaining.Where(a => written.Contains(a, StringComparer.OrdinalIgnoreCase) || IsMain(Path.Combine(target, a))).ToList();
                        }

                        checkpoint.PutOriginals(target, originals);
                        checkpoint.End();
                    }
                }
            }

            // An install that was undone may have been over another one that was put by hand, which is still there.
            if (!undone || !Client.Flag(game, "IsOptiscalerInstalled"))
            {
                Client.Set(game, "IsOptiscalerInstalled", false);
                Client.Set(game, "OptiscalerVersion", null);
                Try(notes, "settings", new SettingsStore(Client.Text(game, "InstallPath")).Forget);
            }

            Client.Set(game, "Fsr4ExtraVersion", null);
            Save(game, false);
            Analyze(game, true);
            var result = Describe(game, known, false);
            result["remaining"] = Service.Array(remaining);
            result["undone"] = undone;
            if (Guests.Any(target))
            {
                notes.Add("guests");
            }

            result["notes"] = Service.Array(notes);
            return result;
        }
    }
}
