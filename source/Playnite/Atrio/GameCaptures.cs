using Microsoft.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Playnite.Atrio
{
    /// <summary>A screenshot or a clip of a game, made by Steam, Windows or the recorder of the graphics card.</summary>
    public class GameCapture
    {
        public string Path { get; set; }
        public DateTime Date { get; set; }
        public bool IsVideo { get; set; }

        /// <summary>Who made it: Steam, Xbox Game Bar, AMD, NVIDIA, Medal, Discord.</summary>
        public string Source { get; set; }
    }

    /// <summary>Where each program keeps its captures on this computer. The tests give their own folders.</summary>
    public class CapturePlaces
    {
        /// <summary>The userdata folders of every Steam library: one per Steam install.</summary>
        public List<string> SteamUserData { get; set; } = new List<string>();

        /// <summary>The Captures folder of Windows, where the Xbox Game Bar saves.</summary>
        public string GameBar { get; set; }

        /// <summary>The Videos folder of the person.</summary>
        public string Videos { get; set; }

        /// <summary>The folders of this computer, with the places every program uses unless the person moved them.</summary>
        public static CapturePlaces Read()
        {
            var places = new CapturePlaces { Videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) };
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var steam = key?.GetValue("SteamPath") as string;
                    if (!string.IsNullOrWhiteSpace(steam))
                    {
                        places.SteamUserData.Add(System.IO.Path.Combine(steam.Replace('/', '\\'), "userdata"));
                    }
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException || e is IOException || e is UnauthorizedAccessException)
            {
            }

            places.GameBar = GetKnownFolder(new Guid("EDC0FE71-98D8-4F4A-B920-C8DC133CB165")) ??
                (places.Videos == null ? null : System.IO.Path.Combine(places.Videos, "Captures"));
            return places;
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

        private static string GetKnownFolder(Guid id)
        {
            try
            {
                if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var path) == 0)
                {
                    try
                    {
                        return Marshal.PtrToStringUni(path);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(path);
                    }
                }
            }
            catch (Exception e) when (e is EntryPointNotFoundException || e is DllNotFoundException)
            {
            }

            return null;
        }
    }

    /// <summary>
    /// The captures of a game that are already on this computer: it only reads the folders where those programs
    /// save them, never anything of an account. Steam keeps them by the number of the game; the others by its name.
    /// </summary>
    public static class GameCaptures
    {
        public const int MaxCaptures = 300;
        private static readonly TimeSpan listLifetime = TimeSpan.FromMinutes(1);
        private static readonly HashSet<string> imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".jxr" };
        private static readonly HashSet<string> videoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".webm", ".mov", ".avi" };
        private static readonly Regex gameBarName = new Regex(@"^(?<name>.+?) \d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}$", RegexOptions.Compiled);
        private static readonly Regex discordName = new Regex(@"^(?<name>.+?)_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly ConcurrentDictionary<string, Tuple<DateTime, string[]>> listings = new ConcurrentDictionary<string, Tuple<DateTime, string[]>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>"Clair Obscur: Expedition 33™" and "clair obscur expedition 33" are the same name.</summary>
        public static string Normalize(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var text = new StringBuilder();
            var space = false;
            foreach (var c in name.Normalize(NormalizationForm.FormD))
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(c))
                {
                    if (space && text.Length > 0)
                    {
                        text.Append(' ');
                    }

                    text.Append(char.ToLowerInvariant(c));
                    space = false;
                }
                else
                {
                    space = true;
                }
            }

            return text.ToString();
        }

        /// <summary>The whole name and, when it has one, the name without its subtitle or only the subtitle.</summary>
        public static List<string> GetNameVariants(IEnumerable<string> names)
        {
            var result = new HashSet<string>();
            foreach (var name in names ?? Enumerable.Empty<string>())
            {
                void add(string variant)
                {
                    var normal = Normalize(variant);
                    if (normal.Length >= 3)
                    {
                        result.Add(normal);
                    }
                }

                add(name);
                foreach (var separator in new[] { ":", " - ", " – " })
                {
                    var index = name?.IndexOf(separator, StringComparison.Ordinal) ?? -1;
                    if (index > 0)
                    {
                        add(name.Substring(0, index));
                        add(name.Substring(index + separator.Length));
                    }
                }
            }

            return result.ToList();
        }

        /// <summary>The name of the window in a file of the Xbox Game Bar, "Dota 2 2026-11-08 12-34-56".</summary>
        public static string GetGameBarTitle(string fileName)
        {
            var match = gameBarName.Match(fileName ?? string.Empty);
            return match.Success ? match.Groups["name"].Value : null;
        }

        /// <summary>The name in a clip of Discord, "Dota_2_a000bfe5-…": the name is cut after a few letters.</summary>
        public static string GetDiscordTitle(string fileName)
        {
            var match = discordName.Match(fileName ?? string.Empty);
            return match.Success ? match.Groups["name"].Value.Replace('_', ' ') : null;
        }

        /// <summary>Discord cuts the name of the game at this many letters (with "_" for the spaces).</summary>
        public const int DiscordCut = 19;

        /// <summary>
        /// The name of a clip of Discord: equal to one of the names, or, only when Discord cut it, the start of one.
        /// A short name like "Portal" isn't cut, so it doesn't count for "Portal 2".
        /// </summary>
        public static bool StartsOneOf(string cut, List<string> variants)
        {
            var normal = Normalize(cut);
            if (normal.Length < 4)
            {
                return false;
            }

            var wasCut = (cut ?? string.Empty).Length >= DiscordCut;
            return variants.Any(a => a == normal || (wasCut && a.StartsWith(normal, StringComparison.Ordinal)));
        }

        private static string[] List(string folder, bool folders)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return new string[0];
            }

            var key = (folders ? "d|" : "f|") + folder;
            if (listings.TryGetValue(key, out var known) && DateTime.Now - known.Item1 < listLifetime)
            {
                return known.Item2;
            }

            string[] items;
            try
            {
                items = !Directory.Exists(folder) ? new string[0] :
                    folders ? Directory.GetDirectories(folder) : Directory.GetFiles(folder);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                items = new string[0];
            }

            listings[key] = Tuple.Create(DateTime.Now, items);
            return items;
        }

        private static void AddFiles(List<GameCapture> result, IEnumerable<string> files, string source)
        {
            foreach (var file in files)
            {
                var extension = System.IO.Path.GetExtension(file);
                var video = videoExtensions.Contains(extension);
                if (!video && !imageExtensions.Contains(extension))
                {
                    continue;
                }

                DateTime date;
                try
                {
                    date = File.GetLastWriteTime(file);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    continue;
                }

                result.Add(new GameCapture { Path = file, Date = date, IsVideo = video, Source = source });
            }
        }

        /// <summary>Folders inside a folder whose name is the game: the recorders of AMD and Medal, or NVIDIA in Videos.</summary>
        private static IEnumerable<string> GetGameFolders(string parent, List<string> variants)
        {
            return List(parent, true).Where(a => variants.Contains(Normalize(System.IO.Path.GetFileName(a))));
        }

        /// <summary>
        /// The captures of a game, the newest first. Names are those of its copies; numbers are its numbers of Steam.
        /// The names of the other games of the library keep a name without its subtitle from taking their captures:
        /// "Hollow Knight: Silksong" doesn't get those of "Hollow Knight".
        /// </summary>
        public static List<GameCapture> Find(IEnumerable<string> names, IEnumerable<string> steamAppIds, CapturePlaces places, IEnumerable<string> otherNames = null)
        {
            var result = new List<GameCapture>();
            if (places == null)
            {
                return result;
            }

            foreach (var userData in places.SteamUserData ?? new List<string>())
            {
                foreach (var account in List(userData, true))
                {
                    foreach (var appId in (steamAppIds ?? Enumerable.Empty<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct())
                    {
                        var folder = System.IO.Path.Combine(account, "760", "remote", appId, "screenshots");
                        if (Directory.Exists(folder))
                        {
                            AddFiles(result, List(folder, false), "Steam");
                        }
                    }
                }
            }

            var whole = new HashSet<string>((names ?? Enumerable.Empty<string>()).Select(Normalize));
            var others = new HashSet<string>((otherNames ?? Enumerable.Empty<string>()).Select(Normalize));
            others.ExceptWith(whole);
            var variants = GetNameVariants(names).Where(a => whole.Contains(a) || !others.Contains(a)).ToList();
            if (variants.Count > 0)
            {
                AddFiles(result, List(places.GameBar, false).Where(a =>
                {
                    var title = GetGameBarTitle(System.IO.Path.GetFileNameWithoutExtension(a));
                    return title != null && variants.Contains(Normalize(title));
                }), "Xbox Game Bar");

                if (!string.IsNullOrWhiteSpace(places.Videos))
                {
                    foreach (var folder in GetGameFolders(System.IO.Path.Combine(places.Videos, "Radeon ReLive"), variants))
                    {
                        AddFiles(result, List(folder, false), "AMD");
                    }

                    foreach (var folder in GetGameFolders(System.IO.Path.Combine(places.Videos, "Medal", "Clips"), variants))
                    {
                        AddFiles(result, List(folder, false), "Medal");
                    }

                    // NVIDIA saves in a folder named as the game, right in Videos.
                    foreach (var folder in GetGameFolders(places.Videos, variants))
                    {
                        AddFiles(result, List(folder, false), "NVIDIA");
                    }

                    AddFiles(result, List(System.IO.Path.Combine(places.Videos, "Discord Clips"), false).Where(a =>
                    {
                        var title = GetDiscordTitle(System.IO.Path.GetFileNameWithoutExtension(a));
                        return title != null && StartsOneOf(title, variants);
                    }), "Discord");
                }
            }

            return result.
                GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase).
                Select(a => a.First()).
                OrderByDescending(a => a.Date).
                Take(MaxCaptures).
                ToList();
        }
    }
}
