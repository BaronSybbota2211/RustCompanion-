using System.Windows;

namespace RustCompanion.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent(); Icon = AppIcon.Source;
        StateChanged += (_, _) => UpdateWindowState();
        UpdateWindowState();
    }

    /// <summary>Развёрнутое окно с WindowChrome выходит за экран на ширину системной рамки — компенсируется отступом; значок кнопки меняется на «восстановить».</summary>
    private void UpdateWindowState()
    {
        var max = WindowState == WindowState.Maximized;
        RootFrame.Margin = max ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaxGlyph.Visibility = max ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = max ? Visibility.Visible : Visibility.Collapsed;
        MaxButton.ToolTip = max ? "Восстановить" : "Развернуть";
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void OnMaxRestore(object sender, RoutedEventArgs e) { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }
    /// <summary>Закрытие идёт через обычное событие Closing: в App.xaml.cs окно скрывается в трей (CloseToTray) и приложение продолжает работать.</summary>
    private void OnClose(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
}
