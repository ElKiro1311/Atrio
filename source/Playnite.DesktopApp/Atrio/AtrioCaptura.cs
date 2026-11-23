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

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr handle, uint message, IntPtr first, IntPtr second);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int size);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(int access, bool inherit, int id);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        // A 32-bit program can't look at the modules of a 64-bit one, but it can ask for its path.
        private static string GetProcessPath(int id)
        {
            var process = OpenProcess(0x1000, false, id);
            if (process == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var size = 1024;
                var path = new StringBuilder(size);
                return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr handle, StringBuilder text, int length);

        public static void PrepareWindow(Window window)
        {
            StartWatchdog();
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            // On a desktop of its own nobody sees the window, and on a screen it's really drawn: that's
            // what "fluidez" needs, because outside of every screen the frames cost nothing.
            var onScreen = Environment.GetEnvironmentVariable("ATRIO_CAPTURA_EN_PANTALLA") == "1";
            window.Left = onScreen ? 0 : -32000;
            window.Top = onScreen ? 0 : -32000;
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
                case "fluidez":
                    await MeasureSwitching(model, argument.Split('|'));
                    break;
                case "tictac":
                    // Outside of every screen nothing asks for frames, and the animations stay where they are.
                    CompositionTarget.Rendering += (_, __) => { };
                    break;
                case "cruce":
                    // A capture in the middle of a change of picture: cruce <milliseconds> <capture> <game>
                    var crossing = argument.Split(new[] { ' ' }, 3);
                    model.OpenGame(model.GamesView.Items.First(a => a.Name == crossing[2]).Id);
                    await Task.Delay(int.Parse(crossing[0]));
                    Render(model.Window.Window, crossing[1]);
                    break;
                case "rafaga":
                    var pause = int.Parse(argument.Split(' ')[0]);
                    foreach (var name in argument.Substring(argument.IndexOf(' ') + 1).Split('|'))
                    {
                        model.OpenGame(model.GamesView.Items.First(a => a.Name == name).Id);
                        await Task.Delay(pause);
                    }

                    break;
                case "recorrer":
                    var steps = argument.Split(' ');
                    foreach (var game in model.GamesView.Items.Take(int.Parse(steps[0])).ToList())
                    {
                        model.OpenGame(game.Id);
                        await Task.Delay(int.Parse(steps[1]));
                    }

                    Log($"memoria privada {System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / 1048576} MB");
                    break;
                case "imagenes":
                    foreach (var picture in FindAll<Playnite.Controls.FadeImage>(model.Window.Window))
                    {
                        Log($"  {GetPictureRole(picture)}: {GetPictureState(picture)}, fuente {picture.Source}");
                    }

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
                    model.Pane = argument == "juego" ? LibraryPane.Game : argument == "todos" ? LibraryPane.AllGames :
                        argument == "colecciones" ? LibraryPane.Collections : LibraryPane.Home;
                    break;
                case "coleccion":
                    await CollectionOrder(model, argument);
                    break;
                case "actualizacion":
                    UpdateOrder(model, argument);
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
                    Log($"panel: {model.Pane}, coleccion: {model.ActiveCollection?.Name}, vista: {model.AppSettings.ViewSettings.GamesViewType}, grupo: {model.AppSettings.ViewSettings.GroupingOrder}, " +
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
                    Log($"jugado: {model.SelectedGameDetails?.PlayedText}, actividad: {model.SelectedGameDetails?.Feed?.Entries.Count}");
                    break;
                case "desplaza":
                    // desplaza <pixels>: the biggest visible scroll of the window goes there.
                    var scrolls = FindAll<System.Windows.Controls.ScrollViewer>(model.Window.Window).Where(a => a.IsVisible && a.ScrollableHeight > 0).OrderByDescending(a => a.ActualWidth).ToList();
                    var scroll = scrolls.FirstOrDefault();
                    scroll?.ScrollToVerticalOffset(double.Parse(argument, System.Globalization.CultureInfo.InvariantCulture));
                    Log($"desplaza: {(scroll == null ? "sin scroll" : $"{scroll.VerticalOffset} de {scroll.ScrollableHeight}")}");
                    break;
                case "jugadahilo":
                    // jugadahilo: the game of the page as if it was played outside Atrio, which is seen in another thread.
                    var pageId = model.SelectedGameDetails.Game.Id;
                    await Task.Run(() =>
                    {
                        var elsewhere = model.Database.Games.Get(pageId);
                        elsewhere.Playtime += 600;
                        elsewhere.LastActivity = DateTime.Now;
                        elsewhere.IsInstalled = elsewhere.IsInstalled;
                        model.Database.Games.Update(elsewhere);
                    });
                    Log($"jugado en otro hilo: {model.SelectedGameDetails?.PlayedText}, actividad: {model.SelectedGameDetails?.Feed?.Entries.Count}");
                    break;
                case "actividad":
                    // actividad estado | todo | parches | mas | fuente <n> | abre <n>: what the store of the game publishes.
                    var feed = model.SelectedGameDetails?.Feed;
                    var feedParts = argument.Split(new[] { ' ' }, 2);
                    switch (feedParts[0])
                    {
                        case "todo":
                            feed.ShowsAll = true;
                            break;
                        case "parches":
                            feed.OnlyPatches = true;
                            break;
                        case "mas":
                            feed.MoreCommand.Execute(null);
                            break;
                        case "fuente":
                            feed.Sources[int.Parse(feedParts[1])].IsSelected = true;
                            break;
                        case "abre":
                            feed.OpenCommand.Execute(feed.Entries.Where(a => a.IsPost).ElementAt(int.Parse(feedParts[1])));
                            break;
                        case "espera":
                            for (var i = 0; i < 100 && (feed.IsLoading || i < 12); i++)
                            {
                                await Task.Delay(200);
                            }

                            break;
                        default:
                            Log($"actividad {model.SelectedGameDetails?.Game?.Name}: fuentes [{string.Join(", ", feed.Sources.Select(a => a.Name + (a.IsSelected ? "*" : "")))}] " +
                                $"lineas {feed.Entries.Count}, publicaciones {feed.Entries.Count(a => a.IsPost)}, parches {feed.Entries.Count(a => a.IsPatch)}, propias {feed.Entries.Count(a => !a.IsPost)}, " +
                                $"mas {feed.CanLoadMore}, nota '{feed.NoteText}', vacio '{feed.EmptyText}'");
                            foreach (var line in feed.Entries.Take(feedParts.Length > 1 ? int.Parse(feedParts[1]) : 8))
                            {
                                Log($"   {(line.HasDay ? line.DayText + " > " : "")}{(line.IsPost ? line.Label : "propia")}{(line.IsNew ? " NUEVO" : "")}: {line.Title} [{line.LikesText}/{line.CommentsText}] {(string.IsNullOrEmpty(line.ImageUrl) ? "sin imagen" : "con imagen")}");
                            }

                            break;
                    }

                    break;
                case "cromos":
                    // cromos estado | abre [n] | sobra <n> | privado: the trading cards and the badge of the game.
                    var cardPanel = model.SelectedGameDetails?.Cards;
                    var cardParts = argument.Split(' ');
                    switch (cardParts[0])
                    {
                        case "abre":
                            cardPanel.ShowAllCommand.Execute(cardParts.Length > 1 ? cardPanel.Tiles[int.Parse(cardParts[1])] : null);
                            break;
                        case "reflectantes":
                            var cardsWindow = Application.Current.Windows.OfType<Playnite.DesktopApp.Windows.SteamCardsWindow>().First();
                            ((Playnite.DesktopApp.Windows.SteamCardsModel)cardsWindow.DataContext).ShowsFoil = cardParts.Length == 1 || cardParts[1] == "si";
                            break;
                        case "privado":
                            cardPanel.Show(new Playnite.Atrio.SteamCardsResult { State = Playnite.Atrio.SteamCardsState.Private });
                            break;
                        case "sobra":
                            // The same cards with what only the account says: what is left to get and that the badge can be made.
                            var full = cardPanel.Result.Badge;
                            full.DropsLeft = int.Parse(cardParts[1]);
                            foreach (var card in full.Cards)
                            {
                                card.Owned = Math.Max(1, card.Owned);
                            }

                            cardPanel.Show(cardPanel.Result);
                            break;
                        default:
                            Log(cardPanel == null ? "cromos: sin panel" : $"cromos: visible {cardPanel.IsVisible}, privado {cardPanel.IsPrivate}, estado {cardPanel.Result.State}, '{cardPanel.BadgeTitle}' '{cardPanel.BadgeText}' '{cardPanel.NextText}' '{cardPanel.CardsText}' " +
                                $"'{cardPanel.DropsText}' fabricar {cardPanel.CanCraft}, icono {cardPanel.HasIcon}: " + string.Join(" | ", cardPanel.Tiles.Select(a => a.ToolTip)));
                            break;
                    }

                    break;
                case "opti":
                    // opti abre | cierra | estado | pon <option> <value> | instala | actualiza | quita | para | descarga | busca | espera <seconds>
                    var opti = model.SelectedGameDetails?.OptiScaler;
                    var optiParts = argument.Split(new[] { ' ' }, 3);
                    switch (optiParts[0])
                    {
                        case "abre":
                            opti.IsOpen = true;
                            break;
                        case "cierra":
                            opti.IsOpen = false;
                            break;
                        case "pon":
                            var option = opti.Options.First(a => a.Key == optiParts[1]);
                            option.Selected = option.Choices.First(a => a.Id == optiParts[2]);
                            break;
                        case "instala":
                            _ = Application.Current.Dispatcher.BeginInvoke(new Action(() => opti.InstallCommand.Execute(null)));
                            break;
                        case "actualiza":
                            _ = Application.Current.Dispatcher.BeginInvoke(new Action(() => opti.UpdateCommand.Execute(null)));
                            break;
                        case "quita":
                            _ = Application.Current.Dispatcher.BeginInvoke(new Action(() => opti.UninstallCommand.Execute(null)));
                            break;
                        case "para":
                            opti.StopCommand.Execute(null);
                            break;
                        case "descarga":
                            opti.DownloadClientCommand.Execute(null);
                            break;
                        case "busca":
                            opti.CheckUpdatesCommand.Execute(null);
                            break;
                        case "reintenta":
                            opti.RetryCommand.Execute(null);
                            break;
                        case "ventana":
                            opti.AllOptionsCommand.Execute(null);
                            break;
                        case "cliente":
                            opti.OpenClientCommand.Execute(null);
                            break;
                        case "ventanas":
                        case "cierraventana":
                            // Only the programs that this test started, by the folder of its data.
                            var clientFolder = Playnite.Atrio.OptiScalerClient.Root;
                            foreach (var clientProcess in System.Diagnostics.Process.GetProcessesByName("OptiscalerClient"))
                            {
                                var clientPath = GetProcessPath(clientProcess.Id);
                                if (clientPath == null || !clientPath.StartsWith(clientFolder, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                EnumWindows(
                                    (handle, _) =>
                                    {
                                        GetWindowThreadProcessId(handle, out var owner);
                                        if (owner == clientProcess.Id && IsWindowVisible(handle))
                                        {
                                            var caption = new StringBuilder(300);
                                            GetWindowText(handle, caption, 300);
                                            Log($"   ventana del cliente {owner}: '{caption}'");
                                            if (optiParts[0] == "cierraventana")
                                            {
                                                PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
                                            }
                                        }

                                        return true;
                                    },
                                    IntPtr.Zero);
                            }

                            break;
                        case "paquete":
                            Log("opti paquete: " + opti.PackageText);
                            opti.UsePackageCommand.Execute(null);
                            break;
                        case "espera":
                            // Until it's quiet: not starting, not downloading, not working.
                            var quiet = System.Diagnostics.Stopwatch.StartNew();
                            await Task.Delay(400);
                            while (opti.ShowsProgress && quiet.Elapsed.TotalSeconds < double.Parse(optiParts[1]))
                            {
                                await Task.Delay(100);
                            }

                            Log($"opti quieto tras {quiet.Elapsed.TotalSeconds:0.0} s: {opti.Stage}");
                            break;
                        default:
                            Log($"opti {opti?.Target?.Name}: disponible {opti?.IsAvailable}, etapa {opti?.Stage}, instalado '{opti?.InstalledVersion}', '{opti?.StatusText}' '{opti?.StatusDetail}', boton '{opti?.ButtonHint}', " +
                                $"chips [{string.Join(", ", opti?.DetectedChips ?? new List<string>())}], gpu '{opti?.GpuText}', instalar {opti?.ShowsInstall} '{opti?.InstallText}', actualizar '{opti?.UpdateText}', quitar {opti?.ShowsUninstall}, " +
                                $"cliente '{opti?.ClientText}' '{opti?.ClientUpdateText}' '{opti?.DownloadText}', mensaje {(opti?.MessageIsProblem == true ? "(problema) " : "")}'{opti?.Message}', progreso '{opti?.ProgressText}'");
                            foreach (var one in opti?.Options ?? new List<Playnite.DesktopApp.ViewModels.OptiScalerOption>())
                            {
                                Log($"   {one.Key} «{one.Label}»: {one.Selected?.Id} ({one.Selected?.Name}) inicial {one.Initial} cambiada {one.IsChanged} activa {one.IsEnabled}: {string.Join(" / ", one.Choices.Take(14).Select(a => a.Name))}");
                            }

                            if (opti?.HasCompatibility == true)
                            {
                                Log($"   lista: {opti.CompatibilityText} (bien {opti.CompatibilityIsGood}) {opti.CompatibilityNotes} {opti.GuideUrl}");
                            }

                            break;
                    }

                    break;
                case "juegofalso":
                    // juegofalso <name>|<folder>: an installed game of the test data whose folder holds a fake program.
                    var fakeParts = argument.Split('|');
                    foreach (var older in model.Database.Games.Where(a => a.Name == fakeParts[0]).ToList())
                    {
                        model.Database.Games.Remove(older);
                    }

                    model.Database.Games.Add(new Playnite.SDK.Models.Game(fakeParts[0]) { IsInstalled = true, InstallDirectory = fakeParts[1] });
                    Log($"juego falso '{fakeParts[0]}' en {fakeParts[1]}");
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
                        First(a => a.IsVisible && a.Command == (argument == "todos" ? model.ShowAllGamesCommand :
                            argument == "colecciones" ? model.ShowCollectionsCommand : model.ShowHomeCommand));
                    ((System.Windows.Automation.Provider.IToggleProvider)new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(paneButton)).Toggle();
                    paneButton.Command.Execute(null);
                    await Task.Delay(200);
                    Log($"boton marcado: {paneButton.IsChecked}, panel: {model.Pane}");
                    break;
                case "tecla":
                    var mainWindow = model.Window.Window;
                    mainWindow.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(mainWindow), 0, (Key)Enum.Parse(typeof(Key), argument))
                    {
                        RoutedEvent = Keyboard.KeyDownEvent
                    });
                    await Task.Delay(200);
                    Log($"tras la tecla: {model.Pane}, pestana: {model.SelectedGameDetails?.ShowAchievements}");
                    break;
                case "rueda":
                    var navigation = FindAll<System.Windows.Controls.ScrollViewer>(model.Window.Window).First(a => a.Name == "PART_ScrollSideBarItems");
                    var before = navigation.HorizontalOffset;
                    navigation.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -240) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
                    await Task.Delay(200);
                    Log($"rueda: de {before} a {navigation.HorizontalOffset}");
                    break;
                case "enlace":
                    Playnite.Commands.GlobalCommands.NavigateUrl(argument);
                    await Task.Delay(300);
                    Log($"navegador: {model.BrowserModel.Address}, en la fila: {model.SidebarItems.Any(a => a.Title == ResourceProvider.GetString("LOCAtrioBrowser") && a.Visible)}, activa: {model.ActiveView?.GetType().Name}");
                    break;
                case "textonavegador":
                    var pageText = await model.BrowserModel.RunScriptAsync("document.body ? document.body.innerText : ''") ?? string.Empty;
                    Log($"pagina {model.BrowserModel.Address} (edge {model.BrowserModel.UsesEdge}): {pageText.Substring(0, Math.Min(int.Parse(argument), pageText.Length))}");
                    break;
                case "jsnavegador":
                    Log("js: " + await model.BrowserModel.RunScriptAsync(argument));
                    break;
                case "esperanavegador":
                    // esperanavegador <segundos>: until the page is loaded.
                    await model.BrowserModel.WaitReadyAsync();
                    var loadWait = System.Diagnostics.Stopwatch.StartNew();
                    while (loadWait.Elapsed.TotalSeconds < int.Parse(argument) &&
                           await model.BrowserModel.RunScriptAsync("document.readyState") != "\"complete\"")
                    {
                        await Task.Delay(250);
                    }

                    Log($"navegador listo en {loadWait.Elapsed.TotalSeconds:0.0} s: {model.BrowserModel.Address} (edge {model.BrowserModel.UsesEdge})");
                    break;
                case "extensionesnavegador":
                    await model.BrowserModel.WaitReadyAsync();
                    var edgeCore = model.BrowserModel.EdgeCore;
                    if (edgeCore == null)
                    {
                        Log("extensiones: sin Edge");
                        break;
                    }

                    var loadedExtensions = await edgeCore.Profile.GetBrowserExtensionsAsync();
                    Log("extensiones: " + string.Join(", ", loadedExtensions.Select(a => $"{a.Name} {a.Id} {(a.IsEnabled ? "activa" : "apagada")}")));
                    break;
                case "cookiesnavegador":
                    // cookiesnavegador <dominio>: the cookies of a domain in both engines, without their values.
                    await model.BrowserModel.WaitReadyAsync();
                    var edgeCookies = model.BrowserModel.EdgeCore == null ? new List<string>() :
                        (await model.BrowserModel.EdgeCore.CookieManager.GetCookiesAsync(null)).Where(a => a.Domain.EndsWith(argument)).Select(a => a.Domain + a.Path + " " + a.Name).OrderBy(a => a).ToList();
                    var cefCookies = (await CookieBridge.ReadCefAsync() ?? new List<BridgeCookie>()).Where(a => a.Domain.EndsWith(argument)).Select(a => a.Domain + a.Path + " " + a.Name).OrderBy(a => a).ToList();
                    Log($"cookies edge ({edgeCookies.Count}): {string.Join(", ", edgeCookies)}");
                    Log($"cookies cef ({cefCookies.Count}): {string.Join(", ", cefCookies)}");
                    break;
                case "capturanavegador":
                    // capturanavegador <archivo.png>: the page of Edge, which a capture of the window doesn't see.
                    await model.BrowserModel.WaitReadyAsync();
                    if (model.BrowserModel.EdgeCore != null)
                    {
                        using (var pagePng = System.IO.File.Create(argument))
                        {
                            await model.BrowserModel.EdgeCore.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, pagePng);
                        }

                        Log("captura del navegador: " + argument);
                    }

                    break;
                case "cookiecef":
                    // cookiecef <dominio> <nombre> <valor>: a session of the other browser, as a store window leaves it.
                    var cefParts = argument.Split(' ');
                    var cefSet = CefSharp.Cef.GetGlobalCookieManager().SetCookie("https://" + cefParts[0].TrimStart('.') + "/",
                        new CefSharp.Cookie { Name = cefParts[1], Value = cefParts[2], Domain = cefParts[0].StartsWith(".") ? cefParts[0] : null, Path = "/", Secure = true, HttpOnly = true, Expires = DateTime.Now.AddDays(30) }, null);
                    await Task.Delay(300);
                    Log($"cookie cef {cefParts[1]}: {cefSet}");
                    break;
                case "cookieedge":
                    // cookieedge <dominio> <nombre> <valor>: a sign in done in the browser of Edge.
                    await model.BrowserModel.WaitReadyAsync();
                    var setParts = argument.Split(' ');
                    var edgeManager = model.BrowserModel.EdgeCore.CookieManager;
                    var newCookie = edgeManager.CreateCookie(setParts[1], setParts[2], setParts[0], "/");
                    newCookie.IsSecure = true;
                    newCookie.Expires = DateTime.Now.AddDays(30);
                    edgeManager.AddOrUpdateCookie(newCookie);
                    Log($"cookie edge {setParts[1]} = {setParts[2]}");
                    break;
                case "valorcookie":
                    // valorcookie <dominio> <nombre>: the value in both engines (only for the fake cookies of the bench).
                    await model.BrowserModel.WaitReadyAsync();
                    var valueParts = argument.Split(' ');
                    var inEdge = model.BrowserModel.EdgeCore == null ? new List<string>() :
                        (await model.BrowserModel.EdgeCore.CookieManager.GetCookiesAsync(null)).Where(a => a.Domain == valueParts[0] && a.Name == valueParts[1]).Select(a => a.Value).ToList();
                    var inCef = (await CookieBridge.ReadCefAsync() ?? new List<BridgeCookie>()).Where(a => a.Domain == valueParts[0] && a.Name == valueParts[1]).Select(a => a.Value).ToList();
                    Log($"valor {valueParts[1]}: edge [{string.Join(",", inEdge)}] cef [{string.Join(",", inCef)}]");
                    break;
                case "probarcookie":
                    // Which attributes the other browser refuses, one at a time.
                    var probe = CefSharp.Cef.GetGlobalCookieManager();
                    var edgeProbe = model.BrowserModel.EdgeCore == null ? null :
                        (await model.BrowserModel.EdgeCore.CookieManager.GetCookiesAsync(null)).FirstOrDefault(a => a.Name == "steamLoginSecure");
                    if (edgeProbe != null)
                    {
                        Log($"edge: domain={edgeProbe.Domain} path={edgeProbe.Path} expires={edgeProbe.Expires:o} kind={edgeProbe.Expires.Kind} session={edgeProbe.IsSession} secure={edgeProbe.IsSecure} httponly={edgeProbe.IsHttpOnly} samesite={edgeProbe.SameSite}");
                    }

                    var variants = new (string, CefSharp.Cookie)[]
                    {
                        ("base", new CefSharp.Cookie { Name = "p1", Value = "x", Domain = ".steampowered.com", Path = "/", Secure = true }),
                        ("expira local", new CefSharp.Cookie { Name = "p2", Value = "x", Domain = ".steampowered.com", Path = "/", Secure = true, Expires = DateTime.Now.AddDays(30) }),
                        ("expira utc", new CefSharp.Cookie { Name = "p3", Value = "x", Domain = ".steampowered.com", Path = "/", Secure = true, Expires = DateTime.UtcNow.AddDays(30) }),
                        ("expira edge", new CefSharp.Cookie { Name = "p4", Value = "x", Domain = ".steampowered.com", Path = "/", Secure = true, Expires = edgeProbe?.Expires }),
                        ("samesite none", new CefSharp.Cookie { Name = "p5", Value = "x", Domain = ".steampowered.com", Path = "/", Secure = true, SameSite = CefSharp.Enums.CookieSameSite.NoRestriction }),
                        ("samesite lax", new CefSharp.Cookie { Name = "p6", Value = "x", Domain = ".steampowered.com", Path = "/", Secure = true, SameSite = CefSharp.Enums.CookieSameSite.LaxMode }),
                        ("httponly", new CefSharp.Cookie { Name = "p7", Value = "x", Domain = ".steampowered.com", Path = "/", Secure = true, HttpOnly = true }),
                    };
                    foreach (var variant in variants)
                    {
                        var accepted = await CefSharp.AsyncExtensions.SetCookieAsync(probe, "https://steampowered.com/", variant.Item2);
                        Log($"cef {variant.Item1}: {accepted}");
                    }

                    break;
                case "borracookieedge":
                    // borracookieedge <dominio> <nombre>: signing out in the browser.
                    await model.BrowserModel.WaitReadyAsync();
                    var edgeParts = argument.Split(' ');
                    model.BrowserModel.EdgeCore.CookieManager.DeleteCookiesWithDomainAndPath(edgeParts[1], edgeParts[0], "/");
                    Log($"cookie edge {edgeParts[1]} borrada");
                    break;
                case "extensionatrio":
                    // extensionatrio <id> si|no: the switch of the page of the stores.
                    var switchParts = argument.Split(' ');
                    var extensionItem = new BrowserExtensionItem(model, Playnite.Atrio.BrowserExtensions.Find(switchParts[0]));
                    extensionItem.IsOn = switchParts[1] == "si";
                    await Task.Delay(4000);
                    Log($"interruptor {extensionItem.Name}: {extensionItem.IsOn}, estado: {extensionItem.State}");
                    break;
                case "mostrarnavegador":
                    model.BrowserModel.OnShown();
                    await Task.Delay(1500);
                    Log("navegador visible");
                    break;
                case "ocultanavegador":
                    model.BrowserModel.OnHidden();
                    await Task.Delay(1500);
                    Log("navegador oculto");
                    break;
                case "cerrarnavegador":
                    model.BrowserModel.CloseCommand.Execute(null);
                    await Task.Delay(300);
                    Log($"en la fila: {model.SidebarItems.Any(a => a.Title == ResourceProvider.GetString("LOCAtrioBrowser") && a.Visible)}, activa: {model.ActiveView?.GetType().Name}, panel: {model.Pane}");
                    break;
                case "navegadores":
                    Log("navegadores: " + string.Join(" | ", Playnite.Atrio.LinkBrowsers.GetOptions().Select(a => a.Name + " = " + a.Id)));
                    break;
                case "amigos":
                    await Task.Delay(int.Parse(argument));
                    Log($"amigos: tarjeta {model.SelectedGameDetails?.HasFriendsCard}, grupos {model.SelectedGameDetails?.FriendGroups?.Count}, aviso: {model.SelectedGameDetails?.FriendsMessage}");
                    break;
                case "amigosdemo":
                    string avatar = "https://avatars.fastly.steamstatic.com/fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb_full.jpg";
                    List<Playnite.Atrio.SteamFriend> demoFriends(int count, int recent, int total) => Enumerable.Range(1, count).
                        Select(a => new Playnite.Atrio.SteamFriend { SteamId = "7656119" + a, Name = "Amigo de ejemplo " + a, AvatarUrl = avatar, RecentMinutes = recent, TotalMinutes = total }).ToList();
                    model.SelectedGameDetails.ShowFriends(new Playnite.Atrio.SteamFriendsResult
                    {
                        State = Playnite.Atrio.SteamFriendsState.Ready,
                        InGame = demoFriends(1, 45, 900),
                        PlayedRecently = demoFriends(2, 320, 4000),
                        PlayedBefore = demoFriends(20, 0, 1200),
                        Owners = demoFriends(5, 0, 0)
                    });
                    break;
                case "amigosaviso":
                    model.SelectedGameDetails.ShowFriends(new Playnite.Atrio.SteamFriendsResult { State = (Playnite.Atrio.SteamFriendsState)Enum.Parse(typeof(Playnite.Atrio.SteamFriendsState), argument) });
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
                Select(a => a.Header + (a.Items.Count > 0 ? " > " + string.Join(", ", a.Items.OfType<System.Windows.Controls.MenuItem>().Select(b => (b.IsChecked ? "[x] " : string.Empty) + b.Header)) : string.Empty));
            Log("menu: " + string.Join(" | ", lines));
        }

        private static void UpdateOrder(DesktopAppViewModel model, string argument)
        {
            Playnite.ViewModels.UpdateViewModel window() =>
                Application.Current.Windows.Cast<Window>().Select(a => a.DataContext).OfType<Playnite.ViewModels.UpdateViewModel>().First();
            switch (argument)
            {
                case "buscar":
                    var releases = AtrioUpdates.GetReleases();
                    Log($"instalada: {AtrioInfo.CurrentVersion}, publicadas: {string.Join(", ", releases.Select(a => a.Version))}, " +
                        $"nueva: {AtrioUpdates.GetNewerRelease()?.Version}, paquete: {releases.FirstOrDefault()?.PackageSize}, huella: {releases.FirstOrDefault()?.PackageHash != null}");
                    break;
                case "aviso":
                    typeof(PlayniteApplication).GetMethod("CheckForUpdates", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(model.App, new object[] { true, false });
                    Log($"boton: {model.UpdatesAvailable}, avisos: {string.Join(" | ", model.App.Notifications.Messages.Select(a => a.Text))}");
                    break;
                case "ventana":
                    _ = Application.Current.Dispatcher.BeginInvoke(new Action(() => model.OpenUpdatesCommand.Execute(null)));
                    break;
                case "comprobar":
                    _ = Application.Current.Dispatcher.BeginInvoke(new Action(() => model.CheckForUpdateCommand.Execute(null)));
                    break;
                case "notas":
                    Log("notas: " + string.Join(" | ", window().ReleaseNotes.Select(a => $"{a.Version}: {a.Note}")));
                    break;
                case "instalar":
                    window().InstallUpdateCommand.Execute(null);
                    break;
                case "progreso":
                    Log($"descarga: {window().ShowProgress}, {window().UpdateProgress} %");
                    break;
                case "cerrar":
                    window().CloseCommand.Execute(null);
                    break;
                case "carpeta":
                    Log("carpeta de la actualizacion: " + Directory.Exists(Path.Combine(PlaynitePaths.ProgramPath, "update")));
                    break;
                default:
                    Log("orden de actualizacion desconocida: " + argument);
                    break;
            }
        }

        private static GameCollection FindCollection(DesktopAppViewModel model, string name)
        {
            return GameCollections.GetAll(model.Database, GameUnifier.Current.Settings).First(a => a.Name == name);
        }

        private static async Task CollectionOrder(DesktopAppViewModel model, string argument)
        {
            var parts = argument.Split(new char[] { ' ' }, 2);
            var data = parts.Length > 1 ? parts[1].Split('|') : new string[] { string.Empty };
            var collections = model.CollectionsModel;
            var unifier = GameUnifier.Current;
            Game game() => model.Database.Games.First(a => a.Name == data[1]);
            switch (parts[0])
            {
                case "lista":
                    collections.Refresh();
                    Log("colecciones: " + string.Join(" | ", collections.Tiles.Select(a => $"{a.Collection.Name} ({a.CountText})")));
                    break;
                case "nueva":
                    model.Database.Categories.Add(data[0]);
                    break;
                case "abrir":
                    collections.Open(FindCollection(model, data[0]));
                    break;
                case "poner":
                    GameCollections.AddGames(model.Database, FindCollection(model, data[0]).Id, new[] { game() }, unifier);
                    break;
                case "sacar":
                    GameCollections.RemoveGames(model.Database, FindCollection(model, data[0]).Id, new[] { game() }, unifier);
                    break;
                case "soltar":
                    collections.Refresh();
                    await Task.Delay(300);
                    var tile = FindAll<System.Windows.Controls.Button>(model.Window.Window).
                        First(a => a.IsVisible && a.DataContext is CollectionTile b && b.Collection.Name == data[0]);
                    var dropped = new DataObject(Controls.Views.CollectionsView.DragFormat, game().Id.ToString());
                    var drop = (DragEventArgs)typeof(DragEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).First().
                        Invoke(new object[] { dropped, DragDropKeyStates.None, DragDropEffects.Copy, tile, new Point(10, 10) });
                    drop.RoutedEvent = DragDrop.DropEvent;
                    tile.RaiseEvent(drop);
                    break;
                case "encima":
                    var over = FindAll<System.Windows.Controls.Button>(model.Window.Window).
                        First(a => a.IsVisible && a.DataContext is CollectionTile b && b.Collection.Name == data[0]);
                    var dragged = new DataObject(Controls.Views.CollectionsView.DragFormat, game().Id.ToString());
                    var hover = (DragEventArgs)typeof(DragEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).First().
                        Invoke(new object[] { dragged, DragDropKeyStates.None, DragDropEffects.Copy, over, new Point(10, 10) });
                    hover.RoutedEvent = DragDrop.DragOverEvent;
                    over.RaiseEvent(hover);
                    Log($"encima: {hover.Effects}, resaltado: {string.Join(", ", collections.Tiles.Where(a => a.IsDropTarget).Select(a => a.Collection.Name))}");
                    break;
                case "renombrar":
                    collections.Rename(FindCollection(model, data[0]), data[1]);
                    break;
                case "borrar":
                    collections.Delete(FindCollection(model, data[0]));
                    break;
                case "inicio":
                    collections.ToggleHomeShelf(FindCollection(model, data[0]));
                    break;
                case "dinamica":
                    collections.CreateDynamic(data[0], new FilterPresetSettings { IsInstalled = true });
                    break;
                case "filtros":
                    collections.BeginDraft(data[0].Length == 0 ? null : FindCollection(model, data[0]));
                    break;
                case "cancelar":
                    collections.CancelDraftCommand.Execute(null);
                    break;
                case "guardar":
                    collections.SaveFiltersCommand.Execute(null);
                    break;
                case "elegir":
                    _ = Application.Current.Dispatcher.BeginInvoke(new Action(() => collections.ChooseGames(FindCollection(model, data[0]))));
                    break;
                case "menu":
                    collections.ShowMenu(FindCollection(model, data[0]), model.Window.Window);
                    break;
                case "clic":
                    var tileButton = FindAll<System.Windows.Controls.Button>(model.Window.Window).
                        First(a => a.IsVisible && a.DataContext is CollectionTile b && b.Collection.Name == data[0]);
                    Log("orden del recuadro: " + (tileButton.Command != null));
                    tileButton.Command?.Execute(tileButton.CommandParameter);
                    break;
                case "clicderecho":
                    var rightTile = FindAll<System.Windows.Controls.Button>(model.Window.Window).
                        First(a => a.IsVisible && a.DataContext is CollectionTile b && b.Collection.Name == data[0]);
                    FindAll<System.Windows.Controls.TextBlock>(rightTile).First(a => a.IsVisible).
                        RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = Mouse.MouseUpEvent });
                    break;
                case "boton":
                    var command = data[0] == "cerrar" ? collections.CloseCommand : data[0] == "volver" ? collections.ReturnCommand : (ICommand)collections.BackCommand;
                    var bound = FindAll<System.Windows.Controls.Button>(model.Window.Window).FirstOrDefault(a => a.IsVisible && a.Command == command);
                    Log("boton encontrado: " + (bound != null));
                    bound?.Command.Execute(bound.CommandParameter);
                    break;
                case "mas":
                    var more = FindAll<System.Windows.Controls.Button>(model.Window.Window).FirstOrDefault(a => a.IsVisible && a.Command == collections.MoreCommand);
                    Log("boton mas: " + (more != null) + ", parametro: " + more?.CommandParameter?.GetType().Name);
                    more?.Command.Execute(more.CommandParameter);
                    break;
                case "marcar":
                    var chooser = Application.Current.Windows.Cast<Window>().Last(a => a != model.Window.Window && a.IsVisible).DataContext as CollectionGamesViewModel;
                    chooser.SearchText = data[0];
                    await Task.Delay(300);
                    var shown = chooser.Games.Cast<CollectionGameOption>().ToList();
                    Log($"coinciden: {shown.Count}, marcados: {chooser.SelectionText}");
                    shown.First().IsSelected = !shown.First().IsSelected;
                    Log("tras marcar: " + chooser.SelectionText);
                    break;
                case "confirmar":
                    var confirmed = Application.Current.Windows.Cast<Window>().Last(a => a != model.Window.Window && a.IsVisible).DataContext as CollectionGamesViewModel;
                    confirmed.ConfirmCommand.Execute(null);
                    break;
                case "copias":
                    var copies = GameCollections.GetCopies(game(), unifier);
                    var id = FindCollection(model, data[0]).Id;
                    Log($"copias: {copies.Count}, en la coleccion: {copies.Count(a => a.CategoryIds?.Contains(id) == true)}");
                    break;
                case "ocultar":
                    var hiddenGame = model.Database.Games.First(a => a.Name == data[0]);
                    hiddenGame.Hidden = !hiddenGame.Hidden;
                    model.Database.Games.Update(hiddenGame);
                    break;
                case "seleccionar":
                    // Two games selected and a press on the second one, which must not lose the selection by itself.
                    var list = FindAll<Playnite.Controls.ExtendedListBox>(model.Window.Window).First(a => a.IsVisible && a.ItemContainerStyle != null);
                    var items = FindAll<System.Windows.Controls.ListBoxItem>(list).Where(a => a.IsVisible).Take(2).ToList();
                    items.ForEach(a => a.IsSelected = true);
                    await Task.Delay(200);
                    var pressed = FindAll<System.Windows.Controls.TextBlock>(items[1]).First(a => a.IsVisible);
                    pressed.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
                    await Task.Delay(100);
                    Log($"al pulsar: {list.SelectedItems.Count} seleccionados");
                    pressed.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseUpEvent });
                    await Task.Delay(300);
                    Log($"al soltar: {list.SelectedItems.Count} seleccionados, panel: {model.Pane}");
                    break;
                case "barra":
                    Log($"abierta: {collections.ActiveName}, {collections.ActiveDetails}, elegir: {collections.CanChooseGames}, " +
                        $"borrador: {collections.IsDrafting}, guardar: {collections.CanSaveFilters}, oferta: {collections.OffersSavingFilters}, " +
                        $"todos: {model.IsAllGamesPane}, seccion: {model.IsCollectionSection}, filtros: {model.AppSettings.FilterSettings.IsActive}, panel filtros: {model.AppSettings.FilterPanelVisible}");
                    break;
                default:
                    Log("orden de coleccion desconocida: " + parts[0]);
                    break;
            }
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

        /// <summary>
        /// Opens the games one after another and tells how the frames went. WPF only starts a frame when the
        /// last one is on its way, so a gap means that the interface or the drawing fell behind.
        /// </summary>
        private static async Task MeasureSwitching(DesktopAppViewModel model, string[] names)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var ticks = new List<double>();
            EventHandler onRendering = (_, __) => ticks.Add(clock.Elapsed.TotalMilliseconds);
            var tier = System.Windows.Media.RenderCapability.Tier >> 16;
            Log($"dibujo: nivel {tier}, modo {System.Windows.Media.RenderOptions.ProcessRenderMode}");
            System.Windows.Media.CompositionTarget.Rendering += onRendering;

            // What keeps the interface thread busy, by operation.
            var nameOf = typeof(System.Windows.Threading.DispatcherOperation).GetProperty("Name",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var started = new Dictionary<System.Windows.Threading.DispatcherOperation, double>();
            var busy = new Dictionary<string, double>();
            var hooks = model.Window.Window.Dispatcher.Hooks;
            System.Windows.Threading.DispatcherHookEventHandler onStarted = (_, e) => started[e.Operation] = clock.Elapsed.TotalMilliseconds;
            System.Windows.Threading.DispatcherHookEventHandler onCompleted = (_, e) =>
            {
                if (started.TryGetValue(e.Operation, out var begin))
                {
                    started.Remove(e.Operation);
                    var key = $"{nameOf.GetValue(e.Operation, null)} ({e.Operation.Priority})";
                    busy.TryGetValue(key, out var total);
                    busy[key] = total + clock.Elapsed.TotalMilliseconds - begin;
                }
            };
            hooks.OperationStarted += onStarted;
            hooks.OperationCompleted += onCompleted;

            await Task.Delay(1500);
            Log($"en reposo: {ticks.Count / 1.5:0} fotogramas por segundo");
            foreach (var name in names)
            {
                var entry = model.GamesView.Items.First(a => a.Name == name);
                ticks.Clear();
                busy.Clear();
                var threadsBefore = GetThreadTimes();
                var collections = GC.CollectionCount(2);
                var start = clock.Elapsed.TotalMilliseconds;
                var pictures = FindAll<Playnite.Controls.FadeImage>(model.Window.Window).ToList();
                var states = pictures.ToDictionary(a => a, GetPictureState);
                var moves = new Dictionary<Playnite.Controls.FadeImage, Tuple<double, double>>();
                var arrivals = new Dictionary<Playnite.Controls.FadeImage, double>();
                var knownSources = pictures.SelectMany(GetPictureSources).ToList();
                model.OpenGame(entry.Id);
                var opened = clock.Elapsed.TotalMilliseconds - start;
                while (clock.Elapsed.TotalMilliseconds - start < 1400)
                {
                    await Task.Delay(5);
                    foreach (var picture in pictures)
                    {
                        var state = GetPictureState(picture);
                        if (state != states[picture])
                        {
                            states[picture] = state;
                            var now = clock.Elapsed.TotalMilliseconds - start;
                            moves[picture] = Tuple.Create(moves.TryGetValue(picture, out var move) ? move.Item1 : now, now);
                            if (!arrivals.ContainsKey(picture) && GetPictureSources(picture).Except(knownSources).Any())
                            {
                                arrivals[picture] = now;
                            }
                        }
                    }
                }

                Log("  imagenes: " + string.Join(", ", moves.Select(a =>
                    $"{GetPictureRole(a.Key)} se mueve a los {a.Value.Item1:0}, llega la nueva a los {(arrivals.TryGetValue(a.Key, out var arrival) ? arrival.ToString("0") : "-")}, acaba a los {a.Value.Item2:0}")));
                var times = new[] { start }.Concat(ticks).ToList();
                var gaps = times.Skip(1).Zip(times, (a, b) => a - b).ToList();
                var firstSecond = times.Count(a => a < start + 1000) - 1;
                Log($"{name}: abrir {opened:0} ms, mayor hueco {gaps.Max():0} ms, " +
                    $"huecos de mas de 34 ms {gaps.Count(a => a > 34)}, de mas de 100 ms {gaps.Count(a => a > 100)}, " +
                    $"fotogramas en el primer segundo {firstSecond}, recolecciones {GC.CollectionCount(2) - collections}");
                var threadsAfter = GetThreadTimes();
#pragma warning disable CS0618 // It is the id that the process lists, which is what is needed here.
                var interfaceThread = AppDomain.GetCurrentThreadId();
#pragma warning restore CS0618
                Log("  hilos: " + string.Join(", ", threadsAfter.
                    Select(a => new { a.Key, Time = a.Value - (threadsBefore.TryGetValue(a.Key, out var b) ? b : 0) }).
                    OrderByDescending(a => a.Time).Take(5).
                    Select(a => $"{(a.Key == interfaceThread ? "interfaz" : a.Key.ToString())} {a.Time:0} ms")));
                Log("  ocupado: " + string.Join(", ", busy.OrderByDescending(a => a.Value).Take(6).Select(a => $"{a.Key} {a.Value:0} ms")));
            }

            var collection = System.Diagnostics.Stopwatch.StartNew();
            GC.Collect();
            Log($"  una recoleccion completa: {collection.ElapsedMilliseconds} ms, memoria privada {System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / 1048576} MB");
            hooks.OperationStarted -= onStarted;
            hooks.OperationCompleted -= onCompleted;
            System.Windows.Media.CompositionTarget.Rendering -= onRendering;
        }

        private static string GetPictureState(Playnite.Controls.FadeImage picture)
        {
            var images = ((System.Windows.Controls.Panel)picture.Content).Children.OfType<System.Windows.Controls.Image>().ToList();
            return string.Join(" ", images.Select(a => $"{a.Opacity:0.00}/{a.Source?.GetHashCode()}"));
        }

        private static IEnumerable<ImageSource> GetPictureSources(Playnite.Controls.FadeImage picture)
        {
            return ((System.Windows.Controls.Panel)picture.Content).Children.OfType<System.Windows.Controls.Image>().
                Select(a => a.Source).Where(a => a != null);
        }

        private static string GetPictureRole(Playnite.Controls.FadeImage picture)
        {
            return picture.Stretch == Stretch.Fill ? "lados" : picture.IsBlurEnabled ? "ventana" : "cabecera";
        }

        private static Dictionary<int, double> GetThreadTimes()
        {
            var times = new Dictionary<int, double>();
            foreach (System.Diagnostics.ProcessThread thread in System.Diagnostics.Process.GetCurrentProcess().Threads)
            {
                try
                {
                    times[thread.Id] = thread.TotalProcessorTime.TotalMilliseconds;
                }
                catch (InvalidOperationException)
                {
                }
            }

            return times;
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
