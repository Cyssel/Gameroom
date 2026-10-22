using Playnite.Controls;
using Playnite.Windows;

namespace Playnite.DesktopApp.Windows
{
    public class GameSavesWindowFactory : WindowFactory
    {
        public override WindowBase CreateNewWindowInstance()
        {
            return new GameSavesWindow();
        }
    }

    public partial class GameSavesWindow : WindowBase
    {
        public GameSavesWindow() : base()
        {
            InitializeComponent();
        }
    }
}
