using System.Windows;
using Poseidon.Desktop.ViewModels;

namespace Poseidon.Desktop.Views;

public partial class StartupDiagnosticWindow : Window
{
    public StartupDiagnosticWindow(DiagnosticViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        FlowDirection = FlowDirection.LeftToRight;
    }
}
