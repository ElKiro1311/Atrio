using Playnite.Controls;
using Playnite.Windows;

namespace Playnite.DesktopApp.Windows
{
    public class StoreChooserWindowFactory : WindowFactory
    {
        public override WindowBase CreateNewWindowInstance()
        {
            return new StoreChooserWindow();
        }
    }

    public partial class StoreChooserWindow : WindowBase
    {
        public StoreChooserWindow() : base()
        {
            InitializeComponent();
        }
    }
}
