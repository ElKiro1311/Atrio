using Microsoft.Win32;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Playnite.Atrio
{
    public class BrowserOption
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    /// <summary>
    /// Where the links of the application open: the browser of Atrio, the one Windows uses for the web
    /// or any other browser of this computer, which is kept as the path of its program.
    /// </summary>
    public static class LinkBrowsers
    {
        public const string Internal = "atrio";
        public const string SystemDefault = "system";

        private const string RegisteredBrowsers = @"SOFTWARE\Clients\StartMenuInternet";

        // A store page can start the client of that store. Any other program a page asks for stays closed.
        private static readonly string[] storeClients =
        {
            "steam", "com.epicgames.launcher", "goggalaxy", "uplay", "origin", "origin2", "battlenet", "amazon-games", "itch"
        };

        public static bool IsWebAddress(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        public static bool IsStoreClientLink(string url)
        {
            var end = url?.IndexOf(':') ?? -1;
            return end > 0 && storeClients.Contains(url.Substring(0, end), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>What the browser of Atrio asks pages for, so that they come in the language of the application.</summary>
        public static string GetAcceptLanguage(string language)
        {
            if (language.IsNullOrEmpty() || !language.Contains("_") || language.StartsWith("en_", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // The browser adds the weights to this list by itself.
            var code = language.Replace('_', '-');
            return $"{code},{code.Split('-')[0]},en";
        }

        /// <summary>The program in the command that Windows keeps for a browser, which can come with its arguments.</summary>
        public static string GetProgram(string command)
        {
            var text = command?.Trim();
            if (text.IsNullOrEmpty())
            {
                return null;
            }

            if (text.StartsWith("\""))
            {
                var end = text.IndexOf('"', 1);
                return end > 1 ? text.Substring(1, end - 1) : null;
            }

            var extension = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return extension > 0 ? text.Substring(0, extension + 4) : text;
        }

        /// <summary>Every place a link can open in, starting with the ones that are always there.</summary>
        public static List<BrowserOption> GetOptions()
        {
            var options = new List<BrowserOption>
            {
                new BrowserOption { Id = Internal, Name = ResourceProvider.GetString("LOCAtrioBrowserInternal") },
                new BrowserOption { Id = SystemDefault, Name = ResourceProvider.GetString("LOCAtrioBrowserSystem") }
            };

            var installed = new List<BrowserOption>();
            foreach (var root in new[]
            {
                Tuple.Create(RegistryHive.CurrentUser, RegistryView.Default),
                Tuple.Create(RegistryHive.LocalMachine, RegistryView.Registry64),
                Tuple.Create(RegistryHive.LocalMachine, RegistryView.Registry32)
            })
            {
                try
                {
                    using (var key = RegistryKey.OpenBaseKey(root.Item1, root.Item2))
                    {
                        installed.AddRange(ReadRegistered(key));
                    }
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException)
                {
                    // A branch of the registry that can't be read only means fewer browsers on the list.
                }
            }

            options.AddRange(installed.
                GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).
                Select(a => a.First()).
                OrderBy(a => a.Name));
            return options;
        }

        private static List<BrowserOption> ReadRegistered(RegistryKey root)
        {
            var result = new List<BrowserOption>();
            using (var clients = root.OpenSubKey(RegisteredBrowsers))
            {
                if (clients == null)
                {
                    return result;
                }

                foreach (var name in clients.GetSubKeyNames())
                {
                    using (var client = clients.OpenSubKey(name))
                    using (var command = client?.OpenSubKey(@"shell\open\command"))
                    {
                        var program = GetProgram(command?.GetValue(null) as string);
                        if (!program.IsNullOrEmpty() && File.Exists(program) &&
                            !Path.GetFileName(program).Equals("iexplore.exe", StringComparison.OrdinalIgnoreCase))
                        {
                            var title = client.GetValue(null) as string;
                            result.Add(new BrowserOption { Id = program, Name = title.IsNullOrEmpty() ? name : title });
                        }
                    }
                }
            }

            return result;
        }
    }
}
