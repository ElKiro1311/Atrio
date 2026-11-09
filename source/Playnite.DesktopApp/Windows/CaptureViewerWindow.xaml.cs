using Playnite.Atrio;
using Playnite.Controls;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Playnite.DesktopApp.Windows
{
    /// <summary>The captures of one game, one by one, in a window of Atrio. Clips open in the player of Windows.</summary>
    public class CaptureViewerModel : ObservableObject
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly List<GameCapture> captures;
        private readonly string gameName;
        private int index;
        private int loading;

        public string WindowTitle { get; }

        private BitmapSource picture;
        public BitmapSource Picture
        {
            get => picture;
            private set => SetValue(ref picture, value);
        }

        private string infoText;
        public string InfoText
        {
            get => infoText;
            private set => SetValue(ref infoText, value);
        }

        public bool IsVideo => Current?.IsVideo == true;
        public GameCapture Current => index >= 0 && index < captures.Count ? captures[index] : null;

        public RelayCommand<object> PreviousCommand { get; }
        public RelayCommand<object> NextCommand { get; }
        public RelayCommand<object> PlayCommand { get; }
        public RelayCommand<object> OpenFolderCommand { get; }

        public CaptureViewerModel(string gameName, List<GameCapture> captures, int index)
        {
            this.gameName = gameName;
            this.captures = captures ?? new List<GameCapture>();
            WindowTitle = string.Format(ResourceProvider.GetString("LOCAtrioCapturesWindow"), gameName);
            PreviousCommand = new RelayCommand<object>(_ => Show(this.index - 1), _ => this.index > 0);
            NextCommand = new RelayCommand<object>(_ => Show(this.index + 1), _ => this.index < this.captures.Count - 1);
            PlayCommand = new RelayCommand<object>(_ => Play(), _ => IsVideo);
            OpenFolderCommand = new RelayCommand<object>(_ => OpenFolder(), _ => Current != null);
            Show(index);
        }

        public void Show(int newIndex)
        {
            if (captures.Count == 0)
            {
                return;
            }

            index = Math.Max(0, Math.Min(captures.Count - 1, newIndex));
            var capture = captures[index];
            var culture = Localization.ApplicationLanguageCultureInfo;
            InfoText = string.Format(
                ResourceProvider.GetString("LOCAtrioCapturesInfo"),
                index + 1,
                captures.Count,
                capture.Source,
                capture.Date.ToString("f", culture));
            OnPropertyChanged(nameof(IsVideo));
            OnPropertyChanged(nameof(Current));
            CommandManager.InvalidateRequerySuggested();
            Load(capture);
        }

        /// <summary>The big picture comes from the disk; a quick press of the arrows only shows the last one asked.</summary>
        private async void Load(GameCapture capture)
        {
            var ticket = System.Threading.Interlocked.Increment(ref loading);
            var width = (int)Math.Min(3840, SystemParameters.PrimaryScreenWidth * 1.5);
            BitmapSource image = null;
            try
            {
                image = await Task.Run(() => capture.IsVideo ? CaptureThumbnails.GetShellThumbnail(capture.Path, 1280) : CaptureThumbnails.GetPicture(capture.Path, width));
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                logger.Debug(e, $"Failed to show the capture {capture.Path}.");
            }

            if (ticket == loading)
            {
                Picture = image;
            }
        }

        private void Play()
        {
            var capture = Current;
            if (capture == null)
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(capture.Path) { UseShellExecute = true });
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, $"Failed to open the clip {capture.Path}.");
            }
        }

        private void OpenFolder()
        {
            var capture = Current;
            if (capture == null)
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{capture.Path}\"") { UseShellExecute = true });
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Warn(e, $"Failed to show the capture {capture.Path} in its folder.");
            }
        }
    }

    public partial class CaptureViewerWindow : WindowBase
    {
        public CaptureViewerWindow() : base()
        {
            InitializeComponent();
        }

        /// <summary>Opens the captures of a game on the window of Atrio, starting at that one.</summary>
        public static void Open(string gameName, List<GameCapture> captures, int index)
        {
            var window = new CaptureViewerWindow { DataContext = new CaptureViewerModel(gameName, captures, index) };
            window.Owner = PlayniteApplication.CurrentNative?.Windows.Cast<Window>().FirstOrDefault(a => a.IsActive) ?? PlayniteApplication.CurrentNative?.MainWindow;
            window.Show();
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            var model = DataContext as CaptureViewerModel;
            if (model != null)
            {
                if (e.Key == Key.Left && model.PreviousCommand.CanExecute(null))
                {
                    model.PreviousCommand.Execute(null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Right && model.NextCommand.CanExecute(null))
                {
                    model.NextCommand.Execute(null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    Close();
                    e.Handled = true;
                }
            }

            base.OnPreviewKeyDown(e);
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
