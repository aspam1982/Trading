using System.Windows;

namespace RoboAINewsReader;

/// <summary>
/// Стартовое окно-селектор. Не выполняет сетевые запросы и расчеты само,
/// а только открывает один из двух рабочих сценариев приложения.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OpenNewsForecast_Click(object sender, RoutedEventArgs e)
    {
        new NewsForecastWindow().Show();
    }

    private void OpenCandleForecaster_Click(object sender, RoutedEventArgs e)
    {
        new CandleForecasterWindow().Show();
    }
}
