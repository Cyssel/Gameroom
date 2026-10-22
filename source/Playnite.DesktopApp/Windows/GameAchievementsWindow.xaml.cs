using Playnite.Controls;
using Playnite.Windows;

namespace Playnite.DesktopApp.Windows
{
    public class GameAchievementsWindowFactory : WindowFactory
    {
        public override WindowBase CreateNewWindowInstance()
        {
            return new GameAchievementsWindow();
        }
    }

    public partial class GameAchievementsWindow : WindowBase
    {
        public GameAchievementsWindow() : base()
        {
            InitializeComponent();
        }
    }
}
