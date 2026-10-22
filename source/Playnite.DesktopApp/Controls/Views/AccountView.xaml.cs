using Playnite.DesktopApp.ViewModels;
using System.Windows.Controls;
using System.Windows.Input;

namespace Playnite.DesktopApp.Controls.Views
{
    public partial class AccountView : UserControl
    {
        private readonly AccountViewModel model;

        public AccountView(AccountViewModel model)
        {
            InitializeComponent();
            this.model = model;
            DataContext = model;
        }

        private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            // Enter submits the form shown at the moment.
            var command = model.IsSignUpMode ? model.SignUpCommand : model.SignInCommand;
            if (command.CanExecute(PasswordInput))
            {
                command.Execute(PasswordInput);
            }
        }
    }
}
