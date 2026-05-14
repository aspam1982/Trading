using System.Windows;

namespace RoboFutureArbitr
{
    /// <summary>
    /// Главное окно-селектор. Оно не выполняет торговую логику само, а только
    /// разделяет три сценария запуска: сбор рыночных данных, анализ сохраненной
    /// истории и рабочий режим торгового робота.
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void OpenDataCollector_Click(object sender, RoutedEventArgs e)
        {
            // Сборщик открывает потоковые подписки и пишет историю сделок в файл.
            new DataCollector().Show();
        }

        private void OpenDataPresenter_Click(object sender, RoutedEventArgs e)
        {
            // Аналитическое окно работает с уже собранным файлом истории.
            new DataPresenter().Show();
        }

        private void OpenRobotTrading_Click(object sender, RoutedEventArgs e)
        {
            // Торговое окно создает MainViewModel, а тот инициализирует RobotFuturesArbitr.
            new RobotTrading().Show();
        }
    }
}
