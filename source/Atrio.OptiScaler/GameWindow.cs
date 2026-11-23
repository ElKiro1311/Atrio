using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;

namespace Atrio.OptiScaler
{
    /// <summary>
    /// OptiScaler Client with its window, straight to the page of one game: all its options, the ones Atrio doesn't show
    /// and the ones a newer version brings. It presses the same button the person would press in its list of games.
    /// </summary>
    internal static class GameWindow
    {
        private static readonly TimeSpan startPatience = TimeSpan.FromSeconds(60);

        // The client may greet first (a new version, its tour): the page of the game comes when the person closes that.
        private static readonly TimeSpan patience = TimeSpan.FromMinutes(20);
        private static JsonObject wanted;

        public static void Prepare(string game)
        {
            wanted = string.IsNullOrEmpty(game) ? null : JsonNode.Parse(game) as JsonObject;
            if (wanted == null)
            {
                return;
            }

            SkipWelcome();
            AddToList();
            new Thread(Watch) { IsBackground = true, Name = "Atrio" }.Start();
        }

        /// <summary>
        /// The questions of a first time (scan everything, take the tour) are about a list the person didn't open. What the
        /// author says about the program when it's new stays: it isn't Atrio's to hide.
        /// </summary>
        private static void SkipWelcome()
        {
            if (!Client.CanCall("ComponentManagementService", "SaveConfiguration"))
            {
                return;
            }

            var service = Client.New("ComponentManagementService");
            var config = Client.TryGet(service, "Config");
            if (config == null)
            {
                return;
            }

            var changed = false;
            foreach (var name in new[] { "HasShownInitialScanPrompt", "HasSeenQuickTour" })
            {
                if (Client.Has(config, name) && !Client.Flag(config, name))
                {
                    changed |= Client.Set(config, name, true);
                }
            }

            var language = (string)wanted["language"];
            if (changed && !string.IsNullOrEmpty(language) && Client.Text(config, "Language") == "en")
            {
                Client.Set(config, "Language", language);
            }

            if (changed)
            {
                Client.Call(service, "SaveConfiguration");
            }
        }

        /// <summary>With the game in its list, the client finds it there and doesn't ask to scan the whole computer.</summary>
        private static void AddToList()
        {
            try
            {
                Games.Remember(wanted);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                // It's added when its window opens.
                Console.Error.WriteLine(e.Message);
            }
        }

        private static void Watch()
        {
            // Nothing of Avalonia is touched until its window is on the screen: asking earlier, from here, would create
            // its pieces in the wrong thread.
            var limit = DateTime.UtcNow + startPatience;
            var process = Process.GetCurrentProcess();
            while (process.MainWindowHandle == IntPtr.Zero)
            {
                if (DateTime.UtcNow > limit)
                {
                    return;
                }

                Thread.Sleep(100);
                process.Refresh();
            }

            try
            {
                // The client stays open afterwards: it may still be installing, or the person may have gone on to another page.
                var opened = false;
                limit = DateTime.UtcNow + patience;
                for (var attempt = 0; !opened && DateTime.UtcNow < limit; attempt++)
                {
                    Thread.Sleep(attempt == 0 ? 300 : 500);
                    OnWindowThread(() => opened = Open(attempt));
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                // The client stays on its list of games, as when it's opened by hand.
                Console.Error.WriteLine(e.Message);
            }
        }

        private static object MainWindow()
        {
            var application = Client.Get(Avalonia("Avalonia.Application"), "Current");
            var window = Client.TryGet(Client.TryGet(application, "ApplicationLifetime"), "MainWindow");
            return window != null && window.GetType() == Client.FindType("MainWindow") ? window : null;
        }

        private static Type Avalonia(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetName().Name.StartsWith("Avalonia", StringComparison.Ordinal) ? assembly.GetType(name, false) : null;
                if (type != null)
                {
                    return type;
                }
            }

            throw new NotInThisVersionException(name);
        }

        private static void OnWindowThread(Action action)
        {
            Exception problem = null;
            using (var done = new ManualResetEventSlim())
            {
                var dispatcher = Client.Get(Avalonia("Avalonia.Threading.Dispatcher"), "UIThread");
                Client.Call(dispatcher, "Post", "action", new Action(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception e)
                    {
                        problem = e;
                    }
                    finally
                    {
                        done.Set();
                    }
                }));
                done.Wait(TimeSpan.FromSeconds(20));
            }

            if (problem != null)
            {
                throw problem;
            }
        }

        private static bool Open(int attempt)
        {
            var window = MainWindow();
            if (window == null || !Client.Flag(window, "IsVisible"))
            {
                return false;
            }

            // Another window in front (something new in this version, a warning) goes first.
            if (Client.Items(Client.TryGet(window, "OwnedWindows")).Count > 0)
            {
                return false;
            }

            if (!(Client.Get(window, "_games") is IList games))
            {
                throw new NotInThisVersionException("MainWindow._games");
            }

            // What shows may be only a part (a filter, a hidden game): the list is the whole one.
            var all = Client.TryGet(window, "_allGames") as IList ?? games;
            var game = Games.Find(wanted, all);
            if (game == null && attempt < 6)
            {
                // Its list takes a moment to come.
                return false;
            }

            if (game == null)
            {
                game = Games.Create(wanted);
                Client.Call(Client.TryGet(window, "_analyzerService") ?? Client.New("GameAnalyzerService"), "AnalyzeGame", "game", game, "forceRefresh", true);
                all.Insert(0, game);
            }

            // When the page of the game closes, the client saves the list that shows: with everything in it, nothing is lost.
            foreach (var other in all.Cast<object>().ToList())
            {
                if (!games.Contains(other))
                {
                    games.Add(other);
                }
            }

            var button = Activator.CreateInstance(Avalonia("Avalonia.Controls.Button"));
            Client.Set(button, "DataContext", game);
            var arguments = Activator.CreateInstance(Avalonia("Avalonia.Interactivity.RoutedEventArgs"));
            Client.Call(window, "BtnManage_Click", "sender", button, "e", arguments);
            return true;
        }
    }
}
