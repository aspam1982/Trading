using CommonClasses;
using NLog;
using NullSoftware;
using NullSoftware.ToolKit;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace RoboFutureArbitr
{
    /// <summary>
    /// ViewModel торгового режима. Собирает настройки приложения, создает
    /// RobotFuturesArbitr, управляет командами Start/Stop и выводит диагностический
    /// текст робота в консольное поле окна RobotTrading.
    /// </summary>
    public class MainViewModel:INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void NotifyPropertyChanged([CallerMemberName] String propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        private static bool InDesignMode()
        {
            return System.ComponentModel.DesignerProperties.GetIsInDesignMode(new DependencyObject());
        }
        private INotificationService NotificationService { get; set; }
        private string consoleText;

        public string ConsoleText { get => consoleText; set { consoleText = value; NotifyPropertyChanged(); } }
        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand CleanConsoleCommand { get; }
        public ICommand StatisticsCommand { get; }
        public RobotFuturesArbitr.RobotFuturesArbitr Robot { get; set; }

        AnalyticsViewModel analytics = null;
        public MainViewModel()
        {
            if (InDesignMode())
                return;

            // Робот пишет часть диагностики через Console.WriteLine, поэтому перенаправляем вывод в UI.
            Console.SetOut(new MainViewModelWriter(this));
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
            var apikey = WindowsCredentialManager.ReadSecret(Properties.Settings.Default.ApiKey??"key not found");
            // Параметры стратегии берутся из App.config, а секрет API хранится в Windows Credential Manager.
            Robot = new RobotFuturesArbitr.RobotFuturesArbitr(
                apikey,
                Properties.Settings.Default.StartTradeDeviationPercent,
                Properties.Settings.Default.CloseTradeDeviationPercent,
                Properties.Settings.Default.FutureMinAverageDayVolume,
                Properties.Settings.Default.SecurityGap,
                Properties.Settings.Default.MaximumFutureExpirationDelayDays,
                Properties.Settings.Default.AvoidDividendsDays,
                Properties.Settings.Default.MaxDealMoneyToBook,
                Properties.Settings.Default.AVGBuyCandlesCount,
                Properties.Settings.Default.AVGSellCandlesCount,
                Properties.Settings.Default.MaxCandlesToStore);
            StartCommand = new RelayCommand(() => StartRobot());
            StopCommand = new RelayCommand(() => StopRobot());
            CleanConsoleCommand = new RelayCommand(() => ConsoleText = "");
            // При открытии торгового окна сохраняем прежнее поведение: робот стартует сразу.
            StartCommand.Execute(null);
            StatisticsCommand = new RelayCommand(() => {
                if (analytics == null)
                    analytics = new AnalyticsViewModel(Robot);
                new Analytics { DataContext = analytics }.ShowDialog();
            });
        }
        private void StartRobot ()
        {
            Robot.Start();
        }
        private void StopRobot ()
        {
            Robot.Stop();
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
                Application.Current.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => { vm.ConsoleText += value + "\r\n"; }));
            }
            public override Encoding Encoding => Encoding.Default;
        }
    }
}
