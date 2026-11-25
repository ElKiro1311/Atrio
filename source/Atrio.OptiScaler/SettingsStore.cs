using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Atrio.OptiScaler
{
    /// <summary>
    /// What Atrio remembers of the settings of a game: how it left them, which tells what the person changed afterwards, the
    /// ones that are waiting for a version that has them, and copies of the files that were replaced. It's of one install, with
    /// the number the client gives it: if the next one is undone, the one before still has its own.
    /// </summary>
    internal sealed class SettingsStore
    {
        private const int CopiesKept = 12;
        private const string Profile = "profile";
        private readonly string game;
        private readonly string folder;

        public SettingsStore(string game)
        {
            this.game = game;
            folder = Path.Combine(Checkpoint.Root, "settings", Checkpoint.NameOf(game));
        }

        private string CopiesPlace => Path.Combine(folder, "copies");

        /// <summary>Where the copies are. Null when there's none.</summary>
        public string Copies => Directory.Exists(CopiesPlace) && Directory.EnumerateFiles(CopiesPlace, "*.ini").Any() ? CopiesPlace : null;

        private string Place(Record record, string type)
        {
            var operation = record.IsOfOptiScaler ? Client.Text(record.Manifest, "OperationId") : null;
            return string.IsNullOrEmpty(operation) ? null : Path.Combine(folder, Clean(operation) + type);
        }

        private static string Clean(string name)
        {
            return string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        }

        private JsonObject Notes(Record record)
        {
            try
            {
                var path = Place(record, ".json");
                return path != null && File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
            }
            catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string Text(object manifest, string name)
        {
            return Convert.ToString(Client.TryGet(manifest, name), CultureInfo.InvariantCulture);
        }

        /// <summary>What the client wrote down of each option of the panel.</summary>
        private static Dictionary<string, string> Applied(Record record)
        {
            return new Dictionary<string, string>
            {
                ["output"] = Text(record.Manifest, "OutputUpscalerBackendApplied"),
                ["quality"] = $"{Text(record.Manifest, "UpscalingQualityPresetApplied")} {Text(record.Manifest, "UpscalingQualityRatioApplied")}",
                ["frames"] = $"{Text(record.Manifest, "FrameGenerationRouteApplied")} {Text(record.Manifest, "FrameGenerationOutputApplied")} {Text(record.Manifest, "MfgModeApplied")}",
                [Profile] = Text(record.Manifest, "AppliedProfileName")
            };
        }

        private void Write(Record record, IniFile left, IEnumerable<Setting> waiting)
        {
            // The notes go last: without them the other one isn't of anything.
            left.Save(Place(record, ".ini"));
            var notes = new JsonObject
            {
                ["game"] = game,
                ["applied"] = new JsonObject(Applied(record).Select(a => KeyValuePair.Create(a.Key, (JsonNode)JsonValue.Create(a.Value)))),
                ["waiting"] = new JsonArray(waiting.Select(a => (JsonNode)new JsonObject { ["section"] = a.Section, ["key"] = a.Key, ["value"] = a.Value }).ToArray())
            };
            File.WriteAllText(Place(record, ".json"), notes.ToJsonString());
        }

        /// <summary>
        /// How they were left. Null when it isn't known: the OptiScaler there is now is of an earlier Atrio, of the client by
        /// itself or of someone's hands.
        /// </summary>
        /// <param name="now">The settings there are, for what the window of the client changed in them since then.</param>
        /// <param name="profile">The settings of the profile there is now.</param>
        public IniFile Left(Record record, IniFile now, Func<IEnumerable<string>> profile)
        {
            var notes = Notes(record);
            var left = notes == null ? null : IniFile.Read(Place(record, ".ini"));
            if (left == null || now == null || !(notes["applied"] is JsonObject before))
            {
                return left;
            }

            // It writes down what is chosen there.
            var applied = Applied(record);
            var changed = applied.Where(a => a.Value != ((string)before[a.Key] ?? string.Empty)).Select(a => a.Key).ToList();
            if (changed.Count == 0)
            {
                return left;
            }

            foreach (var setting in now.All())
            {
                var option = OwnSettings.OptionOf(setting);
                if (option == "output" && changed.Contains(option) && applied[option] == "Default")
                {
                    // With it, the client leaves what there was: it's still of whoever it was.
                    left.Set(setting.Section, setting.Key, "auto");
                }
                else if (option != null && changed.Contains(option))
                {
                    left.Set(setting.Section, setting.Key, setting.Value);
                }
            }

            if (changed.Contains(Profile))
            {
                var ids = new HashSet<string>(profile());
                now.All().Where(a => ids.Contains(a.Id)).ToList().ForEach(a => left.Set(a.Section, a.Key, a.Value));
            }

            // The spoofing isn't written down, but it goes with the rest, and the four at once.
            var spoofing = OwnSettings.OfOption("spoofing").Where(a => now.Has(a.Section, a.Key)).Select(a => new Setting(a.Section, a.Key, now.Get(a.Section, a.Key))).ToList();
            if (spoofing.Select(a => a.Value.ToLowerInvariant()).Distinct().Count() == 1)
            {
                spoofing.ForEach(a => left.Set(a.Section, a.Key, a.Value));
            }

            try
            {
                Write(record, left, Waiting(record).Where(a => !changed.Contains(OwnSettings.OptionOf(a))).ToList());
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // It's found out again the next time.
                Console.Error.WriteLine($"How the settings were left couldn't be written: {e.Message}");
            }

            return left;
        }

        /// <summary>The ones of the person that the version there is doesn't have.</summary>
        public List<Setting> Waiting(Record record)
        {
            return Notes(record)?["waiting"] is JsonArray waiting ?
                waiting.OfType<JsonObject>().Select(a => new Setting((string)a["section"], (string)a["key"], (string)a["value"])).Where(a => !string.IsNullOrEmpty(a.Key)).ToList() :
                new List<Setting>();
        }

        /// <summary>Right after an install: from here on, what changes is of the person.</summary>
        /// <param name="final">The install has ended: what was remembered of the one before isn't needed.</param>
        public void Keep(Record record, IniFile left, IEnumerable<Setting> waiting, bool final)
        {
            var place = Place(record, ".json");
            if (place == null || left == null)
            {
                return;
            }

            Directory.CreateDirectory(folder);
            File.Delete(place);
            Write(record, left, waiting);
            foreach (var other in final ? Remembered().Where(a => !Path.GetFileNameWithoutExtension(a).Equals(Path.GetFileNameWithoutExtension(place), StringComparison.OrdinalIgnoreCase)) : Enumerable.Empty<string>())
            {
                File.Delete(other);
            }
        }

        private List<string> Remembered()
        {
            return Directory.Exists(folder) ? Directory.GetFiles(folder).ToList() : new List<string>();
        }

        /// <summary>What Atrio has just changed in them isn't of the person, and neither is what was chosen in the panel, even when it was already like that.</summary>
        /// <param name="chosen">The options of the panel that were chosen.</param>
        public void Update(Record record, IniFile before, IniFile after, ICollection<string> chosen)
        {
            var left = Notes(record) == null ? null : IniFile.Read(Place(record, ".ini"));
            if (left == null || before == null || after == null)
            {
                return;
            }

            foreach (var setting in after.All().Where(a => a.Value != (before.Get(a.Section, a.Key) ?? "auto") || chosen.Contains(OwnSettings.OptionOf(a))))
            {
                left.Set(setting.Section, setting.Key, setting.Value);
            }

            Write(record, left, Waiting(record).Where(a => !chosen.Contains(OwnSettings.OptionOf(a))).ToList());
        }

        /// <summary>A copy of the file that is about to be replaced or removed.</summary>
        public void Copy(string path, string version)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }

            Directory.CreateDirectory(CopiesPlace);
            var name = Clean($"{DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture)} {version}".Trim());
            var copy = Path.Combine(CopiesPlace, name + ".ini");
            File.WriteAllBytes(copy, File.ReadAllBytes(path));
            foreach (var old in Directory.GetFiles(CopiesPlace, "*.ini").OrderByDescending(a => a, StringComparer.Ordinal).Skip(CopiesKept))
            {
                File.Delete(old);
            }
        }

        /// <summary>OptiScaler went out. The copies stay.</summary>
        public void Forget()
        {
            Remembered().ForEach(File.Delete);
            Checkpoint.RemoveEmpty(new[] { folder, Path.GetDirectoryName(folder), Checkpoint.Root });
        }
    }
}
