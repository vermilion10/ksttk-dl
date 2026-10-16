using System.Windows;
using KsttkDl.App.ViewModels;

namespace KsttkDl.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        StateChanged += (_, _) => UpdateFrame();
    }

    // a chromeless window overhangs the screen by its resize border when maximized
    private void UpdateFrame()
    {
        var maximized = WindowState == WindowState.Maximized;
        Frame.Margin = maximized ? new Thickness(7) : new Thickness(0);
        Frame.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        MaxButton.Content = maximized ? "" : "";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
