using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Atrio.Updater
{
    /// <summary>
    /// Puts a new version in place of the program, all or nothing: what it replaces waits in a side folder
    /// and goes back if anything fails. Only the files of the package and the ones the previous version listed
    /// as its own are touched, so the library and the rest of the data of the user stay out of this.
    /// </summary>
    public class UpdateInstaller
    {
        /// <summary>Every file of a version, one per line. It comes in the package.</summary>
        public const string FileList = "atrio-archivos.txt";

        private const string AddonsFolder = "Extensions";
        private const string AddonManifest = "extension.yaml";
        private static readonly Regex addonVersion = new Regex(@"^Version:\s*['""]?(?<version>[0-9.]+)", RegexOptions.Compiled | RegexOptions.Multiline);

        private readonly string target;
        private readonly string package;
        private readonly string backup;
        private readonly List<string> savedFiles = new List<string>();
        private readonly List<string> placedFiles = new List<string>();

        /// <summary>Right after closing, an antivirus or the system can hold a file for a moment.</summary>
        public int Attempts { get; set; } = 40;
        public int AttemptDelay { get; set; } = 250;

        public UpdateInstaller(string target, string package, string backup)
        {
            this.target = target;
            this.package = package;
            this.backup = backup;
        }

        public void Install()
        {
            try
            {
                var newFiles = GetFiles(package);
                var keptAddons = GetNewerAddons();
                var retired = GetRetiredFiles(newFiles).Where(a => !IsInside(a, keptAddons)).ToList();
                retired.ForEach(Save);

                foreach (var file in newFiles.Where(a => !IsInside(a, keptAddons)))
                {
                    Save(file);
                    var destination = Path.Combine(target, file);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    Retry(() => File.Move(Path.Combine(package, file), destination));
                    placedFiles.Add(file);
                }

                RemoveEmptyFolders(retired);
            }
            catch (Exception e)
            {
                var failed = Undo();
                if (failed.Count > 0)
                {
                    throw new IOException($"{e.Message} ({failed.Count} files could not go back: {failed[0]})", e);
                }

                throw;
            }
        }

        private static List<string> GetFiles(string folder)
        {
            var root = folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(a => a.Substring(root.Length)).ToList();
        }

        private static bool IsInside(string file, IEnumerable<string> folders)
        {
            return folders.Any(a => file.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }

        private static Version ReadAddonVersion(string folder)
        {
            var manifest = Path.Combine(folder, AddonManifest);
            if (!File.Exists(manifest))
            {
                return null;
            }

            var match = addonVersion.Match(File.ReadAllText(manifest));
            return match.Success && Version.TryParse(match.Groups["version"].Value, out var version) ? version : null;
        }

        /// <summary>An add-on the user already updated by itself must not go back to the version of the package.</summary>
        private List<string> GetNewerAddons()
        {
            var kept = new List<string>();
            var addons = Path.Combine(package, AddonsFolder);
            if (!Directory.Exists(addons))
            {
                return kept;
            }

            foreach (var folder in Directory.GetDirectories(addons))
            {
                var name = Path.GetFileName(folder);
                var installed = ReadAddonVersion(Path.Combine(target, AddonsFolder, name));
                var packaged = ReadAddonVersion(folder);
                if (installed != null && packaged != null && installed > packaged)
                {
                    kept.Add(Path.Combine(AddonsFolder, name));
                }
            }

            return kept;
        }

        /// <summary>What the previous version had and this one doesn't. Nothing outside of that list goes away.</summary>
        private List<string> GetRetiredFiles(List<string> newFiles)
        {
            var list = Path.Combine(target, FileList);
            if (!File.Exists(list))
            {
                return new List<string>();
            }

            var root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var keep = new HashSet<string>(newFiles, StringComparer.OrdinalIgnoreCase);
            return File.ReadAllLines(list).
                Select(a => a.Trim().Replace('/', Path.DirectorySeparatorChar)).
                Where(a => a.Length > 0 && !Path.IsPathRooted(a) && !keep.Contains(a)).
                Where(a => Path.GetFullPath(Path.Combine(root, a)).StartsWith(root, StringComparison.OrdinalIgnoreCase)).
                Where(a => File.Exists(Path.Combine(target, a))).
                ToList();
        }

        private void Save(string file)
        {
            var current = Path.Combine(target, file);
            if (File.Exists(current))
            {
                var saved = Path.Combine(backup, file);
                Directory.CreateDirectory(Path.GetDirectoryName(saved));
                Retry(() => File.Move(current, saved));
                savedFiles.Add(file);
            }
        }

        private void RemoveEmptyFolders(List<string> retired)
        {
            var folders = retired.Select(a => Path.GetDirectoryName(a)).Where(a => !string.IsNullOrEmpty(a)).Distinct().OrderByDescending(a => a.Length);
            foreach (var folder in folders)
            {
                for (var current = folder; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                {
                    var path = Path.Combine(target, current);
                    if (!Directory.Exists(path) || Directory.EnumerateFileSystemEntries(path).Any())
                    {
                        break;
                    }

                    Directory.Delete(path);
                }
            }
        }

        /// <summary>Puts everything back. Returns what could not be restored.</summary>
        private List<string> Undo()
        {
            var failed = new List<string>();
            foreach (var file in Enumerable.Reverse(placedFiles))
            {
                try
                {
                    Retry(() => File.Delete(Path.Combine(target, file)));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    failed.Add(file);
                }
            }

            foreach (var file in Enumerable.Reverse(savedFiles))
            {
                try
                {
                    var destination = Path.Combine(target, file);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    Retry(() => File.Move(Path.Combine(backup, file), destination));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    failed.Add(file);
                }
            }

            return failed;
        }

        private void Retry(Action action)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    action();
                    return;
                }
                catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && attempt < Attempts)
                {
                    Thread.Sleep(AttemptDelay);
                }
            }
        }
    }
}
