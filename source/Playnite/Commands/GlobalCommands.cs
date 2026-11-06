using Flurl;
using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Playnite.Commands
{
    public static class GlobalCommands
    {
        private static ILogger logger = LogManager.GetLogger();
        internal static PlayniteSettings AppSettings { get; set; }

        /// <summary>Opens an address in the browser of Atrio. False when it isn't possible at that moment.</summary>
        public static Func<string, bool> InAppBrowser { get; set; }

        public static RelayCommand<object> NavigateUrlCommand { get; } = new RelayCommand<object>((url) =>
        {
            try
            {
                NavigateUrl(url);
            }
            catch (Exception e) when (!Debugger.IsAttached)
            {
                logger.Error(e, "Failed to open url.");
            }
        });

        public static RelayCommand<string> NavigateDirectoryCommand { get; } = new RelayCommand<string>((path) =>
        {
            try
            {
                if (AppSettings?.DirectoryOpenCommand?.IsNullOrWhiteSpace() == false)
                {
                    try
                    {
                        ProcessStarter.ShellExecute(AppSettings.DirectoryOpenCommand.Replace("{Dir}", path));
                    }
                    catch (Exception e)
                    {
                        logger.Error(e, "Failed to open directory using custom command.");
                        Process.Start(path)?.Dispose();
                    }
                }
                else
                {
                    Explorer.OpenDirectory(path);
                }
            }
            catch (Exception e) when (!Debugger.IsAttached)
            {
                logger.Error(e, "Failed to open directory.");
            }
        });

        public static void NavigateUrl(object url)
        {
            if (url is string stringUrl)
            {
                NavigateUrl(stringUrl);
            }
            else if (url is Link linkUrl)
            {
                NavigateUrl(linkUrl.Url);
            }
            else if (url is Uri uriUrl)
            {
                NavigateUrl(uriUrl.OriginalString);
            }
            else
            {
                throw new Exception("Unsupported URL format.");
            }
        }

        public static void NavigateUrl(string url)
        {
            if (url.IsNullOrEmpty())
            {
                throw new Exception("No URL was given.");
            }

            if (url.StartsWith("{DocsRootUrl}", StringComparison.OrdinalIgnoreCase))
            {
                url = Playnite.Atrio.AtrioInfo.ProjectUrl;
            }

            url = url.Replace("{AppBranch}", PlayniteEnvironment.AppBranch);
            if (!Regex.IsMatch(url, @"^.*:\/\/"))
            {
                if (Paths.IsFullPath(url))
                {
                    // Do nothing, some people put local file paths to link fields: #2562
                }
                else
                {
                    url = "http://" + url;
                }
            }

            OpenLink(url);
        }

        private static void OpenLink(string url)
        {
            if (Playnite.Atrio.LinkBrowsers.IsWebAddress(url))
            {
                var browser = AppSettings?.LinkBrowser;
                if (browser == Playnite.Atrio.LinkBrowsers.Internal)
                {
                    if (InAppBrowser?.Invoke(url) == true)
                    {
                        return;
                    }
                }
                else if (browser != Playnite.Atrio.LinkBrowsers.SystemDefault && File.Exists(browser))
                {
                    ProcessStarter.StartProcess(browser, $"\"{url.Replace("\"", "%22")}\"")?.Dispose();
                    return;
                }
            }

            ProcessStarter.StartUrl(url);
        }
    }
}
