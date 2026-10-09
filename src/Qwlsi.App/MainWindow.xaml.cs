using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Qwlsi.App.ViewModels;

namespace Qwlsi.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent(); Title = "QWLSI single-frame wavefront " + AppInfo.Version;
        DataContextChanged += (_, _) => { Wire3D(); WireZernikeAll(); };
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

    bool wiredAll;
    /// <summary>Zernike "All frames" page: columns follow the selected terms (rebuilt after every table), header clicks sort (summary rows stay last), clicks on the trend plot select a frame.</summary>
    void WireZernikeAll()
    {
        if (wiredAll || DataContext is not MainViewModel vm) return; wiredAll = true; var a = vm.ZernikeAll;
        a.TableBuilt += () => BuildAllColumns(a);
        AllGrid.Sorting += (s, e) =>
        {
            e.Handled = true; if (e.Column.Header is not string h) return; a.SortBy(h);
            foreach (var c in AllGrid.Columns) c.SortDirection = (c.Header as string) == a.SortHeader ? (a.SortAscending ? ListSortDirection.Ascending : ListSortDirection.Descending) : null;
        };
        AllTrend.FrameClicked += n => a.SelectFrameNumber(n);
    }

    void BuildAllColumns(ZernikeAllViewModel a)
    {
        var right = new Style(typeof(TextBlock)); right.Setters.Add(new Setter(System.Windows.Controls.TextBlock.TextAlignmentProperty, TextAlignment.Right)); right.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0)));
        AllGrid.Columns.Clear();
        AllGrid.Columns.Add(new DataGridTextColumn { Header = "frame", Binding = new Binding(nameof(AllRow.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Auto), MinWidth = 90 });
        AllGrid.Columns.Add(new DataGridTextColumn { Header = "#", Binding = new Binding(nameof(AllRow.NumberText)), Width = new DataGridLength(1, DataGridLengthUnitType.Auto), ElementStyle = right, SortMemberPath = "#" });
        for (int i = 0; i < a.ColumnDefs.Count; i++)
            AllGrid.Columns.Add(new DataGridTextColumn { Header = a.ColumnDefs[i].header, Binding = new Binding($"Cells[{i}]") { StringFormat = a.ColumnDefs[i].format, ConverterCulture = System.Globalization.CultureInfo.InvariantCulture }, Width = new DataGridLength(1, DataGridLengthUnitType.Auto), ElementStyle = right });
    }

    void OnAbout(object sender, RoutedEventArgs e) { new AboutWindow(DataContext as MainViewModel) { Owner = this }.ShowDialog(); }

    void OnDropFiles(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && DataContext is MainViewModel vm) vm.Offline.AddPaths(paths);
    }
}
