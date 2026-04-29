using System.Windows;
using Poseidon.Desktop.ViewModels;

namespace Poseidon.Desktop.Views;

/// <summary>
/// First-run setup wizard window. Shown before the main window
/// when required model files are not yet available.
/// </summary>
public partial class SetupWizardWindow : Window
{
    public SetupWizardWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Wires the ViewModel's RequestClose event to close this window.
    /// Called by App.xaml.cs after setting DataContext.
    /// </summary>
    public void BindViewModel(SetupWizardViewModel vm)
    {
        DataContext = vm;
        vm.RequestClose += () => Dispatcher.Invoke(Close);
    }
}

