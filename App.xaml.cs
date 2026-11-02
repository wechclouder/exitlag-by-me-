using System.Windows;
using wetwiLAG.ViewModels;

namespace wetwiLAG;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var viewModel = await MainViewModel.CreateAsync();
            var window = new MainWindow { DataContext = viewModel };
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"wetwiLAG could not initialize safely.{Environment.NewLine}{Environment.NewLine}{exception.Message}",
                "Startup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
