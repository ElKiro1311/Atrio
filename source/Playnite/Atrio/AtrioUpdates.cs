using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Net.Http;

namespace Playnite.Atrio
{
    public class AtrioRelease
    {
        public Version Version { get; set; }
        public string Url { get; set; }
    }

    /// <summary>Asks the project's releases page for a newer version. It only informs: nothing is downloaded.</summary>
    public static class AtrioUpdates
    {
        private class GitHubRelease
        {
            [JsonProperty("tag_name")]
            public string TagName { get; set; }

            [JsonProperty("html_url")]
            public string Url { get; set; }

            [JsonProperty("draft")]
            public bool Draft { get; set; }

            [JsonProperty("prerelease")]
            public bool Prerelease { get; set; }
        }

        public static AtrioRelease GetNewerRelease()
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd($"Atrio/{AtrioInfo.Version}");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                var response = client.GetAsync(PlayniteSettings.GetAppConfigValue("AtrioReleasesUrl")).GetAwaiter().GetResult();

                // Answers 404 while there is no published release.
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var release = JsonConvert.DeserializeObject<GitHubRelease>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                if (release == null || release.Draft || release.Prerelease ||
                    !Version.TryParse(release.TagName?.TrimStart('v', 'V'), out var latest))
                {
                    return null;
                }

                return latest > Version.Parse(AtrioInfo.Version) ? new AtrioRelease { Version = latest, Url = release.Url } : null;
            }
        }
    }
}
