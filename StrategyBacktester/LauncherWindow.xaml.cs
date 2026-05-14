using System.Windows;

namespace StrategyBacktester;

public partial class LauncherWindow : Window
{
    public LauncherWindow()
    {
        InitializeComponent();
    }

    private void LaunchEmaCrossoverBacktest(object sender, RoutedEventArgs e)
    {
        new EmaCrossoverBacktest().Show();
    }

    private void LaunchPairsCorrelation(object sender, RoutedEventArgs e)
    {
        new PairsCorrelation().Show();
    }

    private void LaunchGridStrategy(object sender, RoutedEventArgs e)
    {
        new GridStrategy().Show();
    }

    private void LaunchGrokAdvice(object sender, RoutedEventArgs e)
    {
        new GrokAdvice().Show();
    }

    private void LaunchGrokAdvice1(object sender, RoutedEventArgs e)
    {
        new GrokAdvice1().Show();
    }

    private void LaunchOrderbookDensity(object sender, RoutedEventArgs e)
    {
        new OrderbookDensity().Show();
    }

    private void LaunchOrderbookTimeDensity(object sender, RoutedEventArgs e)
    {
        new OrderbookTimeDensity().Show();
    }
}

