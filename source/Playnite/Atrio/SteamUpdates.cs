using Playnite.Common;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Playnite.Atrio
{
    /// <summary>A game of Steam with an update of its client waiting for it.</summary>
    public class SteamPendingUpdate
    {
        public string AppId { get; set; }
        public string Name { get; set; }

        /// <summary>What is still left to download, zero when the client didn't count it yet.</summary>
        public long BytesLeft { get; set; }
        public bool IsUpdating { get; set; }
        public int Percent { get; set; }

        /// <summary>When the client plans to do it by itself, if it says.</summary>
        public DateTime? Scheduled { get; set; }
    }

    /// <summary>
    /// The updates Steam has pending, read from the files its client keeps for each game, and the checking of
    /// the files of a game, which the client does itself. Nothing here talks to any server.
    /// </summary>
    public static class SteamUpdates
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public static List<SteamPendingUpdate> GetPending(IEnumerable<SteamAppManifest> manifests, DateTime nowUtc)
        {
            return (manifests ?? Enumerable.Empty<SteamAppManifest>()).
                Where(a => a != null && a.NeedsUpdate && !a.AppId.IsNullOrEmpty()).
                GroupBy(a => a.AppId).
                Select(a => a.First()).
                Select(a =>
                {
                    DateTime? scheduled = null;
                    if (a.ScheduledAutoUpdate > 0)
                    {
                        var when = DateTimeOffset.FromUnixTimeSeconds(a.ScheduledAutoUpdate).UtcDateTime;
                        if (when > nowUtc)
                        {
                            scheduled = when.ToLocalTime();
                        }
                    }

                    return new SteamPendingUpdate
                    {
                        AppId = a.AppId,
                        Name = a.Name,
                        BytesLeft = Math.Max(0, a.BytesToDownload - a.BytesDownloaded),
                        IsUpdating = a.IsUpdating,
                        Percent = a.Percent,
                        Scheduled = scheduled
                    };
                }).
                OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).
                ToList();
        }

        public static List<SteamPendingUpdate> GetPending()
        {
            try
            {
                return GetPending(SteamClientFiles.ReadAllManifests(), DateTime.UtcNow);
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, "Failed to read the pending updates of Steam.");
                return new List<SteamPendingUpdate>();
            }
        }

        private static HashSet<string> waiting = new HashSet<string>();
        private static System.Threading.Timer timer;

        /// <summary>Raised when the games of Steam with an update waiting change, on a thread of the pool.</summary>
        public static event EventHandler WaitingChanged;

        /// <summary>Some copy of Steam of the game has an update waiting, as Steam said the last time it was looked at.</summary>
        public static bool IsWaiting(IEnumerable<SDK.Models.Game> copies)
        {
            var appId = SteamContent.GetAppId(copies);
            var current = waiting;
            return appId != null && current.Contains(appId);
        }

        /// <summary>
        /// While some game is open, every 20 seconds: an update that arrives while it's open has to wait until it's
        /// closed, and the list says so. With nothing open nothing is read.
        /// </summary>
        public static void Watch(Func<bool> anyOpen)
        {
            if (timer != null)
            {
                return;
            }

            timer = new System.Threading.Timer(_ =>
            {
                try
                {
                    var now = anyOpen() ?
                        new HashSet<string>(GetPending().Select(a => a.AppId)) :
                        new HashSet<string>();
                    if (!now.SetEquals(waiting))
                    {
                        waiting = now;
                        WaitingChanged?.Invoke(null, EventArgs.Empty);
                    }
                }
                catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
                {
                    logger.Debug(e, "Failed to look at the updates of Steam.");
                }
            }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(20));
        }

        public static string GetValidateUrl(string appId) => $"steam://validate/{appId}";

        /// <summary>The client checks every file of the game and downloads again whatever is missing or broken.</summary>
        public static bool Validate(string appId)
        {
            return Open(GetValidateUrl(appId));
        }

        /// <summary>The downloads of the client, where its queue and its pending updates are.</summary>
        public static bool OpenDownloads()
        {
            return Open("steam://open/downloads");
        }

        private static bool Open(string url)
        {
            if (AtrioInfo.GetTestSetting("ATRIO_SIN_TIENDA") != null)
            {
                logger.Info($"Test run: not opening {url}.");
                return true;
            }

            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
                return true;
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, $"Failed to open {url}.");
                return false;
            }
        }
    }
}
