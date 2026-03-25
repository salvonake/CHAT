using System.Windows;
using LegalAI.Desktop.ViewModels;

namespace LegalAI.Desktop;

/// <summary>
/// Main window shell. DataContext is set to MainViewModel by App.xaml.cs.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}