using Playnite.Commands;
using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;

namespace Playnite.FullscreenApp
{
    public class NavigateUrlCommand : RelayCommand<object>
    {
        public NavigateUrlCommand() : base(Navigate)
        {
        }

        public static void Navigate(object link)
        {
            // The browser of the mode for the couch doesn't take the person out of it: nothing to ask.
            if (FullscreenApplication.Current.AppSettings.LinkBrowser == Playnite.Atrio.LinkBrowsers.Internal && CefTools.IsInitialized)
            {
                GlobalCommands.NavigateUrl(link);
                return;
            }

            if (FullscreenApplication.Current.Dialogs.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCUrlNavigationMessage"), link.ToString()),
                string.Empty,
                MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                GlobalCommands.NavigateUrl(link);
            }
        }
    }
}
