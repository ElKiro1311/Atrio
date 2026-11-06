using CefSharp;
using CefSharp.Wpf;
using Playnite.Atrio;
using Playnite.Common;
using Playnite.SDK;
using Playnite.WebView;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Input;

namespace Playnite.DesktopApp.ViewModels
{
    /// <summary>
    /// The browser of Atrio. It is a page of the application, next to the others, and it shares its
    /// sessions with the views used to connect the stores.
    /// </summary>
    public class BrowserViewModel : ObservableObject
    {
        private readonly DesktopAppViewModel main;
        private ChromiumWebBrowser browser;
        private bool visitedSteam;

        /// <summary>Created with the first page: most of the time nobody needs it.</summary>
        public ChromiumWebBrowser Browser => browser ?? (browser = CreateBrowser());

        private string address;
        public string Address
        {
            get => address;
            set => SetValue(ref address, value);
        }

        public RelayCommand<object> GoCommand { get; }
        public RelayCommand<object> OpenOutsideCommand { get; }
        public RelayCommand<object> CloseCommand { get; }

        public BrowserViewModel(DesktopAppViewModel main)
        {
            this.main = main;
            GoCommand = new RelayCommand<object>(_ => Go());
            OpenOutsideCommand = new RelayCommand<object>(_ =>
            {
                if (!Address.IsNullOrEmpty())
                {
                    ProcessStarter.StartUrl(Address)?.Dispose();
                }
            });
            CloseCommand = new RelayCommand<object>(_ =>
            {
                main.CloseBrowser();
                Browser.LoadUrl("about:blank");
            });
        }

        private ChromiumWebBrowser CreateBrowser()
        {
            var view = new ChromiumWebBrowser
            {
                RequestHandler = new InAppBrowserRequestHandler(),
                LifeSpanHandler = new InAppBrowserLifeSpanHandler(),
                DownloadHandler = new InAppBrowserDownloadHandler()
            };

            view.IsBrowserInitializedChanged += (_, e) =>
            {
                if (e.NewValue is bool initialized && initialized)
                {
                    InAppBrowserLanguage.Apply(view, main.AppSettings.Language);
                }
            };

            view.AddressChanged += (_, e) =>
            {
                Address = e.NewValue as string;
                visitedSteam |= Address?.IndexOf("steam", StringComparison.OrdinalIgnoreCase) >= 0;
            };

            view.PreviewMouseDown += (_, e) =>
            {
                if (e.ChangedButton == MouseButton.XButton1 && view.CanGoBack)
                {
                    view.Back();
                    e.Handled = true;
                }
                else if (e.ChangedButton == MouseButton.XButton2 && view.CanGoForward)
                {
                    view.Forward();
                    e.Handled = true;
                }
            };

            return view;
        }

        public void Open(string url)
        {
            Address = url;
            Browser.Address = url;
            Browser.Focus();
        }

        private void Go()
        {
            var target = Address?.Trim();
            if (target.IsNullOrEmpty())
            {
                return;
            }

            Open(Regex.IsMatch(target, @"^[a-zA-Z][a-zA-Z0-9+.\-]*://") ? target : "https://" + target);
        }

        public void OnShown()
        {
            SetMuted(false);
        }

        /// <summary>A trailer shouldn't keep playing behind the library.</summary>
        public void OnHidden()
        {
            SetMuted(true);

            // Signing in or entering the PIN of Family View are done here: the pages of the games should notice.
            if (visitedSteam)
            {
                visitedSteam = false;
                SteamFriends.ForgetSession();
                main.SelectedGameDetails?.ReloadFriends();
            }
        }

        private void SetMuted(bool muted)
        {
            if (browser?.IsBrowserInitialized == true)
            {
                browser.GetBrowserHost()?.SetAudioMuted(muted);
            }
        }
    }
}
