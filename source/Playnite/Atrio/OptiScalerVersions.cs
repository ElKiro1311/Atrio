using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Playnite.Atrio
{
    /// <summary>
    /// The versions of OptiScaler there are to choose from, by type: the finished ones, the ones being tested, the ones
    /// built every night from the code in progress, and the ones the person added to OptiScaler Client.
    /// </summary>
    public class OptiScalerVersions
    {
        private sealed class Entry
        {
            public string Id;
            public string Channel;
            public bool IsLatest;
        }

        public const string Stable = "stable";
        public const string Beta = "beta";
        public const string Nightly = "nightly";
        public const string Custom = "custom";

        private static readonly string[] order = { Stable, Beta, Nightly, Custom };
        private static readonly Regex numberRegex = new Regex(@"^v?\.?(?<number>\d+(?:\.\d+){1,3})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex testRegex = new Regex(@"[-_. ](pre|preview|alpha|beta|rc|dev|test)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex dateRegex = new Regex(@"(?<!\d)20\d{6}(?!\d)", RegexOptions.Compiled);
        private readonly List<Entry> entries = new List<Entry>();

        /// <summary>The types with something to choose from, always in the same order.</summary>
        public List<string> Channels { get; } = new List<string>();

        /// <summary>From what the bridge says, which comes in the order of OptiScaler Client: the newest ones first.</summary>
        public OptiScalerVersions(JArray versions)
        {
            foreach (var version in versions?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                var id = (string)version["id"];
                var channel = (string)version["channel"];
                if (id.IsNullOrWhiteSpace() || Contains(id) || (channel != Nightly && id.Equals(Nightly, StringComparison.OrdinalIgnoreCase)))
                {
                    // The last one is a tag of the past that was moved from build to build: it has nothing to download anymore.
                    continue;
                }

                entries.Add(new Entry { Id = id, Channel = order.Contains(channel) ? channel : Stable, IsLatest = (bool?)version["latest"] == true });
            }

            Channels.AddRange(order.Where(a => entries.Any(b => b.Channel == a)));
        }

        public bool Contains(string id)
        {
            return Find(id) != null;
        }

        private Entry Find(string id)
        {
            return entries.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>All of a type, newest first.</summary>
        public List<string> Get(string channel)
        {
            return entries.Where(a => a.Channel == channel).Select(a => a.Id).ToList();
        }

        /// <summary>The newest of a type: of the ones the person added, the first one.</summary>
        public string GetLatest(string channel)
        {
            var all = entries.Where(a => a.Channel == channel).ToList();
            return (all.FirstOrDefault(a => a.IsLatest) ?? all.FirstOrDefault())?.Id;
        }

        public bool IsLatest(string id)
        {
            return Find(id)?.IsLatest == true;
        }

        /// <summary>
        /// The type of a version. One that isn't published, because it was put by hand or isn't offered anymore, goes with the
        /// ones it looks like: with a number that isn't finished yet it's of the ones being tested, and ahead of them too, of
        /// the code in progress.
        /// </summary>
        public string GetChannel(string id)
        {
            var known = Find(id);
            if (known != null || id.IsNullOrEmpty())
            {
                return known?.Channel ?? Channels.FirstOrDefault() ?? Stable;
            }

            if (id.IndexOf(Nightly, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Nightly;
            }

            var number = GetNumber(id);
            var finished = Get(Stable).Select(OptiScalerClient.ParseVersion).Where(a => a != null).ToList();
            if (number != null && finished.Count > 0 && number > finished.Max())
            {
                if (Get(Beta).Select(GetNumber).Any(a => a >= number))
                {
                    return Beta;
                }

                if (Channels.Contains(Nightly))
                {
                    return Nightly;
                }
            }

            return testRegex.IsMatch(id) ? Beta : Stable;
        }

        /// <summary>The newest of the same type, when it's newer than the one given. Null otherwise.</summary>
        /// <param name="recommended">What OptiScaler Client recommends: between the finished ones it's the one to go to.</param>
        public string GetUpdate(string id, string recommended)
        {
            var channel = GetChannel(id);
            var latest = channel == Stable && !recommended.IsNullOrEmpty() && GetChannel(recommended) == Stable ? recommended : GetLatest(channel);
            return channel != Custom && IsNewer(latest, id, channel) ? latest : null;
        }

        private bool IsNewer(string id, string than, string channel)
        {
            if (id.IsNullOrEmpty() || than.IsNullOrEmpty() || id.Equals(than, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (channel == Nightly)
            {
                var date = dateRegex.Match(id);
                var known = dateRegex.Match(than);
                return date.Success && known.Success && string.CompareOrdinal(date.Value, known.Value) > 0;
            }

            if (channel == Beta)
            {
                // The number alone says nothing (0.9.5-pre3, 0.9.5-pre4): the list comes in order.
                var all = Get(Beta);
                var position = all.FindIndex(a => a.Equals(than, StringComparison.OrdinalIgnoreCase));
                return position > all.FindIndex(a => a.Equals(id, StringComparison.OrdinalIgnoreCase));
            }

            var newer = OptiScalerClient.ParseVersion(id);
            var current = OptiScalerClient.ParseVersion(than);
            return newer != null && current != null && newer > current;
        }

        /// <summary>The number a version starts with, whatever comes after. Null when it isn't one.</summary>
        private static Version GetNumber(string id)
        {
            var match = numberRegex.Match(id ?? string.Empty);
            return match.Success && Version.TryParse(match.Groups["number"].Value, out var version) ?
                new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision)) : null;
        }
    }
}
