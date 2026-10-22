using Playnite.Controls;
using Playnite.Windows;

namespace Playnite.DesktopApp.Windows
{
    public class GameSessionsWindowFactory : WindowFactory
    {
        public override WindowBase CreateNewWindowInstance()
        {
            return new GameSessionsWindow();
        }
    }

    public partial class GameSessionsWindow : WindowBase
    {
        public GameSessionsWindow() : base()
        {
            InitializeComponent();
        }
    }
}
