using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using CommonClasses;
using NLog;
using RoboTrader.Properties;

namespace RoboTrader;


/// <summary>
/// Главное окно RoboTrader.
///
/// Окно намеренно не содержит торговой логики: вся работа с роботом находится в
/// MainViewModel и классах RobotBase. Code-behind здесь отвечает только за
/// жизненный цикл окна.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // Закрытие крестиком прячет окно в трей, но не останавливает робота.
        // Полное завершение выполняется через команду Exit, где робот явно останавливается.
        this.Hide();
        e.Cancel = true;
    }

    private void TrayIcon_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {

    }
}
