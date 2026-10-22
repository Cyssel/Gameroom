using Playnite.Controls;
using Playnite.Windows;

namespace Playnite.DesktopApp.Windows
{
    public class PlayStatisticsWindowFactory : WindowFactory
    {
        public override WindowBase CreateNewWindowInstance()
        {
            return new PlayStatisticsWindow();
        }
    }

    public partial class PlayStatisticsWindow : WindowBase
    {
        public PlayStatisticsWindow() : base()
        {
            InitializeComponent();
        }
    }
}
