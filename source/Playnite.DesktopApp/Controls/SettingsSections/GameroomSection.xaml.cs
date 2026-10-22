using System.Windows;
using System.Windows.Controls;

namespace Playnite.DesktopApp.Controls.SettingsSections
{
    public partial class GameroomSection : UserControl
    {
        public GameroomSection()
        {
            InitializeComponent();
        }

        private void ScanInstalled_Click(object sender, RoutedEventArgs e)
        {
            var dialogs = DesktopApplication.Current?.MainModel?.Dialogs;
            if (dialogs != null)
            {
                GameroomMenu.ScanInstalledGames(dialogs);
            }
        }

        private void TestAchievement_Click(object sender, RoutedEventArgs e)
        {
            Gameroom.GameroomService.Instance?.ShowTestAchievement();
        }
    }
}
