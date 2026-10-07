using System.Diagnostics;
using System.IO;
using System.Windows;
using Qwlsi.App.ViewModels;
using Qwlsi.Core;

namespace Qwlsi.App;

public partial class AboutWindow : Window
{
    readonly MainViewModel? vm;
    public AboutWindow(MainViewModel? vm)
    {
        InitializeComponent(); this.vm = vm; Title = "About QWLSI single-frame wavefront " + AppInfo.Version;
        Title1.Text = "QWLSI single-frame wavefront  " + AppInfo.Version;
        Sub.Text = "Reconstructs OPD and complex field from one raw quadri-wave lateral shearing frame. " + (char)0x00A9 + " 2026 UH Physiological Optics Lab. Third-party components: see THIRD-PARTY-NOTICES.txt in the program folder.";
        Refresh();
    }

    /// <summary>The full report as it would be pasted into a bug report (also what the button copies).</summary>
    public string ReportText => Report.Text;
    void Refresh() { Report.Text = AppDiagnostics.Build(vm, includeSettings: true, logLines: 40); Report.ScrollToHome(); }
    void OnRefresh(object s, RoutedEventArgs e) { Refresh(); Note.Text = "refreshed"; }
    void OnCopy(object s, RoutedEventArgs e) { try { Clipboard.SetText(AppDiagnostics.Build(vm, includeSettings: true, logLines: 80)); Note.Text = "Diagnostics copied to the clipboard - paste them into your bug report."; AppLog.Info("diagnostics copied to clipboard from the About box"); } catch (Exception ex) { Note.Text = "Could not copy: " + ex.Message; } }
    void OnOpenLogs(object s, RoutedEventArgs e) { try { Directory.CreateDirectory(AppLog.Dir); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppLog.Dir}\"") { UseShellExecute = true }); } catch (Exception ex) { Note.Text = ex.Message; } }
}
