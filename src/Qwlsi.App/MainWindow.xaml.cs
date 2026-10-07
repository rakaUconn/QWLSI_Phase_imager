using System.Windows;
using Qwlsi.App.ViewModels;

namespace Qwlsi.App;

public partial class MainWindow : Window
{
    public MainWindow() { InitializeComponent(); }

    void OnDropFiles(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && DataContext is MainViewModel vm) vm.Offline.AddPaths(paths);
    }
}
