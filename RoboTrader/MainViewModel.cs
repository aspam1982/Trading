using CommonClasses;
using Google.Protobuf.WellKnownTypes;
using NLog;
using NullSoftware;
using NullSoftware.ToolKit;
using RoboTrader.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RobotMovingAverageTrading;
using Grpc.Core;
using System.CodeDom;
using System.Reflection;

namespace RoboTrader
{
    /// <summary>
    /// Главная view-model приложения RoboTrader.
    ///
    /// Класс связывает WPF-интерфейс с выбранным торговым роботом:
    /// создает экземпляр RobotBase, управляет командами Start/Stop/Settings/History,
    /// прокидывает консольный вывод в окно и обновляет состояние кнопок при изменении
    /// статуса робота.
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged
    {
        // Видимость команд зависит от текущего состояния робота и главного окна.
        // Эти свойства используются напрямую из XAML через binding.
        public Visibility StartButtonVisibility { get { return !Robot.IsRunning ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility StopButtonVisibility { get { return Robot.IsRunning ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility ExpandCommandVisibility { get { return App.Current.MainWindow.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility CollapseCommandVisibility { get { return App.Current.MainWindow.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed; } }
        public bool IsSilentModeEnabled { get; set; }

        public ICommand ExpandCommand { get; }
        public ICommand CollapseCommand { get; }
        public ICommand ViewHistoryCommand { get; }
        public ICommand EditSettingsCommand { get; }
        public ICommand CloseCommand { get; }
        public ICommand StartCommand { get; }
        public ICommand ToggleVisibilityCommand { get; }

        public ICommand StopCommand { get; }
        public ICommand CleanConsoleCommand { get; }

        private INotificationService NotificationService { get; set; }
        private string consoleText;

        public string ConsoleText { get => consoleText; set { consoleText = value; NotifyPropertyChanged(); } }
        public RobotBase Robot { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void NotifyPropertyChanged([CallerMemberName] String propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        private static bool InDesignMode()
        {
            return System.ComponentModel.DesignerProperties.GetIsInDesignMode(new DependencyObject());
        }
        public MainViewModel()
        {
            // Окно можно скрывать в трей, поэтому view-model отслеживает его видимость
            // и переключает команды Expand/Collapse без пересоздания робота.
            App.Current.MainWindow.IsVisibleChanged += MainWindow_IsVisibleChanged;
            CollapseCommand = new RelayCommand(() => App.Current.MainWindow.Hide());
            ToggleVisibilityCommand = new RelayCommand(() =>
            {
                if (App.Current.MainWindow.Visibility == Visibility.Visible)
                    App.Current.MainWindow.Hide();
                else
                {
                    App.Current.MainWindow.WindowState = System.Windows.WindowState.Maximized;
                    App.Current.MainWindow.Show();
                }
            });

            ExpandCommand = new RelayCommand(() => {
                App.Current.MainWindow.WindowState = System.Windows.WindowState.Maximized;
                App.Current.MainWindow.Show();
            });
            CloseCommand = new RelayCommand(() => 
            {
                if (MessageBox.Show("Arey you shore you want to close application?", "Application close", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    StopRobot();
                    Application.Current.Shutdown();
                }
            });
            StartCommand = new RelayCommand(() => StartRobot());
            StopCommand = new RelayCommand(() => StopRobot());
            CleanConsoleCommand = new RelayCommand(() => ConsoleText = "");
            if (InDesignMode())
                return;

            // Все Console.WriteLine из робота и вспомогательного кода попадают
            // в текстовую консоль главного окна.
            Console.SetOut(new MainViewModelWriter(this));

            // Логи разделены по назначению: консоль для быстрых предупреждений,
            // Errors.log для разбора сбоев, Robot.log для текущей диагностической ленты.
            NLog.LogManager.Setup().LoadConfiguration(builder =>
            {
                builder.ForLogger().FilterMaxLevel(LogLevel.Warn).WriteToConsole();
                builder.ForLogger().FilterMinLevel(LogLevel.Warn).WriteToFile("Errors.log");
                /*builder.ForLogger().FilterMaxLevel(LogLevel.Warn).WriteToMethodCall((info, o) => {
                    Application.Current.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => NotificationService?.Notify(info.Level.ToString(), info.Message, NotificationType.Information)));
                });*/
                builder.ForLogger().FilterMaxLevel(LogLevel.Warn).WriteToFile(fileName: "Robot.log");
            });
            List<System.Type> robots = new List<System.Type>();
            robots.Add(typeof(RobotMovingAverage));
            robots.Add(typeof(RobotGrokAdvice));
            robots.Add(typeof(RobotGrokAdvice1));

            // Тип робота выбирается по имени из application settings. Это позволяет
            // менять стратегию без изменения XAML и без отдельного контейнера DI.
            var robottype = robots.FirstOrDefault(u => u.Name == Properties.Settings.Default.RobotType);
            if (robottype != null)
            {
                Robot = robottype.GetConstructor(new System.Type[] { }).Invoke(null) as RobotBase;
                Robot.PropertyChanged += Robot_PropertyChanged;
                if (Robot.Settings.AutoStart)
                    StartRobot();
            }
            EditSettingsCommand = new RelayCommand(() => {
                // Settings редактирует копию настроек. Если пользователь нажал OK,
                // робот останавливается, чтобы новые параметры применились чисто.
                if (Robot != null && new Settings(Robot).ShowDialog() == true)
                    Robot.Stop();
            });
            ViewHistoryCommand = new RelayCommand(() => {
                new DepoHistoryViewer(Robot).ShowDialog();
            }, () => Robot!=null && Robot.Settings != null && Robot.IsRunning && !string.IsNullOrEmpty(Robot.Settings.ApiKey) && !string.IsNullOrEmpty(Robot.Settings.Ticker) );
        }

        private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            NotifyPropertyChanged(nameof(ExpandCommandVisibility));
            NotifyPropertyChanged(nameof(CollapseCommandVisibility));
        }

        public void StartRobot()
        {
            // Фактическая торговая логика находится внутри RobotBase-наследников.
            // UI только делегирует запуск и затем реагирует на PropertyChanged.
            Robot?.Start();
        }
        public void StopRobot()
        {
            Robot?.Stop();
        }

        private void Robot_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Robot.IsRunning))
            {
                NotifyPropertyChanged(nameof(StartButtonVisibility));
                NotifyPropertyChanged(nameof(StopButtonVisibility));
            }
            else if (e.PropertyName == nameof(Robot.TitleText))
                NotifyPropertyChanged(nameof(TitleText));
        }

        public string TitleText
        {
            get => Robot == null ? "[Робот не задан]" : Robot.TitleText;
        }
        ~MainViewModel()
        {
            StopRobot();
        }
        private class MainViewModelWriter : TextWriter
        {
            private MainViewModel vm;

            public MainViewModelWriter(MainViewModel vm)
            {
                this.vm = vm;
            }

            public override void WriteLine(string value)
            {
                // Запись может прийти из фонового потока робота, поэтому обновляем
                // binding через Dispatcher главного WPF-потока.
                Application.Current.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => vm.ConsoleText += value + "\r\n"));
            }
            public override Encoding Encoding => Encoding.Default;
        }
    }
}
