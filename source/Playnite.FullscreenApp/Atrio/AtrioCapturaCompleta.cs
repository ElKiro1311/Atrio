#if ATRIO_BANCO
using Playnite.Atrio;
using Playnite.FullscreenApp.ViewModels;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Playnite.FullscreenApp.Atrio
{
    /// <summary>
    /// Development harness of this mode. With the ATRIO_CAPTURA variable set the app follows a script
    /// (guion.txt in that folder) and renders its own windows to PNG files, nothing else is captured.
    /// </summary>
    public static class AtrioCapturaCompleta
    {
        private static string Folder => AtrioInfo.GetTestSetting("ATRIO_CAPTURA");

        public static bool HasScript => File.Exists(Path.Combine(Folder, "guion.txt"));

        private static void Log(string message)
        {
            File.AppendAllText(Path.Combine(Folder, "guion.log"), $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        }

        public static async Task Run(FullscreenAppViewModel model)
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
                if (parts[0] == "salir")
                {
                    break;
                }

                try
                {
                    await Execute(model, parts[0], argument);
                }
                catch (Exception e)
                {
                    Log("ERROR " + e);
                }

                await Task.Delay(120);
            }

            Log("fin");
        }

        private static async Task Execute(FullscreenAppViewModel model, string order, string argument)
        {
            switch (order)
            {
                case "espera":
                    await Task.Delay(int.Parse(argument));
                    break;
                case "captura":
                    Capture(model, argument);
                    break;
                case "tecla":
                    foreach (var key in argument.Split(' '))
                    {
                        SendKey((Key)Enum.Parse(typeof(Key), key));
                        await Task.Delay(260);
                    }

                    Log("foco: " + DescribeFocus());
                    break;
                case "juego":
                    var game = model.GamesView.Items.FirstOrDefault(a => a.Name.StartsWith(argument, StringComparison.OrdinalIgnoreCase));
                    Log(game == null ? "no encontrado" : "juego: " + game.Name);
                    if (game != null)
                    {
                        model.SelectedGame = game;
                    }

                    break;
                case "preset":
                    var preset = model.Database.FilterPresets.FirstOrDefault(a => a.Name.Equals(argument, StringComparison.OrdinalIgnoreCase));
                    Log(preset == null ? "filtro no encontrado" : "filtro: " + preset.Name);
                    model.ActiveFilterPreset = preset;
                    break;
                case "ficha":
                    model.ToggleGameDetailsCommand.Execute(null);
                    break;
                case "filtros":
                    model.ToggleFiltersCommand.Execute(null);
                    break;
                case "menu":
                    Later(() => model.OpenMainMenuCommand.Execute(null));
                    break;
                case "opciones":
                    Later(() => model.OpenGameMenuCommand.Execute());
                    break;
                case "notificaciones":
                    Later(() => model.OpenNotificationsMenuCommand.Execute(null));
                    break;
                case "buscar":
                    Later(() => model.OpenSearchCommand.Execute(null));
                    break;
                case "aviso":
                    Later(() => model.Dialogs.ShowMessage(argument, "Atrio", MessageBoxButton.YesNo));
                    break;
                case "error":
                    Later(() => model.Dialogs.ShowErrorMessage(argument, "Atrio"));
                    break;
                case "progreso":
                    Later(() => model.Dialogs.ActivateGlobalProgress(a =>
                    {
                        a.Text = argument;
                        a.IsIndeterminate = false;
                        a.ProgressMaxValue = 100;
                        a.CurrentProgressValue = 40;
                        System.Threading.Thread.Sleep(4000);
                    }, new GlobalProgressOptions(argument, true) { IsIndeterminate = false }));
                    break;
                case "lanzando":
                    model.GameListFocused = false;
                    model.GameStatusView = new GameStatusViewModel(model.SelectedGame) { GameStatusText = argument };
                    model.GameStatusVisible = true;
                    break;
                case "sinlanzar":
                    model.GameStatusVisible = false;
                    model.GameStatusView = null;
                    model.GameListFocused = true;
                    break;
                case "cerrar":
                    var child = Application.Current.Windows.OfType<Window>().Where(a => a != model.Window.Window && a.IsVisible).LastOrDefault();
                    Log("cierro: " + child);
                    child?.Close();
                    break;
                case "ventanas":
                    Log(string.Join(", ", Application.Current.Windows.OfType<Window>().Select(a => $"{a.GetType().Name} {a.IsVisible} {a.ActualWidth}x{a.ActualHeight}")));
                    break;
                case "estado":
                    Log($"juego: {model.SelectedGame?.Name}, instalado: {model.SelectedGame?.IsInstalled}, instalando: {model.SelectedGame?.IsInstalling}, accion: {model.SelectedGameDetails?.ContextActionDescription}, carpeta: {model.SelectedGame?.Game.InstallDirectory}, filtro: {model.ActiveFilterPreset?.Name}, juegos: {model.GamesView.CollectionView.Count}, foco: {DescribeFocus()}");
                    break;
                case "fuentes":
                    Log(string.Join(" | ", Application.Current.Resources.MergedDictionaries.Select(a => a.Source?.OriginalString).Take(14)));
                    break;
                case "boton":
                    PressButton(argument);
                    break;
                case "ajuste":
                    var parts = argument.Split(' ');
                    var property = typeof(FullscreenSettings).GetProperty(parts[0]);
                    property.SetValue(model.AppSettings.Fullscreen, property.PropertyType.IsEnum ?
                        Enum.Parse(property.PropertyType, parts[1]) :
                        Convert.ChangeType(parts[1], property.PropertyType));
                    Log($"{parts[0]} = {property.GetValue(model.AppSettings.Fullscreen)}");
                    break;
                case "notificacion":
                    model.App.Notifications.Add(new NotificationMessage(Guid.NewGuid().ToString(), argument, NotificationType.Info));
                    break;
                case "seccion":
                    Later(() => PressButton(argument));
                    break;
                case "juegosteam":
                    var steamGame = model.GamesView.Items.FirstOrDefault(a => !a.IsInstalled && a.Game.PluginId == SteamContent.SteamPluginId && a.Copies.Count == 1);
                    Log("juego de Steam sin instalar: " + steamGame?.Name + " " + steamGame?.Game.GameId);
                    model.SelectedGame = steamGame;
                    break;
                case "instalar":
                    Later(() => model.InstallGame(model.SelectedGame.Game));
                    break;
                case "quitarinstalado":
                    var installed = model.Database.Games.Get(model.SelectedGame.Game.Id);
                    installed.IsInstalled = false;
                    installed.IsInstalling = false;
                    installed.InstallDirectory = null;
                    model.Database.Games.Update(installed);
                    break;
                case "busca":
                    model.AppSettings.Fullscreen.FilterSettings.Name = argument;
                    break;
                case "tiendas":
                    var group = model.GamesView.Unifier.GetGroup(model.SelectedGame.Game.Id);
                    Log("copias: " + group?.Copies.Count);
                    var choose = typeof(FullscreenAppViewModel).GetMethod("ChooseGameCopy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    Later(() => choose.Invoke(model, new object[] { group }));
                    break;
                case "actualizacion":
                    Later(() => model.OpenUpdatesCommand.Execute(null));
                    break;
                case "clientes":
                    Later(() => new MainMenuViewModel(new Windows.MainMenuWindowFactory(), model).OpenClientsCommand.Execute(null));
                    break;
                default:
                    Log("orden desconocida");
                    break;
            }
        }

        // The menus are modal: they need the script to go on while they stay open.
        private static void Later(Action action)
        {
            Application.Current.Dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }

        private static string DescribeFocus()
        {
            var focused = Keyboard.FocusedElement as FrameworkElement;
            var window = WindowOnTop();
            var logical = window == null ? null : FocusManager.GetFocusedElement(window) as FrameworkElement;
            return $"teclado={focused?.GetType().Name}:{focused?.Name}/{(focused?.DataContext as GamesCollectionViewEntry)?.Name} " +
                   $"logico={logical?.GetType().Name}:{logical?.Name}/{(logical?.DataContext as GamesCollectionViewEntry)?.Name}";
        }

        private static Window WindowOnTop()
        {
            return Application.Current.Windows.OfType<Window>().Where(a => a.IsVisible).LastOrDefault();
        }

        private static void SendKey(Key key)
        {
            var window = WindowOnTop();
            var target = (Keyboard.FocusedElement ?? FocusManager.GetFocusedElement(window) ?? window) as UIElement ?? window;
            var source = PresentationSource.FromVisual(window);
            var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            target.RaiseEvent(down);
            var pressed = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent };
            if (!down.Handled)
            {
                target.RaiseEvent(pressed);
            }

            // WPF moves the focus after going through the input manager, which this skips.
            if (!down.Handled && !pressed.Handled)
            {
                var direction = key == Key.Up ? FocusNavigationDirection.Up : key == Key.Down ? FocusNavigationDirection.Down :
                                key == Key.Left ? FocusNavigationDirection.Left : key == Key.Right ? FocusNavigationDirection.Right : (FocusNavigationDirection?)null;
                if (direction != null)
                {
                    target.MoveFocus(new TraversalRequest(direction.Value));
                }
            }

            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyUpEvent });
        }

        private static void PressButton(string name)
        {
            var window = WindowOnTop();
            foreach (var button in FindChildren<System.Windows.Controls.Primitives.ButtonBase>(window))
            {
                if (button.Name == name || (button.Content as string) == name)
                {
                    Log("pulso: " + button.Name);
                    button.Command?.Execute(button.CommandParameter);
                    return;
                }
            }

            Log("sin boton: " + name);
        }

        private static IEnumerable<T> FindChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is T match)
                {
                    yield return match;
                }

                foreach (var nested in FindChildren<T>(child))
                {
                    yield return nested;
                }
            }
        }

        /// <summary>All the windows of the app, one over the other, the way they are on the screen.</summary>
        private static void Capture(FullscreenAppViewModel model, string name)
        {
            var main = model.Window.Window;
            var width = (int)main.ActualWidth;
            var height = (int)main.ActualHeight;
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                foreach (var window in new[] { main }.Concat(Application.Current.Windows.OfType<Window>().Where(a => a != main && a.IsVisible)))
                {
                    var picture = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    picture.Render(window);
                    // Every window of this mode covers the whole view.
                    context.DrawImage(picture, new Rect(0, 0, picture.PixelWidth, picture.PixelHeight));
                }
            }

            var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            result.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(result));
            using (var stream = File.Create(Path.Combine(Folder, name + ".png")))
            {
                encoder.Save(stream);
            }
        }
    }
}
#endif
