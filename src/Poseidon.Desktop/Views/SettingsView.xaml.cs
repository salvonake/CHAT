using System.Windows.Controls;
using Poseidon.Desktop.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace Poseidon.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void OnPassphraseChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && sender is PasswordBox passwordBox)
            vm.EncryptionPassphrase = passwordBox.Password;
    }
}
