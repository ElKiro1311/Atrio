using Playnite.Atrio;
using Playnite.Commands;
using Playnite.Controls;
using Playnite.DesktopApp.ViewModels;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Playnite.DesktopApp.Windows
{
    /// <summary>The posts of a game, one by one and whole, with the notes of each patch.</summary>
    public class ActivityReaderModel : ObservableObject
    {
        // The part of the page every post shares: lists, tables and quotes the way they are easy to read.
        private const string PageStyle = @"<style>
            p { margin: 0 0 12px 0; }
            li p { margin: 0; }
            li { margin: 0 0 6px 0; }
            ul, ol { margin: 0 0 14px 0; padding-left: 26px; }
            h1 { font-size: 24px; margin: 22px 0 10px 0; }
            h2 { font-size: 20px; margin: 20px 0 8px 0; }
            h3 { font-size: 17px; margin: 18px 0 8px 0; }
            h4, h5, h6 { font-size: 15px; margin: 14px 0 6px 0; }
            img { max-width: 100%; margin: 6px 0 6px 0; }
            blockquote { margin: 10px 0 14px 0; padding: 4px 0 4px 14px; border-left: 3px solid #808080; }
            pre, code { font-family: Consolas; }
            hr { border: none; border-top: 1px solid #606060; margin: 16px 0 16px 0; }
            table.notes { border-collapse: collapse; margin: 8px 0 14px 0; }
            table.notes td, table.notes th { border: 1px solid #606060; padding: 6px 10px 6px 10px; text-align: left; }
            .spoiler { background-color: #4a4f58; color: #4a4f58; }
            .video { margin: 10px 0 14px 0; }
            </style>";

        private readonly List<ActivityEntry> entries;
        private int index;

        public string WindowTitle { get; }
        public event EventHandler Shown;

        public ActivityEntry Current => index >= 0 && index < entries.Count ? entries[index] : null;
        public string Label => Current?.Label?.ToUpper(Localization.ApplicationLanguageCultureInfo);
        public string PostTitle => Current?.Title;
        public string DateText => Current?.Post?.Date?.ToString("D", Localization.ApplicationLanguageCultureInfo);
        public string InfoText => string.Format(ResourceProvider.GetString("LOCAtrioActivityPosition"), index + 1, entries.Count);
        public string Html => Current == null ? string.Empty : PageStyle + Shorten(Current.Post.Html, Current.Post.Url);

        /// <summary>The text is drawn at once: one as long as a book would stop the window. The rest is on the web.</summary>
        private const int MaxLength = 60000;

        public static string Shorten(string html, string url)
        {
            if (html == null || html.Length <= MaxLength)
            {
                return html;
            }

            var end = new[] { "</p>", "</ul>", "</li>", "<br>", "</h4>", "</h3>" }.Select(a => html.LastIndexOf(a, MaxLength, StringComparison.OrdinalIgnoreCase) + a.Length).Max();
            var cut = html.Substring(0, end > MaxLength / 2 ? end : MaxLength);
            return url.IsNullOrEmpty() ? cut : $"{cut}<p><a href=\"{System.Net.WebUtility.HtmlEncode(url)}\">{ResourceProvider.GetString("LOCAtrioActivityRest")}</a></p>";
        }

        /// <summary>The picture of the card, unless the post starts with it.</summary>
        public string ImageUrl
        {
            get
            {
                var url = Current?.FullImageUrl ?? Current?.ImageUrl;
                if (url.IsNullOrEmpty() || Current.Post.Html.Length == 0)
                {
                    return url;
                }

                var first = Regex.Match(Current.Post.Html, @"<img[^>]+src=""([^""]+)""", RegexOptions.IgnoreCase);
                return first.Success && (first.Index < 400 || first.Groups[1].Value == url) ? null : url;
            }
        }

        public RelayCommand<object> PreviousCommand { get; }
        public RelayCommand<object> NextCommand { get; }
        public RelayCommand<object> OpenWebCommand { get; }

        /// <summary>The page opened inside Atrio, behind this window.</summary>
        public event EventHandler OpenedBehind;

        public ActivityReaderModel(string gameName, List<ActivityEntry> entries, int index)
        {
            this.entries = entries ?? new List<ActivityEntry>();
            WindowTitle = string.Format(ResourceProvider.GetString("LOCAtrioActivityWindow"), gameName);
            PreviousCommand = new RelayCommand<object>(_ => Show(this.index - 1), _ => this.index > 0);
            NextCommand = new RelayCommand<object>(_ => Show(this.index + 1), _ => this.index < this.entries.Count - 1);
            OpenWebCommand = new RelayCommand<object>(_ => OpenUrl(Current.Post.Url), _ => Current?.Post?.Url.IsNullOrEmpty() == false);
            Show(index);
        }

        public void OpenUrl(string url)
        {
            if (WebLinks.Open(SteamMarkup.SafeUrl(url)))
            {
                OpenedBehind?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Show(int newIndex)
        {
            if (entries.Count == 0)
            {
                return;
            }

            index = Math.Max(0, Math.Min(entries.Count - 1, newIndex));
            foreach (var name in new[] { nameof(Current), nameof(Label), nameof(PostTitle), nameof(DateText), nameof(InfoText), nameof(ImageUrl), nameof(Html) })
            {
                OnPropertyChanged(name);
            }

            CommandManager.InvalidateRequerySuggested();
            Shown?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The links of the windows that are in front of the page of a game.</summary>
    public static class WebLinks
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        /// <summary>True when it opened inside Atrio, behind the window that asks for it.</summary>
        public static bool Open(string url)
        {
            if (url.IsNullOrEmpty())
            {
                return false;
            }

            try
            {
                GlobalCommands.NavigateUrl(url);
                return PlayniteApplication.Current?.AppSettings?.LinkBrowser == LinkBrowsers.Internal;
            }
            catch (Exception e) when (!PlayniteEnvironment.ThrowAllErrors)
            {
                logger.Error(e, $"Failed to open {url}.");
                return false;
            }
        }
    }

    public partial class ActivityReaderWindow : WindowBase
    {
        private readonly ActivityReaderModel model;

        public ActivityReaderWindow(ActivityReaderModel model) : base()
        {
            this.model = model;
            DataContext = model;
            InitializeComponent();
            Body.LinkHandler = model.OpenUrl;
            Body.ImageLoader = LoadImageAsync;
            model.OpenedBehind += (_, __) => Close();
            model.Shown += (_, __) => Scroll.ScrollToTop();
            PreviewKeyDown += ActivityReaderWindow_PreviewKeyDown;
        }

        /// <summary>Only from the web, at the width of the text, and kept like the other pictures of the page.</summary>
        private static Task<BitmapSource> LoadImageAsync(string source)
        {
            var url = SteamMarkup.SafeUrl(source);
            return url == null ? Task.FromResult<BitmapSource>(null) : WebImage.GetAsync(url, 1100);
        }

        private void ActivityReaderWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var command = e.Key == Key.Left ? model.PreviousCommand : e.Key == Key.Right ? model.NextCommand : null;
            if (command?.CanExecute(null) == true)
            {
                command.Execute(null);
                e.Handled = true;
            }
        }

        public static void Open(string gameName, List<ActivityEntry> entries, int index)
        {
            if (entries == null || entries.Count == 0)
            {
                return;
            }

            var window = new ActivityReaderWindow(new ActivityReaderModel(gameName, entries, index))
            {
                Owner = PlayniteApplication.CurrentNative?.Windows.Cast<Window>().FirstOrDefault(a => a.IsActive) ?? PlayniteApplication.CurrentNative?.MainWindow
            };

            // As tall as the screen of the window lets it be: the notes of a patch are long.
            var owner = window.Owner;
            if (owner != null && owner.ActualHeight > 0)
            {
                window.Height = Math.Max(560, Math.Min(1100, owner.ActualHeight * 0.9));
            }

            window.Show();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
