using CefSharp;
using CefSameSite = CefSharp.Enums.CookieSameSite;
using Microsoft.Web.WebView2.Core;
using Playnite.Atrio;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Playnite.DesktopApp.Atrio
{
    /// <summary>
    /// The engine of Edge that Windows brings (WebView2) for the browser of Atrio: it plays the videos of the stores
    /// with their sound and carries extensions of Chrome. It has its own folder, apart from the one of the trailers,
    /// because extensions need their own kind of engine.
    /// </summary>
    public static class EdgeBrowser
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static Task<CoreWebView2Environment> environment;
        private static readonly SemaphoreSlim applying = new SemaphoreSlim(1, 1);

        public static string DataFolder => Path.Combine(PlaynitePaths.ConfigRootPath, "browser", "edge");

        /// <summary>The bench sets it to keep the browser away from the folder of the person.</summary>
        internal static string DataFolderOverride { get; set; }

        /// <summary>Extensions turned off in the bench, or on an engine too old for them.</summary>
        public static bool ExtensionsAvailable { get; private set; }

        public static bool IsAvailable()
        {
            if (Environment.GetEnvironmentVariable("ATRIO_NAVEGADOR_CEF") == "1")
            {
                return false;
            }

            try
            {
                return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch (Exception e) when (e is WebView2RuntimeNotFoundException || e is DllNotFoundException || e is BadImageFormatException)
            {
                return false;
            }
        }

        public static Task<CoreWebView2Environment> GetEnvironmentAsync(string language)
        {
            var current = environment;
            if (current == null || current.IsFaulted || current.IsCanceled)
            {
                current = environment = CreateEnvironmentAsync(language);
            }

            return current;
        }

        /// <summary>The picture of the sessions of both browsers, as fingerprints.</summary>
        public static string SessionsPicturePath => Path.Combine(DataFolderOverride ?? DataFolder, "atrio-sesiones.txt");

        private static async Task<CoreWebView2Environment> CreateEnvironmentAsync(string language)
        {
            var folder = DataFolderOverride ?? DataFolder;
            var acceptLanguage = LinkBrowsers.GetAcceptLanguage(language);
            CoreWebView2EnvironmentOptions options(bool extensions)
            {
                var created = new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = extensions };
                if (acceptLanguage != null)
                {
                    created.Language = acceptLanguage.Split(',')[0];
                }

                return created;
            }

            try
            {
                var created = await CoreWebView2Environment.CreateAsync(null, folder, options(true));
                ExtensionsAvailable = true;
                return created;
            }
            catch (Exception e) when (e is ArgumentException || e is NotImplementedException || e is System.Runtime.InteropServices.COMException)
            {
                logger.Warn(e, "The engine of Edge doesn't take extensions, the browser goes without them.");
                ExtensionsAvailable = false;
                return await CoreWebView2Environment.CreateAsync(null, folder, options(false));
            }
        }

        /// <summary>
        /// Leaves in the browser the extensions the person turned on, and only those. A new version of one is
        /// loaded again. True when something was added, so the page can load again with it.
        /// </summary>
        public static async Task<bool> ApplyExtensionsAsync(CoreWebView2 core, Func<IEnumerable<string>> wanted, CancellationToken token)
        {
            if (!ExtensionsAvailable || core == null)
            {
                return false;
            }

            await applying.WaitAsync(token);
            try
            {
                return await ApplyLockedAsync(core, wanted(), token);
            }
            finally
            {
                applying.Release();
            }
        }

        private static async Task<bool> ApplyLockedAsync(CoreWebView2 core, IEnumerable<string> wanted, CancellationToken token)
        {
            var added = false;

            var profile = core.Profile;
            var installed = await profile.GetBrowserExtensionsAsync();
            var wantedIds = new HashSet<string>(wanted ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var state = LoadState();
            foreach (var extension in installed)
            {
                if (!wantedIds.Contains(extension.Id) && BrowserExtensions.Find(extension.Id) != null)
                {
                    await extension.RemoveAsync();
                    state.Remove(extension.Id);
                    logger.Info($"Extension {extension.Name} removed from the browser.");
                }
            }

            foreach (var id in wantedIds)
            {
                string folder;
                try
                {
                    folder = await BrowserExtensions.EnsureAsync(id, core.Environment.BrowserVersionString, token);
                }
                catch (Exception e) when (!token.IsCancellationRequested && !(e is OutOfMemoryException))
                {
                    logger.Error(e, $"Failed to get the extension {id}.");
                    continue;
                }

                if (folder == null)
                {
                    continue;
                }

                var current = installed.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
                if (current != null && state.TryGetValue(id, out var loaded) && string.Equals(loaded, folder, StringComparison.OrdinalIgnoreCase))
                {
                    if (!current.IsEnabled)
                    {
                        await current.EnableAsync(true);
                    }

                    continue;
                }

                try
                {
                    if (current != null)
                    {
                        await current.RemoveAsync();
                    }

                    var loadedNow = await profile.AddBrowserExtensionAsync(folder);
                    state[id] = folder;
                    added = true;
                    logger.Info($"Extension {loadedNow.Name} ({loadedNow.Id}) loaded from {folder}.");
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    logger.Error(e, $"Failed to load the extension {id}.");
                }
            }

            SaveState(state);
            return added;
        }

        private static string StatePath => Path.Combine(DataFolderOverride ?? DataFolder, "atrio-extensiones.txt");

        private static Dictionary<string, string> LoadState()
        {
            var state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(StatePath))
                {
                    foreach (var line in File.ReadAllLines(StatePath))
                    {
                        var parts = line.Split(new[] { '\t' }, 2);
                        if (parts.Length == 2)
                        {
                            state[parts[0]] = parts[1];
                        }
                    }
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Debug(e, "Failed to read the extensions of the browser.");
            }

            return state;
        }

        private static void SaveState(Dictionary<string, string> state)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                File.WriteAllLines(StatePath, state.Select(a => a.Key + "\t" + a.Value));
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Debug(e, "Failed to save the extensions of the browser.");
            }
        }
    }

    /// <summary>
    /// The sessions of the browser of Edge and of the other one, which the friends of Steam, the installs without
    /// the store and the add-ons read. Each side gets what changed on the other since the last time: new cookies,
    /// changed ones and the ones that went away, so signing out also counts. Each side is compared with its own
    /// picture, kept on disk as fingerprints, so a restart doesn't make an old session of one side overwrite a newer
    /// one of the other, and a cookie one engine refuses is never taken as gone.
    /// </summary>
    public class CookieBridge
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly SemaphoreSlim busy = new SemaphoreSlim(1, 1);
        private readonly string picturePath;
        private Dictionary<string, string> lastCef;
        private Dictionary<string, string> lastEdge;
        private bool firstFromEdge = true;

        public CookieBridge(string picturePath)
        {
            this.picturePath = picturePath;
        }

        /// <summary>Both ways: first what changed in Edge, which is where the person signs in, then the rest.</summary>
        public async Task SyncAsync(CoreWebView2CookieManager edge)
        {
            await FromEdgeAsync(edge);
            await ToEdgeAsync(edge);
        }

        /// <summary>From the other browser to Edge.</summary>
        public async Task ToEdgeAsync(CoreWebView2CookieManager edge)
        {
            await busy.WaitAsync();
            try
            {
                LoadPictures();
                var cef = await ReadCefAsync();
                if (cef == null)
                {
                    return;
                }

                var changes = CookieSync.Changes(cef, lastCef);
                lastCef = CookieSync.Picture(cef);
                if (changes.Item1.Count == 0 && changes.Item2.Count == 0)
                {
                    SavePictures();
                    return;
                }

                // The ones that went away first: a cookie can move from its host to its domain, with the same name.
                foreach (var key in changes.Item2)
                {
                    var gone = CookieSync.FromKey(key);
                    try
                    {
                        if (gone != null)
                        {
                            edge.DeleteCookiesWithDomainAndPath(gone.Name, gone.Domain, gone.Path);
                        }
                    }
                    catch (Exception e) when (!(e is OutOfMemoryException))
                    {
                        logger.Debug(e, $"Failed to delete the cookie {gone?.Name} of {gone?.Domain} in Edge.");
                    }
                }

                foreach (var cookie in changes.Item1)
                {
                    try
                    {
                        edge.AddOrUpdateCookie(ToEdge(edge, cookie));
                    }
                    catch (Exception e) when (!(e is OutOfMemoryException))
                    {
                        logger.Debug(e, $"Failed to pass the cookie {cookie.Name} of {cookie.Domain} to Edge.");
                    }
                }

                var edgeNow = (await edge.GetCookiesAsync(null)).Select(FromEdge).ToList();
                CookieSync.Update(lastEdge, changes.Item1, changes.Item2, edgeNow);
                SavePictures();
                logger.Info($"Sessions to Edge: {changes.Item1.Count} cookies, {changes.Item2.Count} gone.");
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Warn(e, "Failed to pass the sessions to the browser of Edge.");
            }
            finally
            {
                busy.Release();
            }
        }

        /// <summary>From Edge to the other browser.</summary>
        public async Task FromEdgeAsync(CoreWebView2CookieManager edge)
        {
            await busy.WaitAsync();
            try
            {
                if (!Cef.IsInitialized.GetValueOrDefault())
                {
                    return;
                }

                LoadPictures();
                var current = (await edge.GetCookiesAsync(null)).Select(FromEdge).ToList();
                var changes = CookieSync.Changes(current, lastEdge);
                if (firstFromEdge)
                {
                    // Edge starts again without the cookies of its last session: that isn't signing out.
                    firstFromEdge = false;
                    changes = Tuple.Create(changes.Item1, CookieSync.WithoutClosedSession(changes.Item2, lastEdge));
                }

                lastEdge = CookieSync.Picture(current);
                if (changes.Item1.Count == 0 && changes.Item2.Count == 0)
                {
                    SavePictures();
                    return;
                }

                var manager = Cef.GetGlobalCookieManager();
                var refused = 0;
                foreach (var key in changes.Item2)
                {
                    var gone = CookieSync.FromKey(key);
                    try
                    {
                        if (gone != null)
                        {
                            // Only that cookie: an expired copy with its domain and path replaces it. Deleting by
                            // name would also take the ones of the same name in the host, the domain or other paths.
                            // HttpOnly, because only a cookie with it can replace one that has it.
                            gone.Value = string.Empty;
                            gone.HttpOnly = true;
                            gone.Expires = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                            await manager.SetCookieAsync(CookieSync.GetUrl(gone), ToCef(gone));
                        }
                    }
                    catch (Exception e) when (!(e is OutOfMemoryException))
                    {
                        logger.Debug(e, $"Failed to delete the cookie {gone?.Name} of {gone?.Domain} from Edge.");
                    }
                }

                foreach (var cookie in changes.Item1)
                {
                    try
                    {
                        // A cookie without HttpOnly can't replace one that has it: the second try keeps it.
                        if (!await manager.SetCookieAsync(CookieSync.GetUrl(cookie), ToCef(cookie)) &&
                            (cookie.HttpOnly || !await manager.SetCookieAsync(CookieSync.GetUrl(cookie), ToCef(cookie, true))))
                        {
                            refused++;
                            logger.Info($"The other browser refused the cookie {cookie.Name} of {cookie.Domain}.");
                        }
                    }
                    catch (Exception e) when (!(e is OutOfMemoryException))
                    {
                        logger.Debug(e, $"Failed to pass the cookie {cookie.Name} of {cookie.Domain} from Edge.");
                    }
                }

                CookieSync.Update(lastCef, changes.Item1, changes.Item2, await ReadCefAsync() ?? new List<BridgeCookie>());
                SavePictures();
                logger.Info($"Sessions from Edge: {changes.Item1.Count} cookies ({refused} refused), {changes.Item2.Count} gone.");
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Warn(e, "Failed to pass the sessions of the browser of Edge.");
            }
            finally
            {
                busy.Release();
            }
        }

        private void LoadPictures()
        {
            if (lastCef != null && lastEdge != null)
            {
                return;
            }

            lastCef = new Dictionary<string, string>();
            lastEdge = new Dictionary<string, string>();
            try
            {
                if (picturePath != null && File.Exists(picturePath))
                {
                    foreach (var line in File.ReadAllLines(picturePath))
                    {
                        var parts = line.Split(new[] { '\t' }, 3);
                        if (parts.Length == 3)
                        {
                            (parts[0] == "E" ? lastEdge : lastCef)[parts[1]] = parts[2];
                        }
                    }
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Debug(e, "Failed to read the picture of the sessions.");
            }
        }

        private void SavePictures()
        {
            if (picturePath == null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(picturePath));
                var lines = lastCef.Where(a => !a.Key.Contains("\t") && !a.Key.Contains("\n")).Select(a => "C\t" + a.Key + "\t" + a.Value).
                    Concat(lastEdge.Where(a => !a.Key.Contains("\t") && !a.Key.Contains("\n")).Select(a => "E\t" + a.Key + "\t" + a.Value));
                var temp = picturePath + ".tmp";
                File.WriteAllLines(temp, lines);
                if (File.Exists(picturePath))
                {
                    File.Delete(picturePath);
                }

                File.Move(temp, picturePath);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Debug(e, "Failed to save the picture of the sessions.");
            }
        }

        internal static async Task<List<BridgeCookie>> ReadCefAsync()
        {
            if (!Cef.IsInitialized.GetValueOrDefault())
            {
                return null;
            }

            var cookies = await Cef.GetGlobalCookieManager().VisitAllCookiesAsync();
            return cookies.Select(a => new BridgeCookie
            {
                Name = a.Name,
                Value = a.Value,
                Domain = a.Domain,
                Path = a.Path.IsNullOrEmpty() ? "/" : a.Path,
                Expires = a.Expires,
                HttpOnly = a.HttpOnly,
                Secure = a.Secure,
                SameSite = a.SameSite == CefSameSite.NoRestriction ? 1 : a.SameSite == CefSameSite.LaxMode ? 2 : a.SameSite == CefSameSite.StrictMode ? 3 : 0
            }).ToList();
        }

        private static BridgeCookie FromEdge(CoreWebView2Cookie a)
        {
            return new BridgeCookie
            {
                Name = a.Name,
                Value = a.Value,
                Domain = a.Domain,
                Path = a.Path.IsNullOrEmpty() ? "/" : a.Path,
                Expires = a.IsSession ? (DateTime?)null : a.Expires,
                HttpOnly = a.IsHttpOnly,
                Secure = a.IsSecure,
                SameSite = a.SameSite == CoreWebView2CookieSameSiteKind.None ? 1 : a.SameSite == CoreWebView2CookieSameSiteKind.Strict ? 3 : 2
            };
        }

        private static CoreWebView2Cookie ToEdge(CoreWebView2CookieManager edge, BridgeCookie a)
        {
            var cookie = edge.CreateCookie(a.Name, a.Value, a.Domain, a.Path);
            cookie.IsHttpOnly = a.HttpOnly;
            cookie.IsSecure = a.Secure;
            if (a.Expires.HasValue)
            {
                cookie.Expires = a.Expires.Value;
            }

            if (a.SameSite == 1)
            {
                cookie.SameSite = CoreWebView2CookieSameSiteKind.None;
                cookie.IsSecure = true;
            }
            else if (a.SameSite == 3)
            {
                cookie.SameSite = CoreWebView2CookieSameSiteKind.Strict;
            }
            else
            {
                cookie.SameSite = CoreWebView2CookieSameSiteKind.Lax;
            }

            return cookie;
        }

        private static CefSharp.Cookie ToCef(BridgeCookie a, bool httpOnly = false)
        {
            return new CefSharp.Cookie
            {
                Name = a.Name,
                Value = a.Value,

                // A cookie of only one host goes without a domain: with one it would reach its subdomains.
                Domain = a.Domain.StartsWith(".") ? a.Domain : null,
                Path = a.Path,
                Expires = a.Expires,
                HttpOnly = a.HttpOnly || httpOnly,
                Secure = a.Secure,
                SameSite = a.SameSite == 1 ? CefSameSite.NoRestriction : a.SameSite == 3 ? CefSameSite.StrictMode : a.SameSite == 2 ? CefSameSite.LaxMode : CefSameSite.Unspecified
            };
        }
    }
}
