#if ATRIO_BANCO
using Playnite.Atrio;
using Playnite.DesktopApp.ViewModels;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Playnite.DesktopApp.Atrio
{
    /// <summary>
    /// Development harness. With the ATRIO_CAPTURA variable set, the app runs off-screen and
    /// follows a script (guion.txt in that folder), rendering its own windows to PNG files.
    /// Nothing is shown on the desktop and nothing outside of the app's windows is captured.
    /// </summary>
    public static class AtrioCaptura
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        public static string Folder => AtrioInfo.GetTestSetting("ATRIO_CAPTURA");
        public static bool IsActive => !string.IsNullOrEmpty(Folder);

        /// <summary>If the script doesn't get to start, leaves a trace of what is on screen. It runs on its own thread.</summary>
        public static void StartWatchdog()
        {
            var thread = new System.Threading.Thread(() =>
            {
                System.Threading.Thread.Sleep(TimeSpan.FromSeconds(18));
                if (File.Exists(Path.Combine(Folder, "guion.log")))
                {
                    return;
                }

                var process = System.Diagnostics.Process.GetCurrentProcess().Id;
                var titles = new List<string>();
                EnumWindows((handle, _) =>
                {
                    GetWindowThreadProcessId(handle, out var owner);
                    if (owner == process && IsWindowVisible(handle))
                    {
                        var text = new StringBuilder(256);
                        GetWindowText(handle, text, 256);
                        titles.Add(text.ToString());
                    }

                    return true;
                }, IntPtr.Zero);
                File.WriteAllLines(Path.Combine(Folder, "ventanas.log"), titles);
            })
            { IsBackground = true };
            thread.Start();
        }

        private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr handle, out int processId);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr handle);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr handle, StringBuilder text, int length);

        public static void PrepareWindow(Window window)
        {
            StartWatchdog();
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.Width = GetSize("ATRIO_CAPTURA_ANCHO", 1720);
            window.Height = GetSize("ATRIO_CAPTURA_ALTO", 1000);
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
        }

        private static int GetSize(string variable, int fallback)
        {
            return int.TryParse(Environment.GetEnvironmentVariable(variable), out var value) ? value : fallback;
        }

        private static void Log(string message)
        {
            File.AppendAllText(Path.Combine(Folder, "guion.log"), $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        }

        public static async void Run(DesktopAppViewModel model)
        {
            try
            {
                foreach (var rawLine in File.ReadAllLines(Path.Combine(Folder, "guion.txt")))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                    {
                        continue;
                    }

                    Log("> " + line);
                    var parts = line.Split(new char[] { ' ' }, 2);
                    var argument = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                    try
                    {
                        if (parts[0] == "salir")
                        {
                            break;
                        }

                        await Execute(model, parts[0], argument);
                    }
                    catch (Exception e)
                    {
                        Log("ERROR " + e);
                    }

                    await Task.Delay(100);
                }
            }
            catch (Exception e)
            {
                Log("FATAL " + e);
            }

            Log("fin");
            PlayniteApplication.Current.Quit();
        }

        private static async Task Execute(DesktopAppViewModel model, string command, string argument)
        {
            switch (command)
            {
                case "espera":
                    await Task.Delay(int.Parse(argument));
                    break;
                case "captura":
                    Render(model.Window.Window, argument);
                    break;
                case "dialogo":
                    var dialog = Application.Current.Windows.Cast<Window>().LastOrDefault(a => a != model.Window.Window && a.IsVisible);
                    if (dialog == null)
                    {
                        Log("sin dialogo");
                    }
                    else
                    {
                        Render(dialog, argument);
                    }

                    break;
                case "cerrardialogo":
                    Application.Current.Windows.Cast<Window>().Where(a => a != model.Window.Window && a.IsVisible).ToList().ForEach(a => a.Close());
                    break;
                case "emergente":
                    RenderPopups(argument);
                    break;
                case "vista":
                    model.AppSettings.ViewSettings.GamesViewType = (DesktopView)Enum.Parse(typeof(DesktopView), argument);
                    break;
                case "juego":
                    var entry = model.GamesView.Items.FirstOrDefault(a => a.Name == argument) ??
                                model.GamesView.Items.FirstOrDefault(a => a.Name.IndexOf(argument, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (entry == null)
                    {
                        Log("juego no encontrado");
                    }
                    else
                    {
                        model.OpenGame(entry.Id);
                    }

                    break;
                case "selector":
                    FindAll<System.Windows.Controls.Primitives.ToggleButton>(model.Window.Window).First(a => a.Name == "Picker").IsChecked = argument == "si";
                    break;
                case "panel":
                    model.SwitchToLibraryView();
                    model.Pane = argument == "juego" ? LibraryPane.Game : argument == "todos" ? LibraryPane.AllGames : LibraryPane.Home;
                    break;
                case "barra":
                    var item = model.SidebarItems.FirstOrDefault(a => a.Title == argument);
                    if (item == null)
                    {
                        Log("barra: " + string.Join(", ", model.SidebarItems.Select(a => a.Title)));
                    }
                    else
                    {
                        item.Command.Execute(null);
                    }

                    break;
                case "filtro":
                    model.AppSettings.FilterSettings.Name = argument;
                    break;
                case "tienda":
                    model.StoreFilter.Refresh();
                    var store = model.StoreFilter.Items.FirstOrDefault(a => a.Name == argument) ??
                                (argument == "Todas" ? model.StoreFilter.Items.FirstOrDefault() : null);
                    model.StoreFilter.SelectCommand.Execute(store);
                    break;
                case "preguntar":
                    // Shows the store selector of a game with several copies. Only used with games that
                    // are not installed anywhere and always closed without choosing, so nothing starts.
                    var group = GameUnifier.Current.GetAllGroups().FirstOrDefault(a => a.Primary.Name == argument);
                    if (group == null || group.Copies.Any(a => a.IsInstalled))
                    {
                        Log("no sirve para preguntar");
                    }
                    else
                    {
                        _ = Application.Current.Dispatcher.BeginInvoke(new Action(() => model.StartGame(group.Primary, true)));
                    }

                    break;
                case "clicportada":
                    ClickFirstCover(model);
                    break;
                case "pulsa":
                    PressButton(model, argument);
                    break;
                case "pulsadialogo":
                    // The button opens a modal window: the script has to go on while it is open.
                    _ = Application.Current.Dispatcher.BeginInvoke(new Action(() => PressButton(model, argument)));
                    break;
                case "instalados":
                    model.StoreFilter.InstalledOnly = argument == "si";
                    Log("visibles: " + model.GamesView.CollectionView.Count);
                    break;
                case "menu":
                    LogGameMenu(model);
                    break;
                case "tema":
                    if (model.AppSettings.Theme == argument)
                    {
                        Log("tema aplicado tras reiniciar: " + argument);
                    }
                    else
                    {
                        var themes = new ThemesViewModel(model);
                        themes.Refresh();
                        themes.Apply(themes.Families.SelectMany(a => a.Themes).First(a => a.Id == argument));
                    }

                    break;
                case "web":
                    // Same component that shows the login page of every store.
                    using (var view = PlayniteApplication.Current.PlayniteApiGlobal.WebViews.CreateOffscreenView())
                    {
                        view.NavigateAndWait("data:text/html,<html><body>navegador-ok</body></html>");
                        Log("navegador: " + view.GetPageText());
                    }

                    break;
                case "ajustes":
                    _ = Application.Current.Dispatcher.BeginInvoke(new Action(() => model.OpenSettingsCommand.Execute(null)));
                    break;
                case "acerca":
                    _ = Application.Current.Dispatcher.BeginInvoke(new Action(() => model.OpenAboutCommand.Execute(null)));
                    break;
                case "acento":
                    ThemesViewModel.ApplyAccent(argument);
                    break;
                case "ajuste":
                    SetProperty(model.AppSettings, argument);
                    break;
                case "ejecuta":
                    Invoke(model, argument);
                    break;
                case "informe":
                    WriteReport(model, argument);
                    break;
                case "catalogo":
                    await Task.Run(() => CheckCatalog(argument));
                    break;
                case "emuladores":
                    await Task.Run(() => ImportTestEmulators(model, argument));
                    break;
                case "filtroemu":
                    model.StoreFilter.ShowEmulator(model.Database.Emulators.First(a => a.Name == argument).Id);
                    Log("visibles: " + model.GamesView.CollectionView.Count);
                    break;
                case "chips":
                    model.StoreFilter.Refresh();
                    Log("chips: " + string.Join(", ", model.StoreFilter.Items.Select(a => a.Name + " " + a.CountText)));
                    break;
                case "logros":
                    await RefreshAchievements(model, argument);
                    break;
                case "buscarnombre":
                    FindProductName(model);
                    break;
                case "revelar":
                    var cards = FindAll<System.Windows.Controls.Primitives.ToggleButton>(model.Window.Window).
                        Where(a => a.DataContext?.GetType().Name == "AchievementDisplayItem").ToList();
                    Log("tarjetas de logros: " + cards.Count);
                    cards[int.Parse(argument)].IsChecked = true;
                    break;
                case "medir":
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var measured = argument.Split(new char[] { ' ' }, 2);
                    await Execute(model, measured[0], measured.Length > 1 ? measured[1] : string.Empty);
                    await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Log($"tiempo [{argument}]: {clock.ElapsedMilliseconds} ms");
                    break;
                case "sonda":
                    await WatchUiThread(int.Parse(argument));
                    break;
                case "sincronizar":
                    _ = model.UpdateLibrary(false, true, false);
                    break;
                case "temporizador":
                    // Same thread as the real timer, which fires every few hours.
                    var callback = typeof(PlayniteApplication).GetMethod("UpdateCheckerCallback", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    await Task.Run(() => callback.Invoke(model.App, new object[] { null }));
                    await Task.Delay(1500);
                    while (GlobalTaskHandler.IsActive)
                    {
                        await Task.Delay(250);
                    }

                    Log("ultima sincronizacion: " + model.AppSettings.LastLibraryUpdateCheck.ToString("s"));
                    break;
                case "periodico":
                    await model.ProcessPeriodicLibUpdate();
                    Log("ultima sincronizacion: " + model.AppSettings.LastLibraryUpdateCheck.ToString("s"));
                    break;
                case "jugando":
                    if (argument == "si")
                    {
                        model.App.GamesEditor.RunningGames[Guid.NewGuid()] = null;
                    }
                    else
                    {
                        model.App.GamesEditor.RunningGames.Clear();
                    }
                    break;
                case "combo":
                    var boxes = FindAll<System.Windows.Controls.ComboBox>(model.Window.Window).Where(a => a.IsVisible).ToList();
                    Log($"listas visibles: {boxes.Count}, opciones: {string.Join(" | ", boxes[0].Items.Cast<object>().Select(a => a.ToString()))}");
                    boxes[0].SelectedIndex = int.Parse(argument);
                    Log($"frecuencia: tiendas {model.AppSettings.CheckForLibraryUpdates}, emuladores {model.AppSettings.CheckForEmulatedLibraryUpdates}");
                    break;
                case "menulogros":
                    var menuParts = argument.Split('|');
                    var refreshButton = FindAll<System.Windows.Controls.Button>(model.Window.Window).First(a => a.ContextMenu != null && a.Content as string == menuParts[0]);
                    var refreshItems = refreshButton.ContextMenu.Items.Cast<System.Windows.Controls.MenuItem>().ToList();
                    Log("opciones de actualizar: " + string.Join(" | ", refreshItems.Select(a => a.Header)));
                    if (menuParts.Length > 1)
                    {
                        refreshItems.First(a => a.Header as string == menuParts[1]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                        refreshButton.ContextMenu.IsOpen = false;
                    }
                    break;
                case "sondajuego":
                    var watch = WatchUiThread(3500);
                    await Task.Delay(300);
                    await Execute(model, "juego", argument);
                    await watch;
                    break;
                case "detalles":
                    model.SelectedGameDetails.DetailsExpanded = argument == "si";
                    break;
                case "radio":
                    FindAll<System.Windows.Controls.RadioButton>(model.Window.Window).First(a => a.Name == argument).IsChecked = true;
                    break;
                case "pestana":
                    model.SelectedGameDetails.ShowAchievements = argument == "si";
                    break;
                case "memoria":
                    var process = System.Diagnostics.Process.GetCurrentProcess();
                    Log($"memoria: trabajo {process.WorkingSet64 / 1048576} MB, privada {process.PrivateMemorySize64 / 1048576} MB, virtual {process.VirtualMemorySize64 / 1048576} MB, administrada {GC.GetTotalMemory(false) / 1048576} MB");
                    break;
                case "ventanas":
                    Log("ventanas abiertas: " + string.Join(" | ", Application.Current.Windows.Cast<Window>().Select(a => a.GetType().Name + (a.IsVisible ? "" : " (oculta)"))));
                    break;
                case "estado":
                    Log($"panel: {model.Pane}, vista: {model.AppSettings.ViewSettings.GamesViewType}, grupo: {model.AppSettings.ViewSettings.GroupingOrder}, " +
                        $"ficha: {model.SelectedGameDetails?.Game?.Name}, seleccion: {model.SelectedGame?.Name}, pestana: {model.SelectedGameDetails?.ShowAchievements}, " +
                        $"visibles: {model.GamesView.CollectionView.Count}, selector: {model.StoreFilter.SelectionText}, bibliotecas: {model.StoreFilter.Items?.Count}");
                    break;
                case "esc":
                    Log("esc disponible: " + model.LeaveGamePageCommand.CanExecute(null));
                    if (model.LeaveGamePageCommand.CanExecute(null))
                    {
                        model.LeaveGamePageCommand.Execute(null);
                    }

                    break;
                case "vistatipo":
                    model.AppSettings.ViewSettings.GamesViewType = (DesktopView)Enum.Parse(typeof(DesktopView), argument);
                    break;
                case "agrupar":
                    model.AppSettings.ViewSettings.GroupingOrder = (GroupableField)Enum.Parse(typeof(GroupableField), argument);
                    Log($"por instalados: {model.StoreFilter.GroupByInstalled}, por tienda: {model.StoreFilter.GroupByStore}");
                    break;
                case "ordenar":
                    model.AppSettings.ViewSettings.SortingOrder = (SortOrder)Enum.Parse(typeof(SortOrder), argument);
                    Log($"recientes primero: {model.StoreFilter.RecentFirst}");
                    break;
                case "texto":
                    model.AppSettings.FilterSettings.Name = argument;
                    break;
                case "instalacion":
                    var changedGame = model.Database.Games.First(a => a.Name == argument);
                    changedGame.IsInstalled = !changedGame.IsInstalled;
                    model.Database.Games.Update(changedGame);
                    break;
                case "jugada":
                    var playedGame = model.Database.Games.First(a => a.Name == argument);
                    playedGame.Playtime += 3600;
                    playedGame.LastActivity = DateTime.Now;
                    model.Database.Games.Update(playedGame);
                    Log($"jugado: {model.SelectedGameDetails?.PlayedText}, actividad: {model.SelectedGameDetails?.Activity?.Count}");
                    break;
                case "nuevo":
                    model.Database.Games.Add(new Playnite.SDK.Models.Game(argument));
                    break;
                case "quitar":
                    model.Database.Games.Remove(model.Database.Games.First(a => a.Name == argument));
                    break;
                case "preset":
                    model.ActiveFilterPreset = model.SortedFilterPresets.First(a => a.Name == argument || argument.IsNullOrEmpty());
                    break;
                case "clicgrupo":
                    ClickInList<System.Windows.Controls.GroupItem>(model);
                    break;
                case "clicjuego":
                    ClickInList<System.Windows.Controls.ListBoxItem>(model);
                    break;
                case "pulsapanel":
                    var paneButton = FindAll<System.Windows.Controls.Primitives.ToggleButton>(model.Window.Window).
                        First(a => a.IsVisible && a.Command == (argument == "todos" ? model.ShowAllGamesCommand : model.ShowHomeCommand));
                    ((System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(paneButton)).Toggle();
                    paneButton.Command.Execute(null);
                    await Task.Delay(200);
                    Log($"boton marcado: {paneButton.IsChecked}, panel: {model.Pane}");
                    break;
                case "fila":
                    var row = FindAll<Playnite.DesktopApp.Controls.Views.Sidebar>(model.Window.Window).First();
                    Log("no caben: " + string.Join(", ", row.GetHiddenItems().Select(a => a.Title)));
                    if (argument == "mas")
                    {
                        row.ShowHiddenItems();
                    }

                    break;
                case "progreso":
                    model.ProgressStatus = argument;
                    model.ProgressTotal = 100;
                    model.ProgressValue = 40;
                    model.ProgressActive = argument.Length > 0;
                    break;
                case "filtroboton":
                    var filterButton = FindAll<System.Windows.Controls.Primitives.ToggleButton>(model.Window.Window).
                        First(a => a.IsVisible && a.InputBindings.Count > 0);
                    Log($"filtro activo: {filterButton.Tag}, panel abierto: {filterButton.IsChecked}");
                    if (argument == "quitar")
                    {
                        ((MouseBinding)filterButton.InputBindings[0]).Command.Execute(null);
                    }

                    break;
                case "menuvista":
                    var mainMenu = new Playnite.DesktopApp.Controls.MainMenu(model);
                    foreach (var viewEntry in mainMenu.Items.OfType<System.Windows.Controls.MenuItem>().Where(a => a.Header as string == ResourceProvider.GetString("LOCMenuView")))
                    {
                        Log("vista: " + string.Join(", ", viewEntry.Items.OfType<System.Windows.Controls.Control>().Select(a => (a as System.Windows.Controls.MenuItem)?.Header + ":" + a.Visibility)));
                    }

                    break;
                case "fondo":
                    var pictures = FindAll<Playnite.Controls.FadeImage>(model.Window.Window).Where(a => a.IsVisible && a.ActualWidth > 300).ToList();
                    Log("fondos: " + string.Join(", ", pictures.Select(a => (a.Source != null) + "/" + (int)a.ActualWidth)));
                    break;
                case "logrosdemo":
                    SeedAchievements(model, argument);
                    break;
                case "descargar":
                    await Task.Run(() => DownloadAddon(model, argument));
                    break;
                case "complementos":
                    Log("complementos: " + string.Join(", ", Playnite.Plugins.ExtensionFactory.GetInstalledManifests().Select(a => a.Id)));
                    break;
                case "avisos":
                    Log("avisos: " + model.App.Notifications.Messages.Count);
                    foreach (var message in model.App.Notifications.Messages)
                    {
                        Log($"  [{message.Type}] {message.Text}");
                    }

                    break;
                case "demo":
                    LoadDemo(model, argument);
                    break;
                default:
                    Log("orden desconocida");
                    break;
            }
        }

        /// <summary>Reads the add-on database the way the add-on browser does, and tries every installer manifest.</summary>
        private static void CheckCatalog(string limit)
        {
            var client = new Playnite.Services.ServicesClient();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var all = new List<AddonManifest>();
            foreach (AddonType type in Enum.GetValues(typeof(AddonType)))
            {
                var found = client.GetAllAddons(type, null);
                all.AddRange(found);
                Log($"{type}: {found.Count}");
            }

            Log($"catalogo cargado en {watch.ElapsedMilliseconds} ms");
            Log("busqueda steam: " + client.GetAllAddons(AddonType.GameLibrary, "steam").Count);
            Log("bloqueados: " + client.GetAddonBlacklist().Length);

            var max = int.TryParse(limit, out var value) ? value : all.Count;
            var good = 0;
            var empty = 0;
            var failed = 0;
            System.Threading.Tasks.Parallel.ForEach(all.Take(max), new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 6 }, addon =>
            {
                try
                {
                    var installer = client.GetAddonInstaller(addon.AddonId);
                    if (installer?.Packages?.Count > 0)
                    {
                        System.Threading.Interlocked.Increment(ref good);
                    }
                    else
                    {
                        System.Threading.Interlocked.Increment(ref empty);
                        Log($"SIN PAQUETES {addon.Name}");
                    }
                }
                catch (Exception e)
                {
                    System.Threading.Interlocked.Increment(ref failed);
                    Log($"FALLO {addon.Name}: {e.GetType().Name} {e.Message}");
                }
            });

            Log($"instaladores: {good} bien, {empty} vacios, {failed} con error, {watch.ElapsedMilliseconds} ms");
        }

        /// <summary>
        /// The two steps of the Stores page without their dialogs: finds the emulators of a folder
        /// (emuladores/) and imports the games of roms/ with the first profile of each.
        /// </summary>
        private static void ImportTestEmulators(DesktopAppViewModel model, string folder)
        {
            var database = model.Database;
            var token = new System.Threading.CancellationToken();
            var found = Playnite.Emulators.EmulatorScanner.SearchForEmulators(
                Path.Combine(folder, "emuladores"), Playnite.Emulators.Emulation.Definitions, token);
            Log("emuladores: " + string.Join(", ", found.Select(a => a.Name)));
            foreach (var scanned in found)
            {
                var emulator = EmulatorStores.CreateEmulator(scanned);
                database.Emulators.Add(emulator);
                var romFolder = Path.Combine(folder, "roms", scanned.Name);
                if (!Directory.Exists(romFolder))
                {
                    continue;
                }

                var config = new Playnite.SDK.Models.GameScannerConfig
                {
                    Name = emulator.Name,
                    EmulatorId = emulator.Id,
                    EmulatorProfileId = emulator.AllProfiles.First().Id,
                    Directory = romFolder
                };

                var games = new Playnite.Emulators.GameScanner(config, database).Scan(token, out var platforms, out var regions);
                foreach (var scannedGame in games)
                {
                    database.Games.Add(scannedGame.ToGame());
                }

                Log($"{emulator.Name}: {games.Count} juegos");
            }
        }

        /// <summary>Asks the achievements add-on to read one game, the same as its own menu entry.</summary>
        private static async Task RefreshAchievements(DesktopAppViewModel model, string gameName)
        {
            var plugin = model.Extensions.Plugins[AtrioInfo.AchievementsPluginId].Plugin;
            var game = model.Database.Games.First(a => a.Name.IndexOf(gameName, StringComparison.OrdinalIgnoreCase) >= 0 && a.PluginId != Guid.Empty);
            var method = plugin.GetType().GetMethod("RequestSingleGameRefreshAsync");
            await (Task)method.Invoke(plugin, new object[] { game.Id });
            Log("logros pedidos para " + game.Name);
        }

        /// <summary>Looks for the name of the base app in every loaded text and in what the window shows.</summary>
        private static void FindProductName(DesktopAppViewModel model)
        {
            var found = new List<string>();
            void Walk(ResourceDictionary dictionary)
            {
                foreach (var key in dictionary.Keys)
                {
                    if (dictionary[key] is string text && text.IndexOf("playnite", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        found.Add($"{key}: {text}");
                    }
                }

                foreach (var child in dictionary.MergedDictionaries)
                {
                    Walk(child);
                }
            }

            Walk(Application.Current.Resources);
            foreach (var block in FindAll<System.Windows.Controls.TextBlock>(model.Window.Window))
            {
                if (block.Text?.IndexOf("playnite", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found.Add("ventana: " + block.Text);
                }
            }

            Log("textos con el nombre: " + found.Count);
            found.Take(40).ForEach(a => Log("  " + a.Substring(0, Math.Min(150, a.Length)).Replace(Environment.NewLine, " ")));
        }

        /// <summary>How long the interface stays stuck: what the user feels as the app freezing.</summary>
        private static async Task WatchUiThread(int milliseconds)
        {
            var total = System.Diagnostics.Stopwatch.StartNew();
            var worst = 0L;
            var stuck = 0L;
            var samples = 0;
            while (total.ElapsedMilliseconds < milliseconds)
            {
                var one = System.Diagnostics.Stopwatch.StartNew();
                await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Input);
                var wait = one.ElapsedMilliseconds;
                worst = Math.Max(worst, wait);
                stuck += wait > 100 ? wait : 0;
                samples++;
                await Task.Delay(25);
            }

            Log($"sonda: {samples} muestras, peor bloqueo {worst} ms, bloqueado {stuck} ms de {total.ElapsedMilliseconds} ms");
        }

        /// <summary>Saves invented achievements for a game, to see the pages that show them without any account.</summary>
        private static void SeedAchievements(DesktopAppViewModel model, string gameName)
        {
            var plugin = model.Extensions.Plugins[AtrioInfo.AchievementsPluginId].Plugin;
            var assembly = plugin.GetType().Assembly;
            var game = model.Database.Games.First(a => a.Name == gameName);
            var icon = model.Database.GetFullFilePath(game.CoverImage);
            void Set(object target, string name, object value) => target.GetType().GetProperty(name).SetValue(target, value);

            var dataType = assembly.GetType("PlayniteAchievements.Models.Achievements.GameAchievementData");
            var detailType = assembly.GetType("PlayniteAchievements.Models.Achievements.AchievementDetail");
            var data = Activator.CreateInstance(dataType);
            Set(data, "ProviderKey", "Steam");
            Set(data, "GameName", game.Name);
            Set(data, "PlayniteGameId", (Guid?)game.Id);
            Set(data, "AppId", 1);
            Set(data, "LastUpdatedUtc", DateTime.UtcNow);
            Set(data, "HasAchievements", true);

            var list = (IList)dataType.GetProperty("Achievements").GetValue(data);
            var names = new[] { "Primer paso", "Explorador", "Cazador de secretos", "Sin un rasguño", "Coleccionista", "Maestro de la tormenta" };
            for (var i = 0; i < names.Length; i++)
            {
                var detail = Activator.CreateInstance(detailType);
                Set(detail, "ApiName", "demo" + i);
                Set(detail, "DisplayName", names[i]);
                Set(detail, "Description", "Logro inventado para la prueba.");
                Set(detail, "UnlockedIconPath", icon);
                Set(detail, "LockedIconPath", icon);
                Set(detail, "GlobalPercentUnlocked", (double?)(70 - i * 12));
                if (i < 4)
                {
                    Set(detail, "UnlockTimeUtc", (DateTime?)DateTime.UtcNow.AddDays(-i * 3 - 1));
                }

                list.Add(detail);
            }

            var cache = plugin.GetType().GetProperty("CacheManager").GetValue(plugin);
            var result = cache.GetType().GetMethod("SaveGameData").Invoke(cache, new object[] { game.Id.ToString(), data });
            cache.GetType().GetMethod("NotifyCacheInvalidated", Type.EmptyTypes).Invoke(cache, null);
            Log($"logros de ejemplo: {list.Count} en {game.Name}: {result}");
        }

        /// <summary>What the download button of the Stores page does, without its dialogs.</summary>
        private static void DownloadAddon(DesktopAppViewModel model, string addonId)
        {
            var addon = model.App.ServicesClient.GetAddon(addonId);
            var package = addon.InstallerManifest.GetLatestCompatiblePackage();
            Log($"paquete {package?.Version} {package?.PackageUrl}");
            var path = addon.GetTargetDownloadPath();
            Playnite.Common.FileSystem.PrepareSaveFile(path);
            Playnite.Common.Web.HttpDownloader.DownloadFile(package.PackageUrl, path);
            Playnite.Plugins.ExtensionInstaller.QueuePackageInstall(path);
            Log("en cola: " + Path.GetFileName(path));
        }

        private class DemoGame
        {
            public string Name { get; set; }
            public string GameId { get; set; }
            public string Store { get; set; }
            public string Cover { get; set; }
            public string Background { get; set; }
            public string Developer { get; set; }
            public string Genre { get; set; }
            public string Description { get; set; }
            public bool Installed { get; set; }
            public ulong Playtime { get; set; }
            public int Year { get; set; }
            public int DaysAgo { get; set; }
        }

        /// <summary>Fills an empty library with the invented games of atrio/demo, used for screenshots.</summary>
        private static void LoadDemo(DesktopAppViewModel model, string folder)
        {
            var database = model.Database;
            var items = Playnite.SDK.Data.Serialization.FromJsonFile<List<DemoGame>>(Path.Combine(folder, "demo.json"));
            var platform = database.Platforms.FirstOrDefault(a => a.SpecificationId == "pc_windows") ?? database.Platforms.Add("PC (Windows)");
            var token = new System.Threading.CancellationToken();
            using (database.BufferedUpdate())
            {
                foreach (var item in items)
                {
                    var game = new Playnite.SDK.Models.Game(item.Name)
                    {
                        GameId = item.GameId,
                        PluginId = Guid.Parse(item.Store),
                        IsInstalled = item.Installed,
                        Playtime = item.Playtime,
                        Description = item.Description,
                        ReleaseDate = new Playnite.SDK.Models.ReleaseDate(item.Year),
                        PlatformIds = new List<Guid> { platform.Id },
                        DeveloperIds = new List<Guid> { database.Companies.Add(item.Developer).Id },
                        GenreIds = new List<Guid> { database.Genres.Add(item.Genre).Id },
                        Added = DateTime.Now.AddDays(-item.DaysAgo - 30)
                    };

                    if (item.DaysAgo > 0)
                    {
                        game.LastActivity = DateTime.Now.AddDays(-item.DaysAgo);
                    }

                    database.Games.Add(game);
                    game.CoverImage = database.AddFile(Path.Combine(folder, item.Cover), game.Id, true, token);
                    game.BackgroundImage = database.AddFile(Path.Combine(folder, item.Background), game.Id, true, token);
                    database.Games.Update(game);
                }
            }

            GameUnifier.Current.RebuildAndNotify();
            Log($"demo: {items.Count} entradas");
        }

        /// <summary>A click where the user would make it: inside the list on the left, on the first element of that type.</summary>
        private static void ClickInList<T>(DesktopAppViewModel model) where T : FrameworkElement
        {
            var list = FindAll<Playnite.Controls.ExtendedListBox>(model.Window.Window).First(a => a.IsVisible);
            var target = FindAll<T>(list).First(a => a.IsVisible);
            var inner = FindAll<System.Windows.Controls.TextBlock>(target).FirstOrDefault(a => a.IsVisible) as FrameworkElement ?? target;
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseUpEvent
            };
            inner.RaiseEvent(args);
            Log($"clic en {typeof(T).Name}: {model.Pane}");
        }

        private static IEnumerable<T> FindAll<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                {
                    yield return match;
                }

                foreach (var nested in FindAll<T>(child))
                {
                    yield return nested;
                }
            }
        }

        private static void ClickFirstCover(DesktopAppViewModel model)
        {
            var item = FindAll<System.Windows.Controls.ListBoxItem>(model.Window.Window).FirstOrDefault(a => a.IsVisible);
            var cover = item == null ? null : FindAll<System.Windows.Controls.Image>(item).FirstOrDefault();
            if (cover == null)
            {
                Log("sin portada");
                return;
            }

            cover.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseUpEvent });
            Log("pagina abierta: " + model.AppSettings.GridViewSideBarVisible);
        }

        private static void PressButton(DesktopAppViewModel model, string argument)
        {
            var parts = argument.Split('|');
            var index = parts.Length > 1 ? int.Parse(parts[1]) : 0;
            var buttons = FindAll<System.Windows.Controls.Button>(model.Window.Window).
                Where(a => a.IsVisible && a.Content?.ToString() == parts[0]).ToList();
            Log($"botones '{parts[0]}': {buttons.Count}");
            if (buttons.Count > index)
            {
                var command = buttons[index].Command;
                Log("orden enlazada: " + (command != null));
                command?.Execute(buttons[index].CommandParameter);
            }
        }

        private static void LogGameMenu(DesktopAppViewModel model)
        {
            var menu = new Controls.GameMenu(model) { DataContext = model.SelectedGame, ShowStartSection = true };
            menu.InitializeItems();
            var lines = menu.Items.OfType<System.Windows.Controls.MenuItem>().
                Select(a => a.Header + (a.Items.Count > 0 ? " > " + string.Join(", ", a.Items.OfType<System.Windows.Controls.MenuItem>().Select(b => b.Header)) : string.Empty));
            Log("menu: " + string.Join(" | ", lines));
        }

        private static void SetProperty(object target, string argument)
        {
            var parts = argument.Split(new char[] { ' ' }, 2);
            var property = target.GetType().GetProperty(parts[0]);
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var value = type.IsEnum ? Enum.Parse(type, parts[1]) : Convert.ChangeType(parts[1], type);
            property.SetValue(target, value);
        }

        private static void Invoke(object target, string argument)
        {
            var property = target.GetType().GetProperty(argument);
            if (property.GetValue(target) is ICommand command)
            {
                command.Execute(null);
            }
        }

        private static void Render(Visual visual, string name)
        {
            var element = visual as FrameworkElement;
            var width = (int)Math.Ceiling(element.ActualWidth);
            var height = (int)Math.Ceiling(element.ActualHeight);
            if (width == 0 || height == 0)
            {
                Log($"sin tamano {name}");
                return;
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(Folder, name + ".png")))
            {
                encoder.Save(stream);
            }
        }

        private static void RenderPopups(string name)
        {
            var index = 0;
            foreach (var source in PresentationSource.CurrentSources.OfType<HwndSource>())
            {
                if (source.RootVisual != null && source.RootVisual.GetType().Name == "PopupRoot")
                {
                    Render(source.RootVisual, $"{name}-{index++}");
                }
            }
        }

        private static void WriteReport(DesktopAppViewModel model, string name)
        {
            var unifier = GameUnifier.Current;
            var report = new StringBuilder();
            var groups = unifier.GetAllGroups().OrderBy(a => a.Primary.Name).ToList();
            report.AppendLine($"Entradas en la base: {model.GamesView.Items.Count}");
            report.AppendLine($"Visibles: {model.GamesView.CollectionView.Count}");
            report.AppendLine($"Grupos: {groups.Count}, copias plegadas: {unifier.CollapsedCopiesCount}");
            foreach (var group in groups)
            {
                var years = group.Copies.Where(a => a.ReleaseYear != null).Select(a => a.ReleaseYear.Value).ToList();
                var spread = years.Count > 1 ? years.Max() - years.Min() : 0;
                report.AppendLine($"[{spread}] " + string.Join("  |  ", group.Copies.Select(a =>
                    $"{a.Name} ({model.GamesView.GetLibraryPlugin(a)?.Name ?? "-"}, {a.ReleaseYear?.ToString() ?? "?"}{(a.IsInstalled ? ", inst" : "")})")));
            }

            File.WriteAllText(Path.Combine(Folder, name + ".txt"), report.ToString(), Encoding.UTF8);
        }
    }
}
#else
using Playnite.DesktopApp.ViewModels;
using System.Windows;

namespace Playnite.DesktopApp.Atrio
{
    /// <summary>The off-screen test harness only exists in test builds (AtrioBanco=true).</summary>
    public static class AtrioCaptura
    {
        public static bool IsActive => false;

        public static void PrepareWindow(Window window)
        {
        }

        public static void Run(DesktopAppViewModel model)
        {
        }
    }
}
#endif
