using RobotFuturesArbitr;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Xceed.Wpf.Toolkit;

namespace RoboFutureArbitr
{
    /// <summary>
    /// Окно торгового режима. Показывает статистику выбранных фьючерсов,
    /// консоль робота и команды ручного запуска/остановки.
    /// </summary>
    public partial class RobotTrading : Window
    {
        public RobotTrading()
        {
            InitializeComponent();
        }

        private void TextBlock_SourceUpdated(object sender, DataTransferEventArgs e)
        {
            var textBlock = e.OriginalSource as TextBlock;
            string text = textBlock.Text;
            // Статистика робота приходит с простыми тегами форматирования, здесь превращаем их в WPF Inlines.
            Regex b = new Regex(@"(<b>([\s\S]*?)<\/b>)|(<red>([\s\S]*?)<\/red>)|(<green>([\s\S]*?)<\/green>)|(<blue>([\s\S]*?)<\/blue>)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            var mc = b.Matches(text);
            int lastidx = 0;
            textBlock.Text = "";
            foreach (Match m in mc)
            {
                if (m.Index != lastidx)
                    textBlock.Inlines.Add(new Run(text.Substring(lastidx, m.Index - lastidx)));
                if (m.Groups[2].Success)
                    textBlock.Inlines.Add(new Bold(new Run(text.Substring(m.Groups[2].Index, m.Groups[2].Length))));
                else if (m.Groups[4].Success)
                    textBlock.Inlines.Add(new Span(new Run(text.Substring(m.Groups[4].Index, m.Groups[4].Length)) { Foreground = new SolidColorBrush(System.Windows.Media.Colors.Red) }));
                else if (m.Groups[6].Success)
                    textBlock.Inlines.Add(new Span(new Run(text.Substring(m.Groups[6].Index, m.Groups[6].Length)) { Foreground = new SolidColorBrush(System.Windows.Media.Colors.Green) }));
                else if (m.Groups[8].Success)
                    textBlock.Inlines.Add(new Span(new Run(text.Substring(m.Groups[8].Index, m.Groups[8].Length)) { Foreground = new SolidColorBrush(System.Windows.Media.Colors.Blue) }));
                lastidx = m.Index + m.Length;
            }
            textBlock.Inlines.Add(new Run(text.Substring(lastidx, text.Length - lastidx)));
        }

        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            (sender as TextBox).ScrollToEnd();
        }
    }
    public class CustomTextFormatter : ITextFormatter
    {
        public string GetText(FlowDocument document)
        {
            return ""; // throw new NotImplementedException();
        }


        public void SetText(FlowDocument document, string text)
        {
            // Custom logic to deserialize the string into a FlowDocument
            // For example, you could parse JSON or XML and create the document elements
            // Here, we'll create a simple document with a single paragraph
            var flowDocument = document;
            flowDocument.Blocks.Clear();
            Regex b = new Regex(@"(<b>([\s\S]*?)<\/b>)|(<red>([\s\S]*?)<\/red>)|(<green>([\s\S]*?)<\/green>)|(<blue>([\s\S]*?)<\/blue>)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
            var mc = b.Matches(text);
            int lastidx = 0;
            var paragraph = new Paragraph();
            foreach (Match m in mc)
            {
                if (m.Index != lastidx)
                    paragraph.Inlines.Add(new Run(text.Substring(lastidx, m.Index - lastidx)));
                if (m.Groups[2].Success)
                    paragraph.Inlines.Add(new Bold(new Run(text.Substring(m.Groups[2].Index, m.Groups[2].Length))));
                else if (m.Groups[4].Success)
                    paragraph.Inlines.Add(new Span(new Run(text.Substring(m.Groups[4].Index, m.Groups[4].Length)) { Foreground = new SolidColorBrush(System.Windows.Media.Colors.Red) }));
                else if (m.Groups[6].Success)
                    paragraph.Inlines.Add(new Span(new Run(text.Substring(m.Groups[6].Index, m.Groups[6].Length)) { Foreground = new SolidColorBrush(System.Windows.Media.Colors.Green) }));
                else if (m.Groups[8].Success)
                    paragraph.Inlines.Add(new Span(new Run(text.Substring(m.Groups[8].Index, m.Groups[8].Length)) { Foreground = new SolidColorBrush(System.Windows.Media.Colors.Blue) }));
                lastidx = m.Index + m.Length;
            }
            paragraph.Inlines.Add(new Run(text.Substring(lastidx, text.Length - lastidx)));
            flowDocument.Blocks.Add(paragraph);
        }
    }
}
