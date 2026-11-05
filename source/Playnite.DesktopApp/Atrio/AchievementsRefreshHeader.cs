using Playnite.Common;
using System.Collections;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Playnite.DesktopApp.Atrio
{
    /// <summary>
    /// The refresh controls on the page of the achievements add-on, which is not a reference of this project.
    /// A refresh started there runs in the background. The menus of the add-on run it in a window that blocks
    /// the app for as long as it takes, and closing or hiding that window cancels the refresh.
    /// </summary>
    internal class AchievementsRefreshHeader
    {
        private readonly object header;

        private AchievementsRefreshHeader(object header)
        {
            this.header = header;
        }

        /// <summary>Null while the page is showing its first steps instead of the achievements.</summary>
        public static AchievementsRefreshHeader Find(DependencyObject page)
        {
            foreach (var control in ElementTreeHelper.FindVisualChildren<UserControl>(page))
            {
                var header = control.GetType().GetProperty("ActiveRefreshHeader")?.GetValue(control);
                if (header != null)
                {
                    return new AchievementsRefreshHeader(header);
                }
            }

            return null;
        }

        public bool IsRefreshing => Read("IsRefreshing") as bool? == true;

        public string CancelText => Read("RefreshOrCancelButtonText") as string;

        /// <summary>The kinds of refresh that don't depend on what is selected on the page.</summary>
        public Dictionary<string, string> GetModes()
        {
            var modes = new Dictionary<string, string>();
            foreach (var mode in (IEnumerable)Read("RefreshModes"))
            {
                var key = mode.GetType().GetProperty("Key").GetValue(mode) as string;
                if (!key.Contains("Single") && !key.Contains("SelectedGame"))
                {
                    modes[key] = mode.GetType().GetProperty("ShortDisplayName").GetValue(mode) as string;
                }
            }

            return modes;
        }

        public void Start(string mode)
        {
            header.GetType().GetProperty("SelectedRefreshMode").SetValue(header, mode);
            StartOrCancel();
        }

        public void StartOrCancel()
        {
            (Read("RefreshOrCancelCommand") as ICommand)?.Execute(null);
        }

        private object Read(string property)
        {
            return header.GetType().GetProperty(property).GetValue(header);
        }
    }
}
