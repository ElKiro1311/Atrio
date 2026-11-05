using Microsoft.Win32;
using Playnite.Common;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Playnite.Atrio
{
    /// <summary>
    /// Brings the library of an existing Playnite into Atrio. Only reads from Playnite's folder:
    /// the original keeps working as it did, with its own copy of everything.
    /// </summary>
    public static class PlayniteImporter
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string SourceOverrideVariable = "ATRIO_PLAYNITE_ORIGEN";

        private static bool HasLibrary(string dir)
        {
            return !dir.IsNullOrEmpty() && File.Exists(Path.Combine(dir, "library", "games.db"));
        }

        private static string GetFolderFromUriHandler()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Playnite\shell\open\command"))
                {
                    var match = Regex.Match(key?.GetValue(string.Empty)?.ToString() ?? string.Empty, "\"([^\"]+)\"");
                    return match.Success ? Path.GetDirectoryName(match.Groups[1].Value) : null;
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to read Playnite's location from the registry.");
                return null;
            }
        }

        /// <summary>Returns the folder that holds a Playnite library, null if there is none.</summary>
        public static string FindPlayniteData()
        {
            var candidates = new List<string> { AtrioInfo.GetTestSetting(SourceOverrideVariable) };
            var programDir = GetFolderFromUriHandler();
            if (!programDir.IsNullOrEmpty())
            {
                // A portable copy keeps its data next to the program, an installed one in AppData.
                candidates.Add(programDir);
            }

            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Playnite"));
            return candidates.FirstOrDefault(a => HasLibrary(a) && !Paths.AreEqual(a, PlaynitePaths.ConfigRootPath));
        }

        public static int CountGames(string source)
        {
            var files = Path.Combine(source, "library", "files");
            return Directory.Exists(files) ? Directory.GetDirectories(files).Length : 0;
        }

        public static bool IsPlayniteRunning()
        {
            // A folder given by hand isn't the one the running Playnite has open.
            if (!AtrioInfo.GetTestSetting(SourceOverrideVariable).IsNullOrEmpty())
            {
                return false;
            }

            return Process.GetProcessesByName("Playnite.DesktopApp").Any() || Process.GetProcessesByName("Playnite.FullscreenApp").Any();
        }

        private static IEnumerable<KeyValuePair<string, string>> GetFolderCopies(string source, string target, string folder)
        {
            var from = Path.Combine(source, folder);
            if (!Directory.Exists(from))
            {
                yield break;
            }

            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                yield return new KeyValuePair<string, string>(file, Path.Combine(target, folder, file.Substring(from.Length + 1)));
            }
        }

        /// <summary>Add-ons and themes that Atrio doesn't ship, so that they keep working after the move.</summary>
        private static IEnumerable<KeyValuePair<string, string>> GetMissingAddons(string source, string target, string folder)
        {
            var from = Path.Combine(source, folder);
            if (!Directory.Exists(from))
            {
                yield break;
            }

            foreach (var addon in Directory.GetDirectories(from))
            {
                var name = Path.GetFileName(addon);
                if (name.Equals(ThemeManager.DefaultThemeDirName, StringComparison.OrdinalIgnoreCase) ||
                    Directory.Exists(Path.Combine(target, folder, name)))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(addon, "*", SearchOption.AllDirectories))
                {
                    yield return new KeyValuePair<string, string>(file, Path.Combine(target, folder, name, file.Substring(addon.Length + 1)));
                }
            }
        }

        public static void Import(string source, Action<int, int> progress)
        {
            var target = PlaynitePaths.ConfigRootPath;
            var copies = new List<KeyValuePair<string, string>>();
            copies.AddRange(GetFolderCopies(source, target, "library"));
            copies.AddRange(GetFolderCopies(source, target, PlaynitePaths.ExtensionsDataDirName));
            copies.AddRange(GetMissingAddons(source, target, PlaynitePaths.ExtensionsDirName));
            copies.AddRange(GetMissingAddons(source, target, Path.Combine(PlaynitePaths.ThemesDirName, "Desktop")));
            copies.AddRange(GetMissingAddons(source, target, Path.Combine(PlaynitePaths.ThemesDirName, "Fullscreen")));

            logger.Info($"Importing {copies.Count} files from {source}");
            var done = 0;
            foreach (var copy in copies)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copy.Value));
                File.Copy(copy.Key, copy.Value, true);
                done++;
                if (done % 25 == 0)
                {
                    progress?.Invoke(done, copies.Count);
                }
            }
        }
    }
}
