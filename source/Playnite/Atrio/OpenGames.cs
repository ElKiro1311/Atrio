using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    /// <summary>Games that were opened or closed since the previous check.</summary>
    public class OpenGamesChangedEventArgs : EventArgs
    {
        public List<Guid> Opened { get; } = new List<Guid>();
        public List<Guid> Closed { get; } = new List<Guid>();
        public IEnumerable<Guid> All => Opened.Concat(Closed);
        public bool IsEmpty => Opened.Count == 0 && Closed.Count == 0;
    }

    /// <summary>One round of the watch: what is open and which program the person has in front.</summary>
    public class OpenGamesTickEventArgs : EventArgs
    {
        public IReadOnlyDictionary<Guid, List<int>> Open { get; }
        public int ForegroundProcessId { get; }

        /// <summary>Time since the previous round, without the time the computer was asleep.</summary>
        public double ElapsedSeconds { get; }

        public OpenGamesTickEventArgs(IReadOnlyDictionary<Guid, List<int>> open, int foregroundProcessId, double elapsedSeconds)
        {
            Open = open ?? new Dictionary<Guid, List<int>>();
            ForegroundProcessId = foregroundProcessId;
            ElapsedSeconds = elapsedSeconds;
        }
    }

    public enum CloseGameResult
    {
        Closed,
        StillOpen,
        Denied
    }

    /// <summary>
    /// Games with a program running from their folder right now, whoever opened them: Atrio, their store or
    /// the person. It only reads which programs are running and where they live, never anything inside them.
    /// </summary>
    public static class OpenGames
    {
        public const int CheckMilliseconds = 3000;

        /// <summary>More games than this in one folder means it holds roms or a whole library, not a game.</summary>
        public const int MaxGamesPerFolder = 3;

        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly object sync = new object();
        private static Dictionary<Guid, List<int>> open = new Dictionary<Guid, List<int>>();
        private static List<GameFolder> folders = new List<GameFolder>();
        private static Func<IEnumerable<Game>> getGames;
        private static SynchronizationContext context;
        private static System.Threading.Timer timer;
        private static int checking;
        private static volatile bool libraryChanged = true;

        /// <summary>Raised on the interface thread when games are opened or closed.</summary>
        public static event EventHandler<OpenGamesChangedEventArgs> Changed;

        /// <summary>Raised on the thread of the watch after every round, also when nothing changed.</summary>
        public static event EventHandler<OpenGamesTickEventArgs> Ticked;

        /// <summary>A round never counts for more than this: a longer gap is the computer asleep.</summary>
        public const double MaxTickSeconds = 3 * CheckMilliseconds / 1000.0;

        private static readonly Stopwatch tickWatch = new Stopwatch();

        public static bool IsRunning => timer != null;

        public class GameFolder
        {
            public string Folder { get; set; }
            public List<Guid> Games { get; set; }
        }

        public static bool IsOpen(Guid gameId)
        {
            lock (sync)
            {
                return open.ContainsKey(gameId);
            }
        }

        public static List<int> GetProcessIds(Guid gameId)
        {
            lock (sync)
            {
                return open.TryGetValue(gameId, out var ids) ? ids.ToList() : new List<int>();
            }
        }

        public static List<Guid> GetOpenGames()
        {
            lock (sync)
            {
                return open.Keys.ToList();
            }
        }

        /// <summary>The first of these copies that has something running.</summary>
        public static Game GetOpenCopy(IEnumerable<Game> copies)
        {
            return copies?.FirstOrDefault(a => a != null && IsOpen(a.Id));
        }

        /// <summary>Where the games come from and where the changes are told. Starts watching when it's on.</summary>
        public static void Attach(Func<IEnumerable<Game>> games, SynchronizationContext uiContext, bool enabled)
        {
            getGames = games;
            context = uiContext;
            if (enabled)
            {
                Start();
            }
        }

        public static void SetEnabled(bool enabled)
        {
            if (enabled)
            {
                Start();
            }
            else
            {
                Stop();
            }
        }

        private static void Start()
        {
            if (timer != null || getGames == null)
            {
                return;
            }

            libraryChanged = true;
            lock (tickWatch)
            {
                tickWatch.Reset();
            }

            timer = new System.Threading.Timer(_ => CheckFromTimer(), null, 2000, CheckMilliseconds);
            logger.Info("Watching which games are open.");
        }

        private static void Stop()
        {
            var running = timer;
            timer = null;
            running?.Dispose();
            OpenGamesChangedEventArgs change;
            lock (sync)
            {
                change = Compare(open, new Dictionary<Guid, List<int>>());
                open = new Dictionary<Guid, List<int>>();
            }

            Raise(change);
            RaiseTick(new Dictionary<Guid, List<int>>(), 0, 0);
        }

        private static void RaiseTick(Dictionary<Guid, List<int>> now, int foreground, double elapsed)
        {
            try
            {
                Ticked?.Invoke(null, new OpenGamesTickEventArgs(now, foreground, elapsed));
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to handle a round of the open games.");
            }
        }

        /// <summary>Games were added, removed or changed: their folders are read again on the next check.</summary>
        public static void LibraryChanged()
        {
            libraryChanged = true;
        }

        /// <summary>Checks right away instead of waiting for the next round.</summary>
        public static void CheckSoon()
        {
            if (timer != null)
            {
                Task.Run(() => CheckFromTimer());
            }
        }

        private static void CheckFromTimer()
        {
            if (Interlocked.Exchange(ref checking, 1) == 1)
            {
                return;
            }

            try
            {
                Check();
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to check which games are open.");
            }
            finally
            {
                Interlocked.Exchange(ref checking, 0);
            }
        }

        private static void Check()
        {
            if (libraryChanged)
            {
                libraryChanged = false;
                var games = getGames?.Invoke()?.ToList() ?? new List<Game>();
                folders = GetFolders(GetCandidates(games), GetSharedFolders());
            }

            var now = folders.Count == 0 ?
                new Dictionary<Guid, List<int>>() :
                Match(folders, ReadProcesses());
            OpenGamesChangedEventArgs change;
            lock (sync)
            {
                change = Compare(open, now);
                open = now;
            }

            if (!change.IsEmpty)
            {
                logger.Info($"Open games changed: {change.Opened.Count} opened, {change.Closed.Count} closed.");
                Raise(change);
            }

            double elapsed;
            lock (tickWatch)
            {
                elapsed = tickWatch.IsRunning ? Math.Min(tickWatch.Elapsed.TotalSeconds, MaxTickSeconds) : 0;
                tickWatch.Restart();
            }

            RaiseTick(now, now.Count == 0 ? 0 : GetForegroundProcessId(), elapsed);
        }

        /// <summary>The program of the window the person has in front, 0 when it can't be known.</summary>
        public static int GetForegroundProcessId()
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return 0;
            }

            GetWindowThreadProcessId(window, out var processId);
            return processId;
        }

        private static void Raise(OpenGamesChangedEventArgs change)
        {
            if (change.IsEmpty)
            {
                return;
            }

            if (context == null)
            {
                Changed?.Invoke(null, change);
            }
            else
            {
                context.Post(_ => Changed?.Invoke(null, change), null);
            }
        }

        private static IEnumerable<KeyValuePair<Guid, string>> GetCandidates(List<Game> games)
        {
            foreach (var game in games)
            {
                // Emulated games point to the folder of their roms, not to a program of their own.
                if (game == null || !game.IsInstalled || game.InstallDirectory.IsNullOrWhiteSpace() || game.Roms.HasItems())
                {
                    continue;
                }

                string folder;
                try
                {
                    folder = game.ExpandVariables(game.InstallDirectory, true);
                }
                catch
                {
                    continue;
                }

                yield return new KeyValuePair<Guid, string>(game.Id, folder);
            }
        }

        /// <summary>Folders that hold programs of many kinds: a game can't claim them.</summary>
        public static List<string> GetSharedFolders()
        {
            var list = new List<string>();
            void Add(string path)
            {
                if (!path.IsNullOrWhiteSpace())
                {
                    list.Add(path.TrimEnd('\\', '/'));
                }
            }

            foreach (Environment.SpecialFolder folder in new[]
            {
                Environment.SpecialFolder.Windows,
                Environment.SpecialFolder.System,
                Environment.SpecialFolder.SystemX86,
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonProgramFiles,
                Environment.SpecialFolder.CommonProgramFilesX86,
                Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.UserProfile,
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolder.MyDocuments
            })
            {
                Add(Environment.GetFolderPath(folder));
            }

            // Atrio is 32-bit: this is the real "Program Files" for 64-bit programs.
            Add(Environment.GetEnvironmentVariable("ProgramW6432"));
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!profile.IsNullOrWhiteSpace())
            {
                Add(Path.GetDirectoryName(profile));
                Add(Path.Combine(profile, "Downloads"));
            }

            Add(PlaynitePaths.ProgramPath);
            return list;
        }

        /// <summary>Last folder names of places that hold whole libraries of games.</summary>
        private static readonly string[] libraryFolderNames = new[]
        {
            "steamapps",
            "SteamLibrary",
            "XboxGames",
            "WindowsApps"
        };

        /// <summary>The folder as it's compared with the programs, ending in "\", or null when it can't belong to a single game.</summary>
        public static string GetFolderKey(string folder, IEnumerable<string> sharedFolders)
        {
            if (folder.IsNullOrWhiteSpace())
            {
                return null;
            }

            try
            {
                folder = folder.Trim();
                if (!Path.IsPathRooted(folder))
                {
                    return null;
                }

                var full = Path.GetFullPath(folder).TrimEnd('\\', '/');
                var root = Path.GetPathRoot(full)?.TrimEnd('\\', '/');
                if (full.Length == 0 || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (sharedFolders?.Any(a => string.Equals(a, full, StringComparison.OrdinalIgnoreCase)) == true)
                {
                    return null;
                }

                var name = Path.GetFileName(full);
                var parent = Path.GetFileName(Path.GetDirectoryName(full) ?? string.Empty);
                if (libraryFolderNames.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) ||
                    (string.Equals(name, "common", StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(parent, "steamapps", StringComparison.OrdinalIgnoreCase)))
                {
                    return null;
                }

                return full + "\\";
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Folders to look for, the deepest first so a program belongs to the most specific game.</summary>
        public static List<GameFolder> GetFolders(IEnumerable<KeyValuePair<Guid, string>> games, IEnumerable<string> sharedFolders)
        {
            var shared = sharedFolders?.ToList() ?? new List<string>();
            var groups = games.
                Select(a => new { a.Key, Folder = GetFolderKey(a.Value, shared) }).
                Where(a => a.Folder != null).
                GroupBy(a => a.Folder, StringComparer.OrdinalIgnoreCase).
                Where(a => a.Count() <= MaxGamesPerFolder).
                Select(a => new GameFolder { Folder = a.Key, Games = a.Select(b => b.Key).Distinct().ToList() }).
                ToList();

            // A folder with the folders of other games inside is where a library lives.
            return groups.
                Where(a => groups.Count(b => b != a && b.Folder.Length > a.Folder.Length &&
                    b.Folder.StartsWith(a.Folder, StringComparison.OrdinalIgnoreCase)) < 2).
                OrderByDescending(a => a.Folder.Length).
                ToList();
        }

        /// <summary>Which games have programs running, with the programs of each one.</summary>
        public static Dictionary<Guid, List<int>> Match(List<GameFolder> folders, IEnumerable<KeyValuePair<int, string>> processes)
        {
            var result = new Dictionary<Guid, List<int>>();
            foreach (var process in processes)
            {
                if (process.Value.IsNullOrEmpty())
                {
                    continue;
                }

                var folder = folders.FirstOrDefault(a => process.Value.StartsWith(a.Folder, StringComparison.OrdinalIgnoreCase));
                if (folder == null)
                {
                    continue;
                }

                foreach (var game in folder.Games)
                {
                    if (!result.TryGetValue(game, out var ids))
                    {
                        result[game] = ids = new List<int>();
                    }

                    ids.Add(process.Key);
                }
            }

            return result;
        }

        private static readonly string[] helperFolders = new[] { "\\_CommonRedist\\", "\\__Installer\\", "\\Redist\\", "\\_Redist\\", "\\Installers\\" };

        /// <summary>
        /// Programs that live with the game without being it: its uninstaller and the installers of what it
        /// needs, which the store runs from its folder. With them running the game isn't open.
        /// </summary>
        public static bool IsHelper(string path)
        {
            if (path.IsNullOrEmpty())
            {
                return true;
            }

            if (helperFolders.Any(a => path.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            var name = Path.GetFileName(path);
            return name.StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase) ||
                name.IndexOf("redist", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.Equals("DXSETUP.exe", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("dotNetFx", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("oalinst", StringComparison.OrdinalIgnoreCase);
        }

        public static OpenGamesChangedEventArgs Compare(IDictionary<Guid, List<int>> before, IDictionary<Guid, List<int>> after)
        {
            var change = new OpenGamesChangedEventArgs();
            change.Opened.AddRange(after.Keys.Where(a => !before.ContainsKey(a)));
            change.Closed.AddRange(before.Keys.Where(a => !after.ContainsKey(a)));
            return change;
        }

        private static List<KeyValuePair<int, string>> ReadProcesses()
        {
            var result = new List<KeyValuePair<int, string>>();
            int session, own;
            using (var current = Process.GetCurrentProcess())
            {
                session = current.SessionId;
                own = current.Id;
            }

            var atrio = PlaynitePaths.ProgramPath?.TrimEnd('\\') + "\\";
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id <= 4 || process.Id == own || process.SessionId != session)
                    {
                        continue;
                    }

                    var path = GetProcessPath(process.Id);
                    if (path != null && !path.StartsWith(atrio, StringComparison.OrdinalIgnoreCase) && !IsHelper(path))
                    {
                        result.Add(new KeyValuePair<int, string>(process.Id, path));
                    }
                }
                catch
                {
                    // It closed while the list was read.
                }
                finally
                {
                    process.Dispose();
                }
            }

            return result;
        }

        #region Windows

        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint ProcessTerminate = 0x0001;
        private const uint StillActive = 259;
        private const uint WmClose = 0x0010;
        private const int GwlStyle = -16;
        private const int GwlExStyle = -20;
        private const long WsExToolWindow = 0x00000080L;
        private const int SwRestore = 9;

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        internal static string GetProcessPath(int processId)
        {
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var size = 1024;
                var builder = new StringBuilder(size);
                return QueryFullProcessImageName(handle, 0, builder, ref size) ? builder.ToString(0, size) : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        /// <summary>
        /// The program as number and start time: Windows reuses numbers, so both together tell that it's the
        /// same program after Atrio restarts. Null when Windows doesn't say.
        /// </summary>
        public static string GetProcessKey(int processId)
        {
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return GetProcessTimes(handle, out var creation, out _, out _, out _) && creation > 0 ?
                    processId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + creation.ToString(System.Globalization.CultureInfo.InvariantCulture) :
                    null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static bool IsAlive(int processId)
        {
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero)
            {
                // Gone, or a protected program: the next check of the list tells which.
                return Marshal.GetLastWin32Error() == 5;
            }

            try
            {
                return GetExitCodeProcess(handle, out var code) && code == StillActive;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        /// <summary>Top windows of these programs that a person can see, the biggest first.</summary>
        public static List<IntPtr> GetWindows(IEnumerable<int> processIds)
        {
            var ids = new HashSet<int>(processIds ?? Enumerable.Empty<int>());
            var found = new List<KeyValuePair<IntPtr, long>>();
            if (ids.Count == 0)
            {
                return new List<IntPtr>();
            }

            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var processId);
                if (ids.Contains(processId) && IsWindowVisible(window) &&
                    GetWindow(window, 4) == IntPtr.Zero &&
                    (GetWindowLong(window, GwlExStyle) & WsExToolWindow) == 0)
                {
                    GetWindowRect(window, out var rect);
                    found.Add(new KeyValuePair<IntPtr, long>(window, (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top)));
                }

                return true;
            }, IntPtr.Zero);

            return found.OrderByDescending(a => a.Value).Select(a => a.Key).ToList();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr window);

        /// <summary>The window in front when it belongs to another program, to come back to it later.</summary>
        public static IntPtr GetOthersForegroundWindow()
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            GetWindowThreadProcessId(window, out var processId);
            using (var current = Process.GetCurrentProcess())
            {
                return processId == current.Id ? IntPtr.Zero : window;
            }
        }

        /// <summary>The open game that owns this window, if any.</summary>
        public static Guid? FindGameOfWindow(IntPtr window)
        {
            if (window == IntPtr.Zero)
            {
                return null;
            }

            GetWindowThreadProcessId(window, out var processId);
            lock (sync)
            {
                foreach (var pair in open)
                {
                    if (pair.Value.Contains(processId))
                    {
                        return pair.Key;
                    }
                }
            }

            return null;
        }

        /// <summary>Restores the window and puts it in front. Windows only lets the program in front do it.</summary>
        public static bool BringToFront(IntPtr window)
        {
            if (window == IntPtr.Zero || !IsWindow(window))
            {
                return false;
            }

            if (IsIconic(window))
            {
                ShowWindow(window, SwRestore);
            }

            return SetForegroundWindow(window);
        }

        /// <summary>Brings the biggest window of the game to the front.</summary>
        public static bool ShowGame(Guid gameId)
        {
            return BringToFront(GetWindows(GetProcessIds(gameId)).FirstOrDefault());
        }

        /// <summary>
        /// The "Stop" of a game page: asks first, then asks the game to close, and only forces it after a
        /// second yes, because what isn't saved is lost.
        /// </summary>
        public static async Task<CloseGameResult> StopAsync(Game copy, string name, IDialogsFactory dialogs)
        {
            var title = ResourceProvider.GetString("LOCAtrioStopGameTitle");
            if (dialogs.ShowMessage(string.Format(ResourceProvider.GetString("LOCAtrioStopGameAsk"), name), title,
                System.Windows.MessageBoxButton.YesNo) != System.Windows.MessageBoxResult.Yes)
            {
                return CloseGameResult.StillOpen;
            }

            var result = await CloseAsync(copy.Id, TimeSpan.FromSeconds(10));
            if (result == CloseGameResult.StillOpen && IsOpen(copy.Id))
            {
                if (dialogs.ShowMessage(string.Format(ResourceProvider.GetString("LOCAtrioStopGameForceAsk"), name), title,
                    System.Windows.MessageBoxButton.YesNo) != System.Windows.MessageBoxResult.Yes)
                {
                    return CloseGameResult.StillOpen;
                }

                result = await ForceCloseAsync(copy.Id);
            }

            if (result == CloseGameResult.Denied)
            {
                dialogs.ShowErrorMessage(string.Format(ResourceProvider.GetString("LOCAtrioStopGameDenied"), name), title);
            }

            return result;
        }

        /// <summary>Asks the game to close, like pressing Alt+F4 or the X of its window, and waits for it.</summary>
        public static async Task<CloseGameResult> CloseAsync(Guid gameId, TimeSpan wait)
        {
            var ids = GetProcessIds(gameId);
            if (ids.Count == 0)
            {
                return CloseGameResult.Closed;
            }

            var windows = GetWindows(ids);
            var asked = 0;
            var denied = 0;
            foreach (var window in windows)
            {
                if (PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero))
                {
                    asked++;
                }
                else if (Marshal.GetLastWin32Error() == 5)
                {
                    denied++;
                }
            }

            logger.Info($"Asked {asked} windows of {ids.Count} programs to close, {denied} refused.");
            if (asked == 0 && denied > 0)
            {
                return CloseGameResult.Denied;
            }

            if (asked > 0)
            {
                var end = DateTime.UtcNow + wait;
                while (DateTime.UtcNow < end)
                {
                    await Task.Delay(500);
                    if (!ids.Any(IsAlive))
                    {
                        CheckSoon();
                        return CloseGameResult.Closed;
                    }
                }
            }

            return CloseGameResult.StillOpen;
        }

        /// <summary>Ends the programs of the game without asking them. Whatever wasn't saved is lost.</summary>
        public static async Task<CloseGameResult> ForceCloseAsync(Guid gameId)
        {
            var ids = GetProcessIds(gameId);
            var denied = 0;
            foreach (var id in ids)
            {
                var handle = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation, false, id);
                if (handle == IntPtr.Zero)
                {
                    denied += Marshal.GetLastWin32Error() == 5 ? 1 : 0;
                    continue;
                }

                try
                {
                    if (!TerminateProcess(handle, 1) && Marshal.GetLastWin32Error() == 5)
                    {
                        denied++;
                    }
                }
                finally
                {
                    CloseHandle(handle);
                }
            }

            logger.Info($"Forced {ids.Count - denied} of {ids.Count} programs of a game to close.");
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(300);
                if (!ids.Any(IsAlive))
                {
                    CheckSoon();
                    return CloseGameResult.Closed;
                }
            }

            return denied > 0 ? CloseGameResult.Denied : CloseGameResult.StillOpen;
        }

        #endregion Windows
    }
}
