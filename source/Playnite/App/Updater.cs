using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.Atrio;
using Playnite.Common;
using Playnite.SDK;

namespace Playnite
{
    public class Updater
    {
        // The panel that shows the notes ignores the colors of the window: without this they are black on dark.
        private const string NotesStyle =
            "<style>body { color: #E8E8E8; font-family: 'Segoe UI'; font-size: 10pt; } " +
            "h1, h2, h3 { color: #FFFFFF; margin: 10px 0 4px 0; } ul { margin: 4px 0 4px 18px; padding: 0; } a { color: #8AB4F8; }</style>";

        private static ILogger logger = LogManager.GetLogger();
        private readonly IPlayniteApplication playniteApp;
        private List<AtrioRelease> newerReleases;

        private List<AtrioRelease> NewerReleases
        {
            get
            {
                if (newerReleases == null)
                {
                    newerReleases = AtrioUpdates.GetNewerReleases(AtrioUpdates.GetReleases(), AtrioInfo.CurrentVersion);
                }

                return newerReleases;
            }
        }

        public bool IsUpdateAvailable => NewerReleases.Count > 0;

        private static Version currentVersion;

        /// <summary>The version add-ons and themes are built against. Atrio has its own, in <see cref="AtrioInfo"/>.</summary>
        public static Version CurrentVersion
        {
            get
            {
                if (currentVersion == null)
                {
                    currentVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                }

                return currentVersion;
            }
        }

        public Updater(IPlayniteApplication app)
        {
            playniteApp = app;
        }

        /// <summary>What changed since the installed version, the newest first.</summary>
        public List<ReleaseNoteData> GetReleaseNotes()
        {
            return NewerReleases.Select(a => new ReleaseNoteData
            {
                Version = a.Version,
                Note = $"<html><head>{NotesStyle}</head><body>{new MarkupConverter().MarkdownToHtml(a.Notes ?? string.Empty)}</body></html>"
            }).ToList();
        }

        public Task DownloadUpdate(Action<int> progressHandler, CancellationToken token)
        {
            return AtrioUpdates.Prepare(NewerReleases[0], progressHandler, token);
        }

        public void InstallUpdate(ApplicationMode mode)
        {
            var fullscreen = mode == ApplicationMode.Fullscreen;
            var options = new CmdLineOptions
            {
                SkipLibUpdate = true,
                MasterInstance = true,
                StartInFullscreen = fullscreen,
                UserDataDir = PlayniteApplication.Current.CmdLine.UserDataDir
            };

            logger.Info($"Installing Atrio {NewerReleases[0].Version} in {PlaynitePaths.ProgramPath}.");
            var plan = AtrioUpdates.CreatePlan(
                NewerReleases[0],
                fullscreen ? PlaynitePaths.FullscreenExecutablePath : PlaynitePaths.DesktopExecutablePath,
                options.ToString());
            playniteApp.QuitAndStart(AtrioUpdates.InstallerPath, $"\"{plan}\"");
        }
    }
}
