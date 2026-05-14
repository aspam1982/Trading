using System;
using System.Collections.Generic;
using System.Linq;
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

namespace RoboFutureArbitr
{
    /// <summary>
    /// Логика взаимодействия для Analytics.xaml
    /// </summary>
    public partial class Analytics : Window
    {
        public Analytics()
        {
            InitializeComponent();
        }

        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (DataContext != null)
                (DataContext as AnalyticsViewModel).SelectedItem = (tv.SelectedItem == null ? null : tv.SelectedItem as TreeViewItem);
        }
    }
}
