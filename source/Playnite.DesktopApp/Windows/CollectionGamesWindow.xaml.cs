using Playnite.Controls;
using Playnite.Windows;

namespace Playnite.DesktopApp.Windows
{
    public class CollectionGamesWindowFactory : WindowFactory
    {
        public override WindowBase CreateNewWindowInstance()
        {
            return new CollectionGamesWindow();
        }
    }

    public partial class CollectionGamesWindow : WindowBase
    {
        public CollectionGamesWindow() : base()
        {
            InitializeComponent();
        }
    }
}
