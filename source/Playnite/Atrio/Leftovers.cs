using Microsoft.Win32;
using Playnite.Common;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Playnite.Atrio
{
    public enum LeftoverKind
    {
        /// <summary>The folder of a game in a library of Steam, without the game installed.</summary>
        SteamGame,
        /// <summary>The shaders Steam keeps for a game that isn't installed.</summary>
        SteamShaders,
        /// <summary>Things of the Workshop of a game that isn't installed.</summary>
        SteamWorkshop,
        /// <summary>A download that was left halfway, of a game that isn't installed.</summary>
        SteamDownload,
        /// <summary>The folder of a game in the folder of Epic, without the game installed.</summary>
        EpicGame,
        /// <summary>The folder of a game in the folder of GOG, without the game installed.</summary>
        GogGame
    }

    /// <summary>A folder that stayed behind after a game went away.</summary>
    public class LeftoverFolder
    {
        public string Path { get; set; }
        public LeftoverKind Kind { get; set; }

        /// <summary>The folder's own name, or the id of the game for the ones Steam names by number.</summary>
        public string Name { get; set; }

        /// <summary>For the ones Steam names by number: its id.</summary>
        public string AppId { get; set; }
    }

    /// <summary>What the stores have installed, to tell a leftover from a game.</summary>
    public class LeftoverSources
    {
        /// <summary>Libraries of Steam with the games its client has a file for, in that library and in all.</summary>
        public List<SteamLibraryContents> SteamLibraries { get; set; } = new List<SteamLibraryContents>();

        /// <summary>Where Epic has its games.</summary>
        public List<string> EpicInstalls { get; set; } = new List<string>();

        /// <summary>Folders of Epic that hold games, also the one of its installer.</summary>
        public List<string> EpicRoots { get; set; } = new List<string>();

        public List<string> GogInstalls { get; set; } = new List<string>();
        public List<string> GogRoots { get; set; } = new List<string>();

        /// <summary>Every folder a game of the library says it's in, from any store or added by hand.</summary>
        public List<string> Claimed { get; set; } = new List<string>();

        /// <summary>The games of Steam the library says are installed: their shaders and Workshop stay, even if Steam has no file for them.</summary>
        public List<string> InstalledSteamApps { get; set; } = new List<string>();

        /// <summary>The clock of the computer, for the tests.</summary>
        public DateTime Now { get; set; } = DateTime.Now;
    }

    public class SteamLibraryContents
    {
        public string Library { get; set; }

        /// <summary>The folders, under steamapps\common, of the games installed in this library.</summary>
        public List<string> InstallDirs { get; set; } = new List<string>();
    }

    /// <summary>
    /// The folders that stores leave behind when a game goes away: its folder in the library, its shaders, its
    /// things of the Workshop or a download left halfway. Only inside the folders of the stores, only what no
    /// installed game uses, and nothing is deleted here without the person choosing it: it goes to the bin.
    /// </summary>
    public static class Leftovers
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        /// <summary>Folders of the client of Steam itself, which no game has a file for.</summary>
        private static readonly HashSet<string> steamOwn = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Steam Controller Configs", "Steamworks Shared", "SteamLinuxRuntime", "SteamLinuxRuntime_soldier", "SteamLinuxRuntime_sniper"
        };

        /// <summary>The Workshop of the configurations of the controllers belongs to the client, not to a game.</summary>
        private static readonly HashSet<string> steamOwnApps = new HashSet<string> { "241100", "228980", "7" };

        /// <summary>Folders of Epic that are its launcher or what its games share.</summary>
        private static readonly string[] epicOwn = { "Launcher", "DirectXRedist", "GameInputRedist", "EpicOnlineServices", "Epic Online Services", "VCRedist" };
        private static readonly string[] epicOwnPrefixes = { "UE_", "Unreal", "Fab" };

        private static bool IsEpicOwn(string name)
        {
            return epicOwn.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                epicOwnPrefixes.Any(a => name.StartsWith(a, StringComparison.OrdinalIgnoreCase));
        }

        public static string Normalize(string path)
        {
            if (path.IsNullOrWhiteSpace())
            {
                return null;
            }

            try
            {
                return System.IO.Path.GetFullPath(path.Trim()).TrimEnd('\\', '/');
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
            {
                return null;
            }
        }

        /// <summary>The same folder, or one inside the other.</summary>
        public static bool Overlaps(string a, string b)
        {
            a = Normalize(a);
            b = Normalize(b);
            if (a == null || b == null)
            {
                return false;
            }

            return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase) ||
                b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> GetFolders(string path)
        {
            try
            {
                return Directory.Exists(path) ? Directory.GetDirectories(path).ToList() : new List<string>();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return new List<string>();
            }
        }

        public static List<LeftoverFolder> Find(LeftoverSources sources)
        {
            var result = new List<LeftoverFolder>();
            var claimed = (sources.Claimed ?? new List<string>()).Select(Normalize).Where(a => a != null).ToList();
            bool isFree(string path) => !claimed.Any(a => Overlaps(a, path));

            // Steam: every game it has installed, in any library, keeps its shaders and its Workshop by its id. So do
            // the ones the library of Atrio has as installed, in case the files of Steam don't say it.
            var installedApps = new HashSet<string>(sources.InstalledSteamApps ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var library in sources.SteamLibraries ?? new List<SteamLibraryContents>())
            {
                foreach (var file in SafeFiles(System.IO.Path.Combine(library.Library, "steamapps"), "appmanifest_*.acf"))
                {
                    installedApps.Add(System.IO.Path.GetFileNameWithoutExtension(file).Substring("appmanifest_".Length));
                }
            }

            foreach (var library in sources.SteamLibraries ?? new List<SteamLibraryContents>())
            {
                var apps = System.IO.Path.Combine(library.Library, "steamapps");
                var dirs = new HashSet<string>(library.InstallDirs ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                foreach (var folder in GetFolders(System.IO.Path.Combine(apps, "common")))
                {
                    var name = System.IO.Path.GetFileName(folder);
                    if (!dirs.Contains(name) && !steamOwn.Contains(name) && isFree(folder))
                    {
                        result.Add(new LeftoverFolder { Path = folder, Kind = LeftoverKind.SteamGame, Name = name });
                    }
                }

                void byId(string sub, LeftoverKind kind)
                {
                    foreach (var folder in GetFolders(System.IO.Path.Combine(apps, sub)))
                    {
                        var id = System.IO.Path.GetFileName(folder);
                        if (id.All(char.IsDigit) && !installedApps.Contains(id) && !steamOwnApps.Contains(id) && isFree(folder))
                        {
                            result.Add(new LeftoverFolder { Path = folder, Kind = kind, Name = id, AppId = id });
                        }
                    }
                }

                byId("shadercache", LeftoverKind.SteamShaders);
                byId(System.IO.Path.Combine("workshop", "content"), LeftoverKind.SteamWorkshop);
                byId("downloading", LeftoverKind.SteamDownload);
            }

            AddStoreFolders(result, sources.EpicRoots, sources.EpicInstalls, LeftoverKind.EpicGame, IsEpicOwn, isFree);

            // GOG writes its installs in the registry only at the end: a folder that changed these days may be one on its way.
            var recent = sources.Now - TimeSpan.FromDays(2);
            AddStoreFolders(result, sources.GogRoots, sources.GogInstalls, LeftoverKind.GogGame,
                name => name.StartsWith("!", StringComparison.Ordinal), path => isFree(path) && !ChangedSince(path, recent));
            return result;
        }

        private static bool ChangedSince(string path, DateTime limit)
        {
            try
            {
                return Directory.GetLastWriteTime(path) > limit;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static void AddStoreFolders(List<LeftoverFolder> result, List<string> roots, List<string> installs, LeftoverKind kind, Func<string, bool> isOwn, Func<string, bool> isFree)
        {
            var installed = (installs ?? new List<string>()).Select(Normalize).Where(a => a != null).ToList();
            foreach (var root in (roots ?? new List<string>()).Select(Normalize).Where(a => a != null).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var folder in GetFolders(root))
                {
                    var name = System.IO.Path.GetFileName(folder);
                    if (!isOwn(name) && !installed.Any(a => Overlaps(a, folder)) && isFree(folder) &&
                        !result.Any(a => Overlaps(a.Path, folder)))
                    {
                        result.Add(new LeftoverFolder { Path = folder, Kind = kind, Name = name });
                    }
                }
            }
        }

        private static IEnumerable<string> SafeFilesDeep(string folder, string pattern)
        {
            try
            {
                return Directory.Exists(folder) ? Directory.GetFiles(folder, pattern, SearchOption.AllDirectories) : new string[0];
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return SafeFiles(folder, pattern);
            }
        }

        private static IEnumerable<string> SafeFiles(string folder, string pattern)
        {
            try
            {
                return Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : new string[0];
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return new string[0];
            }
        }

        /// <summary>The folder of the computer whose name is that one, among its ancestors, if any.</summary>
        public static string FindAncestorNamed(string path, params string[] names)
        {
            var current = Normalize(path);
            while (!current.IsNullOrEmpty())
            {
                var name = System.IO.Path.GetFileName(current);
                if (names.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    return current;
                }

                current = System.IO.Path.GetDirectoryName(current);
            }

            return null;
        }

        /// <summary>What the stores of this computer have installed, read from their own files.</summary>
        public static LeftoverSources ReadSources(IEnumerable<string> claimed, IEnumerable<string> installedSteamApps = null)
        {
            var sources = new LeftoverSources
            {
                Claimed = claimed?.ToList() ?? new List<string>(),
                InstalledSteamApps = installedSteamApps?.ToList() ?? new List<string>()
            };
            try
            {
                foreach (var group in SteamClientFiles.ReadAllManifests().GroupBy(a => a.Library, StringComparer.OrdinalIgnoreCase))
                {
                    sources.SteamLibraries.Add(new SteamLibraryContents
                    {
                        Library = group.Key,
                        InstallDirs = group.Select(a => a.InstallDir).Where(a => !a.IsNullOrEmpty()).ToList()
                    });
                }

                // A library without games doesn't have files, but it's still a library.
                foreach (var library in SteamClientFiles.GetLibraries())
                {
                    if (!sources.SteamLibraries.Any(a => a.Library.Equals(library, StringComparison.OrdinalIgnoreCase)))
                    {
                        sources.SteamLibraries.Add(new SteamLibraryContents { Library = library });
                    }
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, "Failed to read the libraries of Steam.");
            }

            ReadEpic(sources);
            ReadGog(sources);
            return sources;
        }

        private static void ReadEpic(LeftoverSources sources)
        {
            try
            {
                var data = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

                // Also the ones of Manifests\Pending: the installs that Epic has halfway or paused.
                foreach (var file in SafeFilesDeep(System.IO.Path.Combine(data, "Epic", "EpicGamesLauncher", "Data", "Manifests"), "*.item"))
                {
                    var location = ReadJsonValue(file, "InstallLocation");
                    if (!location.IsNullOrEmpty())
                    {
                        sources.EpicInstalls.Add(location);
                    }
                }

                var installed = System.IO.Path.Combine(data, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
                if (File.Exists(installed))
                {
                    var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(installed));
                    foreach (var entry in json["InstallationList"] as Newtonsoft.Json.Linq.JArray ?? new Newtonsoft.Json.Linq.JArray())
                    {
                        var location = (string)entry["InstallLocation"];
                        if (!location.IsNullOrEmpty())
                        {
                            sources.EpicInstalls.Add(location);
                        }
                    }
                }

                // The folder each game was put in is named after Epic, unless the person chose another: only those.
                foreach (var install in sources.EpicInstalls)
                {
                    var root = FindAncestorNamed(install, "Epic Games");
                    if (root != null)
                    {
                        sources.EpicRoots.Add(root);
                    }
                }

                var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!programs.IsNullOrEmpty() && Directory.Exists(System.IO.Path.Combine(programs, "Epic Games")))
                {
                    sources.EpicRoots.Add(System.IO.Path.Combine(programs, "Epic Games"));
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, "Failed to read the games of Epic.");
            }
        }

        private static string ReadJsonValue(string file, string key)
        {
            try
            {
                return (string)Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(file))[key];
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is Newtonsoft.Json.JsonException)
            {
                return null;
            }
        }

        private static void ReadGog(LeftoverSources sources)
        {
            try
            {
                using (var games = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games"))
                {
                    foreach (var id in games?.GetSubKeyNames() ?? new string[0])
                    {
                        using (var game = games.OpenSubKey(id))
                        {
                            if (game?.GetValue("path") is string path && !path.IsNullOrEmpty())
                            {
                                sources.GogInstalls.Add(path);
                                var root = FindAncestorNamed(path, "GOG Games");
                                if (root == null && System.IO.Path.GetFileName(Normalize(System.IO.Path.GetDirectoryName(path)) ?? "").Equals("Games", StringComparison.OrdinalIgnoreCase) &&
                                    FindAncestorNamed(path, "GOG Galaxy") != null)
                                {
                                    root = Normalize(System.IO.Path.GetDirectoryName(path));
                                }

                                if (root != null)
                                {
                                    sources.GogRoots.Add(root);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, "Failed to read the games of GOG.");
            }
        }

        public class Measure
        {
            public long Size { get; set; }

            /// <summary>The newest change of anything inside, to tell the folders still in use.</summary>
            public DateTime? LastChange { get; set; }
        }

        /// <summary>The size of a folder and its last change. Links to other folders are not followed.</summary>
        public static Measure MeasureFolder(string path, CancellationToken token)
        {
            var result = new Measure();
            var pending = new Stack<DirectoryInfo>();
            try
            {
                pending.Push(new DirectoryInfo(path));
                while (pending.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var folder = pending.Pop();
                    FileSystemInfo[] entries;
                    try
                    {
                        entries = folder.GetFileSystemInfos();
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        continue;
                    }

                    foreach (var entry in entries)
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }

                        var changed = entry.LastWriteTime;
                        if (result.LastChange == null || changed > result.LastChange)
                        {
                            result.LastChange = changed;
                        }

                        if (entry is DirectoryInfo directory)
                        {
                            pending.Push(directory);
                        }
                        else if (entry is FileInfo file)
                        {
                            result.Size += file.Length;
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                logger.Debug(e, $"Failed to measure {path}.");
            }

            return result;
        }

        // Windows packs this one byte by byte in 32-bit programs, like Atrio, and normally in 64-bit ones.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        private struct SHFILEOPSTRUCT32
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT64
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        private static extern int SHFileOperation32(ref SHFILEOPSTRUCT32 operation);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        private static extern int SHFileOperation64(ref SHFILEOPSTRUCT64 operation);

        private const uint FO_DELETE = 3;
        private const ushort FOF_NOCONFIRMATION = 0x0010;
        private const ushort FOF_ALLOWUNDO = 0x0040;
        private const ushort FOF_WANTNUKEWARNING = 0x4000;

        /// <summary>
        /// To the bin of Windows, from where it can come back. When it doesn't fit there, Windows itself asks
        /// before deleting it for good. True only if the folder is gone.
        /// </summary>
        public static bool SendToBin(string path)
        {
            var full = Normalize(path);
            if (full == null || !Directory.Exists(full) || full.Length <= 3)
            {
                return false;
            }

            const ushort flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING;
            var code = 0;
            var aborted = false;
            // The shell wants a thread of its own kind, which the pool doesn't have.
            var thread = new Thread(() =>
            {
                if (IntPtr.Size == 4)
                {
                    var operation = new SHFILEOPSTRUCT32 { wFunc = FO_DELETE, pFrom = full + "\0\0", fFlags = flags };
                    code = SHFileOperation32(ref operation);
                    aborted = operation.fAnyOperationsAborted;
                }
                else
                {
                    var operation = new SHFILEOPSTRUCT64 { wFunc = FO_DELETE, pFrom = full + "\0\0", fFlags = flags };
                    code = SHFileOperation64(ref operation);
                    aborted = operation.fAnyOperationsAborted;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
            if (code != 0 || aborted)
            {
                logger.Warn($"The bin didn't take {full}: code {code}, aborted {aborted}.");
            }

            return !Directory.Exists(full);
        }
    }
}
