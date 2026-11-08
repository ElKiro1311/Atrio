using CefSharp;
using CefSharp.Wpf;
using Playnite.Atrio;
using Playnite.Controls;
using Playnite.Input;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.WebView;
using Playnite.Windows;
using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Playnite.FullscreenApp.Windows
{
    /// <summary>
    /// The browser of the mode for the couch, for links and guides. The page fills the screen and the left
    /// stick moves a pointer drawn on top of it: A clicks, the right stick scrolls, B goes back, X writes in
    /// the field, Y goes to an address or searches. It shares the sessions of the browser of the desktop.
    /// </summary>
    public class CouchBrowserWindow : WindowBase
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly ChromiumWebBrowser browser;
        private readonly FrameworkElement pointer;
        private readonly TranslateTransform pointerPlace = new TranslateTransform();
        private readonly TextBlock titleText;
        private readonly TextBlock addressText;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private TimeSpan lastFrame;
        private Point position;
        private Vector pendingScroll;
        private bool keysWereSimulated = true;
        private bool clicking;

        /// <summary>The browser that is open, for the bench of tests.</summary>
        internal static CouchBrowserWindow Current { get; private set; }

        internal ChromiumWebBrowser Browser => browser;
        internal Point PointerPosition => position;

        /// <summary>Opens the page in the browser of the mode for the couch. False when it can't be shown.</summary>
        public static bool Open(string url)
        {
            if (!CefTools.IsInitialized || url.IsNullOrEmpty())
            {
                return false;
            }

            if (Current != null)
            {
                Current.browser.LoadUrl(url);
                return true;
            }

            var window = new CouchBrowserWindow(url);
            var owner = WindowManager.CurrentWindow;
            if (owner != null && owner != window && owner.IsVisible)
            {
                window.Owner = owner;
            }

            window.ShowDialog();
            return true;
        }

        private CouchBrowserWindow(string url) : base()
        {
            SetResourceReference(StyleProperty, "ChildWindowStyle");
            ShowInTaskbar = false;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowTools.ConfigureChildWindow(this);
            var scale = double.IsNaN(Height) ? 1 : Math.Max(1, Height / 1080);

            browser = new ChromiumWebBrowser
            {
                RequestHandler = new InAppBrowserRequestHandler(),
                LifeSpanHandler = new InAppBrowserLifeSpanHandler(),
                Address = url
            };

            zoom = CouchBrowser.GetStartZoom(scale);
            browser.IsBrowserInitializedChanged += (_, e) =>
            {
                if (e.NewValue is bool ready && ready)
                {
                    InAppBrowserLanguage.Apply(browser, FullscreenApplication.Current?.AppSettings.Language);
                    ApplyZoom();
                }
            };

            // Chromium keeps a zoom for each site: every new page gets the one of this browser.
            browser.FrameLoadEnd += (_, e) =>
            {
                if (e.Frame.IsMain)
                {
                    e.Browser.GetHost()?.SetZoomLevel(zoom);
                }
            };

            titleText = new TextBlock { FontSize = 24, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            addressText = new TextBlock { FontSize = 18, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
            addressText.Text = url;
            browser.TitleChanged += (_, e) => titleText.Text = e.NewValue as string;
            browser.AddressChanged += (_, e) => addressText.Text = e.NewValue as string;

            // A real mouse also works: the drawn pointer steps aside until a stick moves again.
            browser.MouseMove += (_, e) =>
            {
                position = e.GetPosition(browser);
                pointer.Visibility = Visibility.Collapsed;
            };

            pointer = CreatePointer(scale);
            var page = new Grid { ClipToBounds = true };
            page.Children.Add(browser);
            var overlay = new Canvas { IsHitTestVisible = false };
            overlay.Children.Add(pointer);
            page.Children.Add(overlay);

            var top = new DockPanel { Margin = new Thickness(40, 14, 40, 14), LayoutTransform = new ScaleTransform(scale, scale) };
            top.Children.Add(titleText);
            top.Children.Add(addressText);

            var hints = new WrapPanel { Margin = new Thickness(40, 12, 40, 14), LayoutTransform = new ScaleTransform(scale, scale) };
            AddHint(hints, ConfirmationPrompt(), "LOCAtrioBrowserClick");
            AddHint(hints, CancellationPrompt(), "LOCAtrioBrowserBack");
            AddHint(hints, "ButtonPromptX", "LOCAtrioBrowserWrite");
            AddHint(hints, "ButtonPromptY", "LOCAtrioBrowserGo");
            AddHint(hints, "ButtonPromptStart", "LOCAtrioBrowserEnter");
            AddHint(hints, "ButtonPromptRS", "LOCAtrioBrowserScroll");
            AddHint(hints, "ButtonPromptRT", "LOCAtrioBrowserZoom");
            AddHint(hints, "ButtonPromptBack", "LOCAtrioBrowserClose");

            var root = new Grid();
            root.SetResourceReference(Panel.BackgroundProperty, "MainBackgourndBrush");
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(top, 0);
            Grid.SetRow(page, 1);
            Grid.SetRow(hints, 2);
            root.Children.Add(top);
            root.Children.Add(page);
            root.Children.Add(hints);
            Content = root;

            AddHandler(Keyboard.KeyDownEvent, new KeyEventHandler(OnPadKey), true);
            AddHandler(Keyboard.KeyUpEvent, new KeyEventHandler(OnPadKey), true);
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private static string ConfirmationPrompt()
        {
            return GameControllerGesture.ConfirmationBinding == ControllerInput.B ? "ButtonPromptB" : "ButtonPromptA";
        }

        private static string CancellationPrompt()
        {
            return GameControllerGesture.CancellationBinding == ControllerInput.A ? "ButtonPromptA" : "ButtonPromptB";
        }

        private static void AddHint(Panel panel, string prompt, string text)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 34, 4) };
            var icon = new ContentControl { Focusable = false, Height = 30, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            icon.SetResourceReference(ContentControl.ContentTemplateProperty, prompt);
            item.Children.Add(icon);
            item.Children.Add(new TextBlock { Text = ResourceProvider.GetString(text), FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(item);
        }

        private FrameworkElement CreatePointer(double scale)
        {
            var arrow = new Path
            {
                Data = Geometry.Parse("M 0,0 L 0,26 L 7,19.5 L 12,30 L 16.5,28 L 11.5,17.5 L 20,17.5 Z"),
                Fill = Brushes.White,
                Stroke = Brushes.Black,
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false,
                Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 2, Opacity = 0.45 }
            };

            var group = new TransformGroup();
            group.Children.Add(new ScaleTransform(scale, scale));
            group.Children.Add(pointerPlace);
            arrow.RenderTransform = group;
            return arrow;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Current = this;
            var controllers = FullscreenApplication.Current?.GameController;
            if (controllers != null)
            {
                // The stick moves the pointer here: as arrows it would also scroll the page.
                keysWereSimulated = controllers.SimulateNavigationKeys;
                controllers.SimulateNavigationKeys = false;
            }

            position = new Point(browser.ActualWidth / 2, browser.ActualHeight / 2);
            MovePointer();
            browser.Focus();
            CompositionTarget.Rendering += OnRendering;
        }

        private void OnClosed(object sender, EventArgs e)
        {
            CompositionTarget.Rendering -= OnRendering;
            var controllers = FullscreenApplication.Current?.GameController;
            if (controllers != null)
            {
                controllers.SimulateNavigationKeys = keysWereSimulated;
            }

            if (Current == this)
            {
                Current = null;
            }

            browser.Dispose();
        }

        private IBrowserHost Host => browser.IsBrowserInitialized ? browser.GetBrowser()?.GetHost() : null;

        private void MovePointer()
        {
            pointerPlace.X = position.X;
            pointerPlace.Y = position.Y;
            pointer.Visibility = Visibility.Visible;
            Host?.SendMouseMoveEvent(new MouseEvent((int)position.X, (int)position.Y, clicking ? CefEventFlags.LeftMouseButton : CefEventFlags.None), false);
        }

        private void OnRendering(object sender, EventArgs e)
        {
            var now = clock.Elapsed;
            var seconds = Math.Min(0.05, (now - lastFrame).TotalSeconds);
            lastFrame = now;
            if (!IsActive)
            {
                return;
            }

            var left = new Vector();
            var right = new Vector();
            var fine = new Vector();
            try
            {
                foreach (var controller in FullscreenApplication.Current?.GameController?.Controllers?.ToList() ?? new System.Collections.Generic.List<GameControllerManager.LoadedGameController>())
                {
                    if (!controller.Enabled)
                    {
                        continue;
                    }

                    var axes = controller.LastAnalogInputState;
                    left += new Vector(CouchBrowser.ToAxis(axes[ControllerInput.LeftStickX]), CouchBrowser.ToAxis(axes[ControllerInput.LeftStickY]));
                    right += new Vector(CouchBrowser.ToAxis(axes[ControllerInput.RightStickX]), CouchBrowser.ToAxis(axes[ControllerInput.RightStickY]));
                    var buttons = controller.LastInputState;
                    fine += new Vector(
                        (buttons[ControllerInput.DPadRight] == ControllerInputState.Pressed ? 1 : 0) - (buttons[ControllerInput.DPadLeft] == ControllerInputState.Pressed ? 1 : 0),
                        (buttons[ControllerInput.DPadDown] == ControllerInputState.Pressed ? 1 : 0) - (buttons[ControllerInput.DPadUp] == ControllerInputState.Pressed ? 1 : 0));
                }
            }
            catch (InvalidOperationException)
            {
                // A controller came or went while reading.
                return;
            }

            MoveBy((CouchBrowser.GetPointerSpeed(left.X, left.Y, Math.Max(600, browser.ActualWidth * 0.9)) + (fine * 240)) * seconds);
            ScrollBy(CouchBrowser.GetScrollSpeed(right.X, right.Y) * seconds);
        }

        /// <summary>Moves the pointer, in pixels of the page. Also used by the bench of tests.</summary>
        internal void MoveBy(Vector distance)
        {
            if (distance.Length < 0.01)
            {
                return;
            }

            position = CouchBrowser.Clamp(position + distance, browser.ActualWidth, browser.ActualHeight);
            MovePointer();
        }

        /// <summary>Scrolls the page where the pointer is; small amounts wait until they make a pixel.</summary>
        internal void ScrollBy(Vector distance)
        {
            pendingScroll += distance;
            var x = (int)pendingScroll.X;
            var y = (int)pendingScroll.Y;
            if (x == 0 && y == 0)
            {
                return;
            }

            pendingScroll -= new Vector(x, y);
            Host?.SendMouseWheelEvent(new MouseEvent((int)position.X, (int)position.Y, CefEventFlags.None), x, y);
        }

        internal void Press(bool down)
        {
            var host = Host;
            if (host == null)
            {
                return;
            }

            if (down)
            {
                host.SetFocus(true);
            }

            clicking = down;
            host.SendMouseClickEvent(new MouseEvent((int)position.X, (int)position.Y, CefEventFlags.None), MouseButtonType.Left, !down, 1);
        }

        private void OnPadKey(object sender, KeyEventArgs e)
        {
            if (e is GameControllerInputEventArgs pad)
            {
                var pressed = e.RoutedEvent == Keyboard.KeyDownEvent;
                e.Handled = true;
                if (pad.Button == GameControllerGesture.ConfirmationBinding)
                {
                    if (pressed != clicking)
                    {
                        Press(pressed);
                    }

                    return;
                }

                // View and Menu together go back to the open game: alone they act when let go.
                if (pad.Button == ControllerInput.Back || pad.Button == ControllerInput.Start)
                {
                    OnViewOrMenu(pad.Button, pressed);
                    return;
                }

                if (!pressed)
                {
                    return;
                }

                if (pad.Button == GameControllerGesture.CancellationBinding)
                {
                    GoBackOrClose();
                    return;
                }

                switch (pad.Button)
                {
                    case ControllerInput.X:
                        Write();
                        break;
                    case ControllerInput.Y:
                        AskAddress();
                        break;
                    case ControllerInput.LeftShoulder:
                        if (browser.CanGoBack)
                        {
                            browser.Back();
                        }

                        break;
                    case ControllerInput.RightShoulder:
                        if (browser.CanGoForward)
                        {
                            browser.Forward();
                        }

                        break;
                    case ControllerInput.TriggerLeft:
                        Zoom(-0.5);
                        break;
                    case ControllerInput.TriggerRight:
                        Zoom(0.5);
                        break;
                }
            }
            else if (e.RoutedEvent == Keyboard.KeyDownEvent && e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        }

        private bool viewDown;
        private bool menuDown;
        private bool bothPressed;

        private void OnViewOrMenu(ControllerInput button, bool pressed)
        {
            var isView = button == ControllerInput.Back;
            if (pressed)
            {
                if (isView)
                {
                    viewDown = true;
                }
                else
                {
                    menuDown = true;
                }

                bothPressed |= viewDown && menuDown;
                return;
            }

            var wasDown = isView ? viewDown : menuDown;
            if (isView)
            {
                viewDown = false;
            }
            else
            {
                menuDown = false;
            }

            if (wasDown && !bothPressed)
            {
                if (isView)
                {
                    Close();
                }
                else
                {
                    SendEnter();
                }
            }

            if (!viewDown && !menuDown)
            {
                bothPressed = false;
            }
        }

        internal void GoBackOrClose()
        {
            if (browser.CanGoBack)
            {
                browser.Back();
            }
            else
            {
                Close();
            }
        }

        private double zoom;

        private void Zoom(double step)
        {
            zoom = Math.Max(-3, Math.Min(6, zoom + step));
            ApplyZoom();
        }

        private void ApplyZoom()
        {
            Host?.SetZoomLevel(zoom);
        }

        /// <summary>Writes in the field of the page that has the focus, with the keyboard of the mode for the couch.</summary>
        internal void Write()
        {
            var input = new TextInputWindow().ShowInput(this, ResourceProvider.GetString("LOCAtrioBrowserWriteTitle"), string.Empty, string.Empty);
            if (input.Result && !input.SelectedString.IsNullOrEmpty())
            {
                Type(input.SelectedString);
            }
        }

        /// <summary>Types text in the page as a keyboard would, one character after another.</summary>
        internal void Type(string text)
        {
            var host = Host;
            if (host == null)
            {
                return;
            }

            host.SetFocus(true);
            foreach (var character in text)
            {
                host.SendKeyEvent(new KeyEvent { Type = KeyEventType.Char, WindowsKeyCode = character, Modifiers = CefEventFlags.None });
            }
        }

        internal void SendEnter()
        {
            var host = Host;
            if (host == null)
            {
                return;
            }

            host.SetFocus(true);
            host.SendKeyEvent(new KeyEvent { Type = KeyEventType.RawKeyDown, WindowsKeyCode = 0x0D, NativeKeyCode = 0x1C });
            host.SendKeyEvent(new KeyEvent { Type = KeyEventType.Char, WindowsKeyCode = 0x0D, NativeKeyCode = 0x1C });
            host.SendKeyEvent(new KeyEvent { Type = KeyEventType.KeyUp, WindowsKeyCode = 0x0D, NativeKeyCode = 0x1C });
        }

        private void AskAddress()
        {
            var input = new TextInputWindow().ShowInput(this, ResourceProvider.GetString("LOCAtrioBrowserGoTitle"), string.Empty, string.Empty);
            if (input.Result)
            {
                Go(input.SelectedString);
            }
        }

        internal void Go(string typed)
        {
            var target = CouchBrowser.GetTarget(typed);
            if (target != null)
            {
                logger.Debug("Couch browser goes to a new address.");
                browser.LoadUrl(target);
            }
        }
    }
}
