using Playnite.Atrio;
using Playnite.Common;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.Windows;
using System;
using System.Drawing.Imaging;
using System.Collections.Generic;
using System.Linq;

namespace Playnite.DesktopApp.ViewModels
{
    /// <summary>
    /// Asks from which store a unified game should be started or installed.
    /// </summary>
    public class StoreChooserViewModel : ObservableObject
    {
        private readonly IWindowFactory window;
        private readonly UnifiedGame group;

        public string GameName { get; }
        public string Question { get; }
        public object HeaderImage { get; }
        public List<GameCopyEntry> Copies { get; }

        private bool remember;
        public bool Remember
        {
            get => remember;
            set => SetValue(ref remember, value);
        }

        public Game SelectedCopy { get; private set; }

        public RelayCommand<GameCopyEntry> SelectCommand { get; }
        public RelayCommand<object> CancelCommand { get; }

        public StoreChooserViewModel(IWindowFactory window, UnifiedGame group)
        {
            this.window = window;
            this.group = group;
            GameName = group.Primary.Name;
            var art = group.Copies.Select(a => a.BackgroundImage).FirstOrDefault(a => !a.IsNullOrEmpty()) ??
                      group.Copies.Select(a => a.CoverImage).FirstOrDefault(a => !a.IsNullOrEmpty());
            HeaderImage = art == null ? null : ImageSourceManager.GetImage(art, false, new BitmapLoadProperties(1000, 0));
            Copies = GameCopyEntry.FromGroup(group, GameUnifier.Current.PluginResolver);
            Question = ResourceProvider.GetString(group.Copies.Any(a => a.IsInstalled) ? "LOCAtrioChooserPlay" : "LOCAtrioChooserInstall");
            SelectCommand = new RelayCommand<GameCopyEntry>(Select);
            CancelCommand = new RelayCommand<object>(_ => window.Close(false));
        }

        private void Select(GameCopyEntry copy)
        {
            SelectedCopy = copy?.Game;
            window.Close(true);
        }

        public Game OpenView()
        {
            if (window.CreateAndOpenDialog(this) != true || SelectedCopy == null)
            {
                return null;
            }

            if (Remember)
            {
                GameUnifier.Current.SetPreferredCopy(SelectedCopy);
            }

            return SelectedCopy;
        }
    }
}
