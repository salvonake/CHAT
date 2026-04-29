using System.Windows.Controls;
using Poseidon.Desktop.ViewModels;

namespace Poseidon.Desktop.Views;

public partial class DiagnosticView : UserControl
{
    public DiagnosticView()
    {
        InitializeComponent();
    }

    private void OnSecretPasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is PasswordBox { Tag: DiagnosticIssueViewModel issue } passwordBox)
        {
            issue.SecretValue = passwordBox.Password;
        }
    }
}
