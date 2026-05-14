using CommonClasses;
using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace RoboTrader
{
    /// <summary>
    /// Окно редактирования настроек выбранного робота.
    ///
    /// Работает с копией RobotBaseSettings, чтобы пользователь мог закрыть окно
    /// без применения изменений. Копия создается через Serialize/Deserialize,
    /// потому что конкретный тип настроек зависит от выбранного робота.
    /// </summary>
    public partial class Settings : Window
    {
        RobotBase Robot;
        RobotBaseSettings RobotSettings;
        public Settings(RobotBase robot)
        {
            Robot = robot;
            InitializeComponent();

            // Создаем копию настроек конкретного наследника RobotBaseSettings.
            // Reflection нужен, потому что Deserialize<T>() должен получить runtime-тип.
            Type t = robot.Settings.GetType();
            MethodInfo method = typeof(RobotBaseSettings).GetMethod("Deserialize");
            MethodInfo genericMethod = method.MakeGenericMethod(t);
            RobotSettings = (RobotBaseSettings)genericMethod.Invoke(null, new object[] { robot.Settings.Serialize() });
            this.DataContext = RobotSettings;
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            // Только после подтверждения копия заменяет рабочие настройки робота
            // и сохраняется в постоянное хранилище.
            Robot.Settings = RobotSettings;
            Robot.Settings.SaveSettings();
            DialogResult = true;
        }

    }
}
