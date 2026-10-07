using System.Windows;
using Qwlsi.App.ViewModels;

namespace Qwlsi.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent(); Title = "QWLSI single-frame wavefront " + AppInfo.Version;
        DataContextChanged += (_, _) => Wire3D();
    }

    bool wired;
    void Wire3D()
    {
        if (wired || DataContext is not MainViewModel vm) return; wired = true; var s = vm.Surface3D;
        s.SceneChanged += (rebuild, reset) =>
        {
            if (s.Mesh == null) { View3D.Clear("No result yet — process a frame on the Offline tab"); return; }
            if (rebuild) View3D.SetScene(s.Mesh, s.Options, reset); else View3D.UpdateOptions(s.Options);
        };
        s.ViewRequested += v => View3D.SetView(v);
        s.ScreenshotRequested += path => { try { View3D.SavePng(path); vm.StatusMessage = "3-D screenshot saved: " + path; } catch (Exception e) { vm.StatusMessage = "Screenshot failed: " + e.Message; } };
        s.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(s.Software) && View3D.IsSoftware != s.Software) { View3D.UseEffectsManager(s.Software); if (s.Mesh != null) View3D.SetScene(s.Mesh, s.Options, false); } };
        View3D.RendererFallback += msg => s.NotifyFallback(msg);
    }

    void OnAbout(object sender, RoutedEventArgs e) { new AboutWindow(DataContext as MainViewModel) { Owner = this }.ShowDialog(); }

    void OnDropFiles(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && DataContext is MainViewModel vm) vm.Offline.AddPaths(paths);
    }
}
