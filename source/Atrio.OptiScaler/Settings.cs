using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Atrio.OptiScaler
{
    /// <summary>One of the settings of OptiScaler.</summary>
    internal sealed class Setting
    {
        public string Section { get; }
        public string Key { get; }
        public string Value { get; }

        public Setting(string section, string key, string value)
        {
            Section = section ?? string.Empty;
            Key = key;
            Value = (value ?? string.Empty).Trim();
        }

        /// <summary>Which one it is, whatever its value.</summary>
        public string Id => GetId(Section, Key);

        public static string GetId(string section, string key)
        {
            return $"{section}|{key}".ToLowerInvariant();
        }

        /// <summary>It's left to OptiScaler.</summary>
        public static bool IsAuto(string value)
        {
            return string.IsNullOrWhiteSpace(value) || value.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
        }

        public override string ToString()
        {
            return $"{Key}={Value}";
        }
    }

    /// <summary>
    /// A file with the settings of OptiScaler, line by line: what isn't changed stays as it's written. The packages bring
    /// «Key=Value» and the game saves «Key = Value», which the client doesn't recognize. Of the ones that are there twice,
    /// OptiScaler takes the last one.
    /// </summary>
    internal sealed class IniFile
    {
        private sealed class Entry
        {
            public int Line;
            public string Section;
            public string Key;
            public string Value;
        }

        // Each byte stays as it is: a path can have been written in any way.
        private static readonly Encoding encoding = Encoding.GetEncoding("iso-8859-1");
        private static readonly string mark = encoding.GetString(Encoding.UTF8.GetPreamble());
        private static readonly Regex defaultValue = new Regex(@"Default \(auto\) is ([^\s,;]+)(?=\s*$|\s+[-=(])", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        private readonly List<string> lines;
        private List<Entry> entries;
        private ILookup<string, Entry> byId;

        private IniFile(IEnumerable<string> lines)
        {
            this.lines = lines.ToList();
        }

        public static IniFile Parse(string text)
        {
            return new IniFile((text ?? string.Empty).Replace("\r\n", "\n").Split('\n'));
        }

        /// <summary>Null when there's none.</summary>
        public static IniFile Read(string path)
        {
            // Reading it as text would take the mark of UTF-8 away, and with it the way its paths are written.
            return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : Parse(encoding.GetString(File.ReadAllBytes(path)));
        }

        /// <summary>Whole or not at all: the game can't start with half of its settings.</summary>
        public void Save(string path)
        {
            var temporary = path + ".atrio-tmp";
            File.WriteAllBytes(temporary, encoding.GetBytes(ToString()));
            try
            {
                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is PlatformNotSupportedException)
            {
                // Not every disk can replace a file by another.
                try
                {
                    File.Copy(temporary, path, true);
                }
                finally
                {
                    File.Delete(temporary);
                }
            }
        }

        private List<Entry> Entries()
        {
            if (entries != null)
            {
                return entries;
            }

            entries = new List<Entry>();
            var section = string.Empty;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (i == 0 && line.StartsWith(mark, StringComparison.Ordinal))
                {
                    line = line.Substring(mark.Length);
                }

                if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                {
                    continue;
                }

                if (line[0] == '[')
                {
                    var end = line.IndexOf(']');
                    section = (end > 0 ? line.Substring(1, end - 1) : line.Substring(1)).Trim();
                    continue;
                }

                var equals = line.IndexOf('=');
                if (equals > 0)
                {
                    entries.Add(new Entry { Line = i, Section = section, Key = line.Substring(0, equals).Trim(), Value = line.Substring(equals + 1).Trim() });
                }
            }

            return entries;
        }

        private IEnumerable<Entry> Find(string section, string key)
        {
            byId = byId ?? Entries().ToLookup(a => Setting.GetId(a.Section, a.Key));
            return byId[Setting.GetId(section, key)];
        }

        private void Changed()
        {
            entries = null;
            byId = null;
        }

        public bool Has(string section, string key)
        {
            return Find(section, key).Any();
        }

        /// <summary>Null when it isn't there.</summary>
        public string Get(string section, string key)
        {
            return Find(section, key).LastOrDefault()?.Value;
        }

        /// <summary>Every one there is, once.</summary>
        public List<Setting> All()
        {
            return Entries().GroupBy(a => Setting.GetId(a.Section, a.Key)).
                Select(a => new Setting(a.First().Section, a.First().Key, a.Last().Value)).ToList();
        }

        public void Set(string section, string key, string value)
        {
            var found = Find(section, key).LastOrDefault();
            if (found != null)
            {
                lines[found.Line] = $"{found.Key}={value}";
                Changed();
                return;
            }

            var all = Entries().Where(a => a.Section.Equals(section, StringComparison.OrdinalIgnoreCase)).ToList();
            var place = all.Count > 0 ? all.Last().Line : lines.FindIndex(a => a.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
            if (place >= 0)
            {
                lines.Insert(place + 1, $"{key}={value}");
                Changed();
                return;
            }

            if (lines.Count > 0 && lines.Last().Trim().Length > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
            lines.Add(string.Empty);
            Changed();
        }

        /// <summary>«Key=Value», and each one once with the value that counts: the way the client can change them.</summary>
        public void Tidy()
        {
            var left = new List<int>();
            foreach (var same in Entries().GroupBy(a => Setting.GetId(a.Section, a.Key)).ToList())
            {
                lines[same.First().Line] = $"{same.First().Key}={same.Last().Value}";
                left.AddRange(same.Skip(1).Select(a => a.Line));
            }

            foreach (var line in left.OrderByDescending(a => a))
            {
                lines.RemoveAt(line);
            }

            Changed();
        }

        /// <summary>What the file says of a setting, right above it.</summary>
        public string Comment(string section, string key)
        {
            var found = Find(section, key).FirstOrDefault();
            var result = new List<string>();
            for (var i = found == null ? -1 : found.Line - 1; i >= 0 && lines[i].TrimStart().StartsWith(";", StringComparison.Ordinal); i--)
            {
                result.Insert(0, lines[i].Trim().TrimStart(';').Trim());
            }

            return string.Join("\n", result);
        }

        /// <summary>The words of what the file says of a setting.</summary>
        public HashSet<string> Words(string section, string key)
        {
            return new HashSet<string>(Regex.Split(Comment(section, key), "[^A-Za-z0-9_]+").Where(a => a.Length > 0), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The value that leaving a setting to OptiScaler is the same as. Null when the file doesn't say it, or it depends.</summary>
        public string Default(string section, string key)
        {
            var found = defaultValue.Match(Comment(section, key));
            return found.Success ? found.Groups[1].Value : null;
        }

        public override string ToString()
        {
            return string.Join("\r\n", lines);
        }
    }

    /// <summary>
    /// What a person changed in the settings of OptiScaler, inside the game or in its file, apart from what Atrio, the client
    /// and OptiScaler itself write there. It's what goes along when the version changes: the client takes everything, and the
    /// settings one version needs, or the ones of something that isn't in the game anymore, stay in all the ones that come after.
    /// </summary>
    internal static class OwnSettings
    {
        public const string Own = "own";

        // What each option of the panel decides.
        private static readonly Dictionary<string, string[]> options = new Dictionary<string, string[]>
        {
            { "output", new[] { "Upscalers|Dx11Upscaler", "Upscalers|Dx12Upscaler", "Upscalers|VulkanUpscaler", "FSR|UpscalerIndex" } },
            { "quality", new[] { "UpscaleRatio|UpscaleRatioOverrideEnabled", "UpscaleRatio|UpscaleRatioOverrideValue" } },
            {
                "frames", new[]
                {
                    "FrameGen|Enabled", "FrameGen|FGInput", "FrameGen|FGOutput", "FrameGen|FGNvngxReplacement", "XeFG|InterpolationCount",
                    "DLSSG|InterpolationCount", "DLSSG|ForceDMFG", "DLSSG|FramerateTargetDMFG", "HUDFix|HUDFix", "OptiFG|HUDFix"
                }
            },
            { "spoofing", new[] { "Spoofing|Dxgi", "Spoofing|StreamlineSpoofing", "Spoofing|Vulkan", "Spoofing|VulkanExtensionSpoofing" } }
        };

        // What the client writes besides: for its own use, or for what it puts next to OptiScaler.
        private static readonly string[] ofTheClient =
        {
            "Log|LogToFile", "Log|LogLevel", "Plugins|LoadAsiPlugins", "Plugins|LoadReshade", "Plugins|LoadCustomAmdxc64OnRdna2",
            "FSR|Fsr4ForceModel", "FSR|Fsr4ForceEnableInt8", "FSR|Fsr4GpuSupport", "FSR|Fsr4Update"
        };

        // The client has its own place for it, where OptiScaler doesn't look: it's of nobody.
        private const string Misplaced = "hudfix|hudfix";

        // What OptiScaler finds out of a game and writes by itself, even without being told to save.
        private static readonly HashSet<string> ofOptiScaler = new HashSet<string> { Misplaced, "xefg|depthinverted", "xefg|jitteredmv", "xefg|highresmv" };
        private const string Dx11 = "upscalers|dx11upscaler";
        private const string Dx12 = "upscalers|dx12upscaler";
        private const string Vulkan = "upscalers|vulkanupscaler";
        private const string FramesEnabled = "framegen|enabled";

        // The same thing under another name, depending on the version. The ones of a list are the same one, the newest at the end.
        private static readonly Dictionary<string, string[][]> names = new Dictionary<string, string[][]>
        {
            { Dx11, new[] { new[] { "fsr31_12", "ffx_12" } } },
            { Dx12, new[] { new[] { "fsr31", "ffx" } } },
            { Vulkan, new[] { new[] { "fsr31", "ffx" }, new[] { "fsr31_12", "ffx_12" } } },
            { "framegen|fginput", new[] { new[] { "nukems", "nvngxfg" } } },
            { "framegen|fgoutput", new[] { new[] { "nukems", "nvngxfg" } } }
        };

        // Their values are words, and each version knows its own: it tells them right above.
        private static readonly HashSet<string> listed = new HashSet<string>
        {
            Dx11, Dx12, Vulkan, "upscalers|rrupscaler", "framegen|fginput", "framegen|fgoutput", "framegen|fgnvngxreplacement"
        };

        private static readonly string[][] places = { new[] { "nukems|makedepthcopy", "nvngxfg|makedepthcopy" } };

        public static IEnumerable<string> Options => options.Keys;

        public static IEnumerable<Setting> OfOption(string option)
        {
            return options[option].Select(a => new Setting(a.Split('|')[0], a.Split('|')[1], "auto"));
        }

        /// <summary>The option of the panel that decides it. Null when there's none.</summary>
        public static string OptionOf(Setting setting)
        {
            return options.Where(a => a.Value.Any(b => b.Equals(setting.Id, StringComparison.OrdinalIgnoreCase))).Select(a => a.Key).FirstOrDefault();
        }

        /// <summary>The client writes it for itself, or for what it puts next to OptiScaler.</summary>
        public static bool IsOfTheClient(Setting setting)
        {
            return ofTheClient.Contains(setting.Id, StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> OtherNames(string id, string value)
        {
            return names.TryGetValue(id, out var groups) ?
                groups.Where(a => a.Contains(value, StringComparer.OrdinalIgnoreCase)).SelectMany(a => a).Where(a => !a.Equals(value, StringComparison.OrdinalIgnoreCase)) :
                Enumerable.Empty<string>();
        }

        private static string NewestName(Setting setting)
        {
            return names.TryGetValue(setting.Id, out var groups) ? groups.FirstOrDefault(a => a.Contains(setting.Value, StringComparer.OrdinalIgnoreCase))?.Last() ?? setting.Value : setting.Value;
        }

        private static bool IsValue(string value, string other)
        {
            return string.Equals(value?.Trim(), other, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The same for OptiScaler, which writes the numbers and some names its own way when it saves.</summary>
        /// <param name="byDefault">The value that leaving it to OptiScaler is the same as, when it's known: it saves it that way.</param>
        public static bool IsSame(Setting one, string other, string byDefault = null)
        {
            var value = Setting.IsAuto(one.Value) ? byDefault ?? string.Empty : one.Value;
            other = Setting.IsAuto(other) ? byDefault ?? string.Empty : other.Trim();
            return value.Equals(other, StringComparison.OrdinalIgnoreCase) || OtherNames(one.Id, value).Contains(other, StringComparer.OrdinalIgnoreCase) ||
                (TryNumber(value, out var number) && TryNumber(other, out var second) && Math.Abs(number - second) < 0.0005);
        }

        private static bool TryNumber(string text, out double number)
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var whole))
            {
                number = whole;
                return true;
            }

            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }

        /// <param name="now">The settings there are.</param>
        /// <param name="left">How they were left: what is the same in both isn't of the person. Null for an OptiScaler nobody else put.</param>
        /// <param name="automatic">The upscaler OptiScaler takes by itself for each kind of game with this graphics card, when it's known.</param>
        public static List<Setting> Find(IniFile now, IniFile left, IDictionary<string, string> automatic = null)
        {
            return now == null ? new List<Setting>() : now.All().Where(a => IsOwn(a, now, left, automatic)).ToList();
        }

        private static bool IsOwn(Setting setting, IniFile now, IniFile left, IDictionary<string, string> automatic)
        {
            return !IsOfNobody(setting, now, left, automatic) && IsChanged(setting, left);
        }

        /// <summary>
        /// What the panel put and is the same as the default, OptiScaler saves as left to it. The other way round it's of the
        /// person: it doesn't save its own defaults, and not every file tells them right.
        /// </summary>
        private static bool IsChanged(Setting setting, IniFile left)
        {
            return !IsSame(setting, left?.Get(setting.Section, setting.Key), Setting.IsAuto(setting.Value) ? left?.Default(setting.Section, setting.Key) : null);
        }

        /// <summary>OptiScaler wrote it by itself: what it used is there when it saves, whatever was chosen.</summary>
        private static bool IsOfNobody(Setting setting, IniFile now, IniFile left, IDictionary<string, string> automatic)
        {
            var before = left?.Get(setting.Section, setting.Key);
            switch (setting.Id)
            {
                case Dx11:
                case Dx12:
                case Vulkan:
                    // The one it falls back to when the chosen one can't be, and the one it takes when none is.
                    string taken = null;
                    if (automatic == null || !automatic.TryGetValue(setting.Id, out taken))
                    {
                        taken = automatic == null ? (left ?? now).Default(setting.Section, setting.Key) : null;
                    }

                    return (setting.Id == Dx12 && IsValue(setting.Value, "fsr21") && !IsValue(before, "fsr21")) ||
                        (Setting.IsAuto(before) && taken != null && IsSame(setting, taken));
                case "framegen|fgnvngxreplacement":
                    return IsValue(setting.Value, "none") && Setting.IsAuto(before);
                case "framegen|fgoutput":
                    // The client writes a name the versions that have the other one don't know.
                    return Setting.IsAuto(setting.Value) && IsValue(before, "nvngxfg");
                case "initflags|disablereactivemask":
                    return IsValue(setting.Value, "false") && Setting.IsAuto(before);
                case "fsr|fsr4gpusupport":
                case "fsr|fsr4forcemodel":
                case "fsr|fsr4forceenableint8":
                    // Each version calls in its own way what the client writes for the FSR 4 it puts.
                    return new[] { "Fsr4ForceModel", "Fsr4ForceEnableInt8" }.Any(a => !Setting.IsAuto(left?.Get("FSR", a)));
                default:
                    return ofOptiScaler.Contains(setting.Id);
            }
        }

        /// <summary>Whether there's something in a file that could be missed, whoever wrote it.</summary>
        public static bool IsWorthKeeping(IniFile now, IniFile left)
        {
            // What the client always writes isn't, when it isn't known how they were left.
            bool isNothing(Setting setting) => left == null && OptionOf(setting) is string option && option != "output" && IsSame(setting, now.Default(setting.Section, setting.Key));
            return now != null && now.All().Any(a => a.Id != Misplaced && !IsOfTheClient(a) && !isNothing(a) && IsChanged(a, left));
        }

        /// <summary>
        /// With the rest of each option in which the person changed something: a ratio is nothing without the one that turns it
        /// on, and that one was written by Atrio.
        /// </summary>
        public static List<Setting> Whole(IEnumerable<Setting> own, IniFile now)
        {
            var result = own.ToList();
            var changed = new HashSet<string>(result.Select(OptionOf).Where(a => a != null));
            var rest = now == null ? Enumerable.Empty<Setting>() :
                now.All().Where(a => a.Id != Misplaced && (!Setting.IsAuto(a.Value) || a.Id == FramesEnabled) && changed.Contains(OptionOf(a) ?? string.Empty) && result.All(b => b.Id != a.Id));
            return result.Concat(rest.ToList()).ToList();
        }

        /// <summary>
        /// What the person has of an option, with the words of OptiScaler: where the frames come from and what makes them, for
        /// instance. Of the upscalers, the one for each kind of game, only the ones they changed.
        /// </summary>
        public static string Summarize(string option, IEnumerable<Setting> own, IniFile now)
        {
            var ids = new HashSet<string>(own.Where(a => option == "output" && OptionOf(a) == option).Select(a => a.Id));
            var all = now.All().Where(a => OptionOf(a) == option && !Setting.IsAuto(a.Value) && (option != "output" || ids.Contains(a.Id))).ToList();
            string value(string key) => all.FirstOrDefault(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Value;
            switch (option)
            {
                case "output":
                    return string.Join(",", all.Where(a => a.Key.EndsWith("Upscaler", StringComparison.OrdinalIgnoreCase)).Select(NewestName).Distinct(StringComparer.OrdinalIgnoreCase));
                case "quality":
                    return IsValue(value("UpscaleRatioOverrideEnabled"), "true") ? value("UpscaleRatioOverrideValue") ?? now.Default("UpscaleRatio", "UpscaleRatioOverrideValue") ?? string.Empty : "auto";
                case "frames":
                    // Left to OptiScaler it's off, except where it follows what the game has of DLSS.
                    var enabled = now.Get("FrameGen", "Enabled");
                    var off = enabled != null && (Setting.IsAuto(enabled) ? !(IsValue(value("FGInput"), "dlssg") && Follows(now)) : !IsValue(enabled, "true"));
                    return off ? "nofg" : string.Join(">", new[] { value("FGInput"), value("FGOutput") }.Where(a => a != null));
                default:
                    return value("Dxgi") ?? "auto";
            }
        }

        /// <summary>In that version, left to OptiScaler the frame generation follows what the game has of DLSS.</summary>
        private static bool Follows(IniFile version)
        {
            return version.Words("FrameGen", "Enabled").Contains("dlssg");
        }

        /// <summary>
        /// How a setting is written in another version, whose own file tells which values it can have. Null when that version
        /// doesn't have that value. A setting that isn't in its file goes as it is: the version that doesn't know it doesn't read
        /// it, and the ones of plugins and of custom upscalers aren't in any file.
        /// </summary>
        /// <param name="was">The file it comes from, which tells the values its own version had. Null when it isn't known.</param>
        public static Setting Fit(Setting setting, IniFile version, IniFile was)
        {
            if (version == null)
            {
                return setting;
            }

            var ids = new[] { setting.Id }.Concat(places.Where(a => a.Contains(setting.Id)).SelectMany(a => a)).Distinct();
            var there = ids.Select(a => version.All().FirstOrDefault(b => b.Id == a)).FirstOrDefault(a => a != null);
            if (there == null)
            {
                return Setting.IsAuto(setting.Value) ? null : setting;
            }

            var value = setting.Value;
            if (setting.Id == FramesEnabled && Setting.IsAuto(value) && was != null && !Follows(was) && Follows(version))
            {
                // There it was off.
                value = "false";
            }
            else if (!Setting.IsAuto(value) && listed.Contains(there.Id))
            {
                var known = version.Words(there.Section, there.Key);
                if (known.Count > 0 && !known.Contains(value))
                {
                    // One with a name of the person's is anything else, in the versions that have them. The same goes for one
                    // that its own version didn't tell either, if it isn't one of those.
                    var other = OtherNames(there.Id, value).FirstOrDefault(known.Contains);
                    var before = was?.Words(setting.Section, setting.Key);
                    if (other == null && !known.Contains("custom") && (before == null || before.Count == 0 || before.Contains(value) || before.Contains("custom")))
                    {
                        return null;
                    }

                    value = other ?? value;
                }
            }

            return new Setting(there.Section, there.Key, value);
        }
    }
}
