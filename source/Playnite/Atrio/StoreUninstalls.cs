using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.Atrio
{
    /// <summary>An entry of the installed programs of Windows: where a program lives and what uninstalls it.</summary>
    public class WindowsUninstallEntry
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string InstallLocation { get; set; }
        public string UninstallString { get; set; }
        public string QuietUninstallString { get; set; }

        /// <summary>In bytes; Windows keeps it in kilobytes.</summary>
        public long EstimatedSize { get; set; }
    }

    /// <summary>The list that "Installed apps" of Windows shows, read from the registry, both for all users and for this one.</summary>
    public static class WindowsUninstallEntries
    {
        private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        public static List<WindowsUninstallEntry> Read()
        {
            var list = new List<WindowsUninstallEntry>();
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                // Atrio is 32-bit: without asking for both views it would only see half of them.
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (var root = RegistryKey.OpenBaseKey(hive, view))
                        using (var uninstall = root.OpenSubKey(UninstallPath))
                        {
                            if (uninstall == null)
                            {
                                continue;
                            }

                            foreach (var name in uninstall.GetSubKeyNames())
                            {
                                using (var key = uninstall.OpenSubKey(name))
                                {
                                    if (key == null)
                                    {
                                        continue;
                                    }

                                    list.Add(new WindowsUninstallEntry
                                    {
                                        Key = $@"{hive}\{name}",
                                        Name = key.GetValue("DisplayName") as string,
                                        InstallLocation = key.GetValue("InstallLocation") as string,
                                        UninstallString = key.GetValue("UninstallString") as string,
                                        QuietUninstallString = key.GetValue("QuietUninstallString") as string,
                                        EstimatedSize = key.GetValue("EstimatedSize") is int kilobytes ? kilobytes * 1024L : 0
                                    });
                                }
                            }
                        }
                    }
                    catch (Exception e) when (e is System.Security.SecurityException || e is UnauthorizedAccessException || e is IOException)
                    {
                        // A key Atrio can't read isn't one of a game.
                    }
                }
            }

            // The user's keys are the same in both views.
            return list.GroupBy(a => a.Key + "|" + a.UninstallString, StringComparer.OrdinalIgnoreCase).Select(a => a.First()).ToList();
        }

        public static bool SameFolder(string first, string second)
        {
            if (first.IsNullOrWhiteSpace() || second.IsNullOrWhiteSpace())
            {
                return false;
            }

            try
            {
                return string.Equals(
                    Path.GetFullPath(first.Trim().Trim('"')).TrimEnd('\\', '/'),
                    Path.GetFullPath(second.Trim().Trim('"')).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Splits a command of the registry into the program and its arguments, with or without quotes.</summary>
        public static bool TrySplitCommand(string command, out string file, out string arguments)
        {
            file = null;
            arguments = string.Empty;
            var text = command?.Trim();
            if (text.IsNullOrEmpty())
            {
                return false;
            }

            if (text.StartsWith("\""))
            {
                var end = text.IndexOf('"', 1);
                if (end <= 1)
                {
                    return false;
                }

                file = text.Substring(1, end - 1);
                arguments = text.Substring(end + 1).Trim();
            }
            else
            {
                var end = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                {
                    return false;
                }

                file = text.Substring(0, end + 4);
                arguments = text.Substring(end + 4).Trim();
            }

            return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The cleaner the EA app installs with each game, which removes it on its own.</summary>
        public static WindowsUninstallEntry FindEa(IEnumerable<WindowsUninstallEntry> entries, string installDirectory)
        {
            return entries?.FirstOrDefault(a =>
                TrySplitCommand(a.UninstallString, out var file, out var arguments) &&
                Path.GetFileName(file).Equals("Cleanup.exe", StringComparison.OrdinalIgnoreCase) &&
                arguments.IndexOf("uninstall_game", StringComparison.OrdinalIgnoreCase) >= 0 &&
                SameFolder(a.InstallLocation, installDirectory));
        }

        /// <summary>The remover of the Amazon Games app for one game, known by its product or its folder.</summary>
        public static WindowsUninstallEntry FindAmazon(IEnumerable<WindowsUninstallEntry> entries, string productId, string installDirectory)
        {
            return entries?.FirstOrDefault(a =>
                TrySplitCommand(a.UninstallString, out var file, out var arguments) &&
                Path.GetFileName(file).StartsWith("Amazon Game Remover", StringComparison.OrdinalIgnoreCase) &&
                ((!productId.IsNullOrEmpty() && arguments.IndexOf(productId, StringComparison.OrdinalIgnoreCase) >= 0) ||
                 SameFolder(a.InstallLocation, installDirectory)));
        }
    }

    /// <summary>
    /// What the direct uninstalls of the stores share: the question of Atrio, and the usual uninstall of the
    /// store when the direct one can't be done.
    /// </summary>
    public abstract class StoreDirectUninstallController : UninstallController, IDirectUninstall
    {
        protected static readonly ILogger logger = LogManager.GetLogger();
        protected readonly UninstallController store;
        protected readonly CancellationTokenSource watcher = new CancellationTokenSource();
        private bool usesStore;

        protected StoreDirectUninstallController(Game game, UninstallController store) : base(game)
        {
            this.store = store;
            Name = store.Name;
        }

        public virtual string GetQuestion()
        {
            return DirectUninstall.GetQuestion(Game.Name, (long)(Game.InstallSize ?? 0), Game.InstallDirectory);
        }

        protected void Finish(string store)
        {
            logger.Info($"{store} uninstalled {Game.GameId} without its window.");
            InvokeOnUninstalled(new GameUninstalledEventArgs());
        }

        /// <summary>The add-on does its usual uninstall: the store opens and the add-on notices when the game is gone.</summary>
        protected void UseStore(UninstallActionArgs args)
        {
            if (usesStore || watcher.IsCancellationRequested)
            {
                return;
            }

            usesStore = true;
            if (AtrioInfo.GetTestSetting("ATRIO_SIN_TIENDA") != null)
            {
                logger.Info("Test run: the store would open now to uninstall.");
                return;
            }

            store.Uninstalled += (_, e) => InvokeOnUninstalled(e);
            try
            {
                store.Uninstall(args);
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                // Nobody upstream catches it after an await: an exception here would close the program.
                logger.Error(e, $"The store couldn't uninstall {Game.GameId}.");
            }
        }

        public override void Dispose()
        {
            watcher.Cancel();
            store.Dispose();
            base.Dispose();
        }
    }

    /// <summary>
    /// Runs the uninstaller the store registered in Windows for the game, the one "Installed apps" uses:
    /// the cleaner of EA and the remover of Amazon. It's done when Windows forgets the game.
    /// </summary>
    public class RegisteredUninstallController : StoreDirectUninstallController
    {
        private static readonly TimeSpan afterExitPatience = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan noticePatience = TimeSpan.FromSeconds(15);
        private readonly WindowsUninstallEntry entry;
        private readonly string storeName;
        private readonly bool answersDialogs;

        /// <param name="answersDialogs">
        /// The uninstaller asks again what the person already said yes to in Atrio, and says when it's done:
        /// Amazon's. Atrio accepts the first and closes the second, only in the windows of the process it started.
        /// </param>
        public RegisteredUninstallController(Game game, UninstallController store, WindowsUninstallEntry entry, string storeName, bool answersDialogs = false) : base(game, store)
        {
            this.entry = entry;
            this.storeName = storeName;
            this.answersDialogs = answersDialogs;
        }

        private void AnswerDialogs(Process process)
        {
            if (!answersDialogs || process == null || process.HasExited)
            {
                return;
            }

            foreach (var answer in UninstallerDialogs.Answer(process.Id))
            {
                logger.Info($"The uninstaller of {storeName}: {answer}.");
            }
        }

        public override string GetQuestion()
        {
            var size = Game.InstallSize > 0 ? (long)Game.InstallSize.Value : entry.EstimatedSize;
            return DirectUninstall.GetQuestion(Game.Name, size, Game.InstallDirectory.IsNullOrEmpty() ? entry.InstallLocation : Game.InstallDirectory);
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            var command = entry.QuietUninstallString.IsNullOrWhiteSpace() ? entry.UninstallString : entry.QuietUninstallString;
            if (!WindowsUninstallEntries.TrySplitCommand(command, out var file, out var arguments) || !File.Exists(file))
            {
                UseStore(args);
                return;
            }

            // Started here and not after an await: a "no" to the question of Windows for administrators
            // arrives as an error that the editor shows and that puts the game back as it was.
            var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(file)
            });

            logger.Info($"Started the uninstaller {storeName} registered for {Game.GameId}.");
            _ = WatchAsync(process, args);
        }

        private bool IsStillInstalled()
        {
            return WindowsUninstallEntries.Read().Any(a =>
                string.Equals(a.Key, entry.Key, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(a.UninstallString, entry.UninstallString, StringComparison.OrdinalIgnoreCase));
        }

        private async Task WatchAsync(Process process, UninstallActionArgs args)
        {
            try
            {
                DateTime? exited = null;
                while (true)
                {
                    await Task.Delay(answersDialogs ? 500 : 1000, watcher.Token);
                    AnswerDialogs(process);
                    if (!await Task.Run(() => IsStillInstalled(), watcher.Token))
                    {
                        Finish(storeName);

                        // Its "done" notice comes after: it shouldn't stay on the screen.
                        var until = DateTime.Now + noticePatience;
                        while (answersDialogs && process?.HasExited == false && DateTime.Now < until)
                        {
                            await Task.Delay(500, watcher.Token);
                            AnswerDialogs(process);
                        }

                        return;
                    }

                    if (exited == null && process?.HasExited != false)
                    {
                        exited = DateTime.Now;
                    }

                    // The uninstaller ended and the game is still there: cancelled or failed.
                    if (exited != null && DateTime.Now - exited > afterExitPatience)
                    {
                        logger.Warn($"The uninstaller of {storeName} ended and {Game.GameId} is still installed.");
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (watcher.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, $"Failed to watch the uninstall of {Game.GameId}.");
            }
            finally
            {
                process?.Dispose();
            }

            UseStore(args);
        }
    }

    /// <summary>The standard dialogs of Windows (#32770) of one process: questions with OK and notices.</summary>
    public static class UninstallerDialogs
    {
        private const uint WmCommand = 0x0111;
        private const uint WmClose = 0x0010;
        private const int IdOk = 1;

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int size);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetDlgItem(IntPtr dialog, int id);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Accepts the questions (a dialog with an OK button, the one numbered 1 in any language) and closes the
        /// notices (only OK, which Windows numbers as Cancel so that Esc works). Says what it did.
        /// </summary>
        public static List<string> Answer(int processId)
        {
            var done = new List<string>();
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var owner);
                if (owner != processId || !IsWindowVisible(window))
                {
                    return true;
                }

                var name = new System.Text.StringBuilder(64);
                GetClassName(window, name, name.Capacity);
                if (name.ToString() != "#32770")
                {
                    return true;
                }

                var ok = GetDlgItem(window, IdOk);
                if (ok != IntPtr.Zero)
                {
                    if (PostMessage(window, WmCommand, (IntPtr)IdOk, ok))
                    {
                        done.Add("accepted its question");
                    }
                }
                else if (PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero))
                {
                    done.Add("closed its notice");
                }

                return true;
            }, IntPtr.Zero);

            return done;
        }
    }

    /// <summary>Uninstalls with butler, the engine of the itch app, which installed the game in the first place.</summary>
    public class ItchDirectUninstallController : StoreDirectUninstallController
    {
        public ItchDirectUninstallController(Game game, UninstallController store) : base(game, store)
        {
        }

        public override async void Uninstall(UninstallActionArgs args)
        {
            try
            {
                using (var butler = await ButlerClient.StartAsync())
                {
                    if (butler != null)
                    {
                        var caves = (await butler.CallAsync("Fetch.Caves", new { filters = new { gameId = long.Parse(Game.GameId) } }))["items"] as JArray;
                        foreach (var cave in caves ?? new JArray())
                        {
                            watcher.Token.ThrowIfCancellationRequested();
                            await butler.CallAsync("Uninstall.Perform", new { caveId = cave["id"].ToString() });
                        }

                        Finish("itch");
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (watcher.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, $"Failed to uninstall {Game.GameId} with butler.");
            }

            UseStore(args);
        }
    }

    /// <summary>
    /// Removes the package of the game with the package manager of Windows, as Settings > Apps does,
    /// without opening Settings.
    /// </summary>
    public class XboxDirectUninstallController : StoreDirectUninstallController
    {
        private const string ManagerName = "Windows.Management.Deployment.PackageManager, Windows.Management.Deployment, ContentType=WindowsRuntime";
        private const string ExtensionsName = "System.WindowsRuntimeSystemExtensions, System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";

        public XboxDirectUninstallController(Game game, UninstallController store) : base(game, store)
        {
        }

        private static object Read(object target, string property)
        {
            return target.GetType().GetProperty(property).GetValue(target);
        }

        private static object Call(object target, string method, params object[] values)
        {
            var found = target.GetType().GetMethods().First(a => a.Name == method && a.GetParameters().Length == values.Length &&
                a.GetParameters().All(b => b.ParameterType == typeof(string)));
            return found.Invoke(target, values);
        }

        public override async void Uninstall(UninstallActionArgs args)
        {
            try
            {
                var manager = Activator.CreateInstance(Type.GetType(ManagerName, true));
                var packages = ((IEnumerable)Call(manager, "FindPackagesForUser", string.Empty, Game.GameId)).Cast<object>().ToList();
                foreach (var package in packages)
                {
                    var fullName = (string)Read(Read(package, "Id"), "FullName");
                    var remove = manager.GetType().GetMethods().First(a => a.Name == "RemovePackageAsync" &&
                        a.GetParameters().Length == 1 && a.GetParameters()[0].ParameterType == typeof(string));
                    var asTask = Type.GetType(ExtensionsName, true).GetMethods(BindingFlags.Public | BindingFlags.Static).First(a =>
                        a.Name == "AsTask" &&
                        a.GetParameters().Length == 1 &&
                        a.GetParameters()[0].ParameterType.Name == "IAsyncOperationWithProgress`2");

                    // The kinds of the result come from the method: the object Windows gives back is a COM object
                    // and doesn't tell its interfaces to reflection.
                    var types = remove.ReturnType.GetGenericArguments();
                    var operation = remove.Invoke(manager, new object[] { fullName });
                    logger.Info($"Windows told to remove the package {fullName}.");
                    var task = (Task)asTask.MakeGenericMethod(types).Invoke(null, new[] { operation });
                    await task;
                    var result = task.GetType().GetProperty("Result").GetValue(task);
                    if (Read(result, "ExtendedErrorCode") is Exception error)
                    {
                        throw new InvalidOperationException((string)Read(result, "ErrorText"), error);
                    }
                }

                Finish("Windows");
                return;
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, $"Failed to remove the package of {Game.GameId}.");
            }

            UseStore(args);
        }
    }
}
