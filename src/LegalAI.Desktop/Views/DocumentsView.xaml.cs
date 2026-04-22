using System.Windows;
using LegalAI.Desktop.ViewModels;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace LegalAI.Desktop.Views;

public partial class DocumentsView : UserControl
{
    public DocumentsView()
    {
        InitializeComponent();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            e.Effects = files.Any(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        var pdfFiles = files
            .Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (pdfFiles.Length == 0) return;

        if (DataContext is DocumentsViewModel vm)
        {
            await vm.IngestFilesCommand.ExecuteAsync(pdfFiles.AsEnumerable());
        }
    }
}
