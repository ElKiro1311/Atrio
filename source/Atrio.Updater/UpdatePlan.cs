using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Atrio.Updater
{
    /// <summary>What Atrio asks for before closing. It goes in a file: paths and arguments would not survive a command line.</summary>
    public class UpdatePlan
    {
        /// <summary>The folder of the program.</summary>
        public string Target { get; set; }

        /// <summary>The new version, already unpacked in a folder next to the backup.</summary>
        public string Package { get; set; }
        public string Backup { get; set; }

        /// <summary>Written when the update finished, with the version.</summary>
        public string Marker { get; set; }

        /// <summary>The process that has to close first.</summary>
        public int WaitFor { get; set; }

        /// <summary>The program to open at the end, whether the update worked or not.</summary>
        public string Start { get; set; }
        public string StartArguments { get; set; }
        public string Version { get; set; }

        public string InstallingText { get; set; }
        public string WaitingText { get; set; }
        public string FailedText { get; set; }
        public string StillOpenText { get; set; }

        public void Save(string path)
        {
            var lines = GetType().GetProperties().Select(a => $"{a.Name}={Escape(a.GetValue(this)?.ToString())}");
            File.WriteAllLines(path, lines);
        }

        public static UpdatePlan Load(string path)
        {
            var plan = new UpdatePlan();
            foreach (var line in File.ReadAllLines(path))
            {
                var separator = line.IndexOf('=');
                var property = separator > 0 ? plan.GetType().GetProperty(line.Substring(0, separator)) : null;
                if (property != null)
                {
                    var value = Unescape(line.Substring(separator + 1));
                    property.SetValue(plan, property.PropertyType == typeof(int) ? (object)int.Parse(value) : value);
                }
            }

            return plan;
        }

        private static string Escape(string text)
        {
            return text?.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", string.Empty) ?? string.Empty;
        }

        private static string Unescape(string text)
        {
            var result = new System.Text.StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\\' && i + 1 < text.Length)
                {
                    i++;
                    result.Append(text[i] == 'n' ? '\n' : text[i]);
                }
                else
                {
                    result.Append(text[i]);
                }
            }

            return result.ToString();
        }
    }
}
