using CommonClasses;
using Google.Protobuf.WellKnownTypes;
using ScottPlot;
using ScottPlot.Plottables;
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
using Tinkoff.InvestApi;
using Tinkoff.InvestApi.V1;
namespace RoboTrader
{
    /// <summary>
    /// Окно анализа истории депозита текущего робота.
    ///
    /// Строит график изменения депозита, рядом показывает свечи целевого инструмента
    /// за тот же период и подгружает операции покупки/продажи по счету.
    /// </summary>
    public partial class DepoHistoryViewer : Window
    {
        RobotBase robot;
        public DepoHistoryViewer(RobotBase robot)
        {
            InitializeComponent();
            this.robot = robot;
            RefreshDisplay();
        }
        public void RefreshDisplay()
        {
            if (robot == null || robot.Settings == null || string.IsNullOrEmpty(robot.Settings.ApiKey))
                return;

            // История депозита хранится в формате свечей, чтобы ее можно было
            // агрегировать в M1/H1/D1 и отображать аналогично рыночному графику.
            var depohistory = robot.GetDepoHistory();
            if (!depohistory.Any())
                return;
            ScottPlot.WPF.WpfPlot plot = new ScottPlot.WPF.WpfPlot();
            plot.Multiplot.AddPlots(2);
            var depoplot = plot.Multiplot.GetPlot(0);
            depoplot.Title("Размер депозита");
            var tickerplot = plot.Multiplot.GetPlot(1);
            var dfrom = DateTime.SpecifyKind(depohistory.Min(u => u.Time), DateTimeKind.Utc);
            var dto = DateTime.SpecifyKind(depohistory.Max(u => u.Time).AddHours(1), DateTimeKind.Utc);
            var candletimespan = new TimeSpan(0, 1, 0);
            var historicaltimeframe = HistoricalTimeFrame.M1;
            var candleinterval = Tinkoff.InvestApi.V1.CandleInterval._1Min;
            bool takehours = true;
            bool takeminutes = true;
            switch (timeframeselector.SelectedIndex)
            {
                case 0:
                    candletimespan = new TimeSpan(0, 1, 0);
                    historicaltimeframe = HistoricalTimeFrame.M1;
                    candleinterval = CandleInterval._1Min;
                    takehours = true;
                    takeminutes = true;
                    break;
                case 1:
                    candletimespan = new TimeSpan(1, 0, 0);
                    historicaltimeframe = HistoricalTimeFrame.H1;
                    candleinterval = CandleInterval.Hour;
                    takeminutes = false;
                    takehours = true;
                    break;
                case 2:
                    candletimespan = new TimeSpan(24, 0, 0);
                    historicaltimeframe = HistoricalTimeFrame.D1;
                    candleinterval = CandleInterval.Day;
                    takeminutes = false;
                    takehours = false;
                    break;
            }

            // Агрегируем историю депозита в выбранный таймфрейм. Это делает график
            // читаемым на длинных периодах и синхронизирует его со свечами тикера.
            List<HistoricalCandle> aggreagatedcandles = new List<HistoricalCandle>();
            HistoricalCandle lastcandle = null;
            foreach(var c in depohistory)
            {
                DateTime candletime = new DateTime(c.Time.Year, c.Time.Month, c.Time.Day, takehours ? c.Time.Hour : 0, takeminutes ? c.Time.Minute : 0, 0, DateTimeKind.Utc);
                if (lastcandle != null)
                {
                    if (lastcandle.Time == candletime)
                    {
                        lastcandle.High = Math.Max(lastcandle.High, c.High);
                        lastcandle.Low = Math.Min(lastcandle.Low, c.Low);
                        lastcandle.Close = c.Close;
                    }
                    else
                    {
                        aggreagatedcandles.Add(lastcandle);
                        lastcandle = null;
                    }
                }
                if (lastcandle == null)
                {
                    lastcandle = new HistoricalCandle(c);
                    lastcandle.Time = candletime;
                }
            }
            if (lastcandle != null)
                aggreagatedcandles.Add(lastcandle);
            if (!aggreagatedcandles.Any())
                return;
            var area = depoplot.Add.Scatter(aggreagatedcandles.Select(u => u.Time.ToLocalTime()).ToArray(), aggreagatedcandles.Select(u => u.Price).ToArray());
            area.FillY = true;
            area.FillYColor = ScottPlot.Colors.Green.WithAlpha(50);
            area.MarkerShape = MarkerShape.None;
            area.Color = ScottPlot.Colors.Blue;
            depoplot.Add.Candlestick(aggreagatedcandles.Select(u => new OHLC(u.Open, u.High, u.Low, u.Close, u.Time.ToLocalTime(), candletimespan)).ToArray());
            InvestApiClient client = InvestApiClientFactory.Create(WindowsCredentialManager.ReadSecret(robot.Settings.ApiKey)??"key not found");
            try
            {
                // Для визуальной проверки поведения робота рядом с equity-графиком
                // загружаем свечи инструмента и список реальных операций по счету.
                var shares = client.Instruments.Shares().Instruments;
                var share = client.Instruments.Shares().Instruments.FirstOrDefault(u => u.Ticker == robot.Settings.Ticker);
                tickerplot.Title($"Целевой интсрумент [{share.Ticker}] {share.Name}");
                var sharedata = HistoricalData.ReadHistoricalData(share.Ticker, share.Figi, historicaltimeframe, false, new HistoricalData.QueryDataDelegate((figi, frame, from, to) =>
                {
                    var candles = client.MarketData.GetCandles(new GetCandlesRequest { Figi = figi, Interval = candleinterval, From = from.ToTimestamp(), To = to.ToTimestamp() });
                    return candles.Candles.Where(u => u.IsComplete).Select(u => u.ToHistoricalCandle()).ToList();
                }));
                var candles = sharedata.GetData(aggreagatedcandles.First().Time, aggreagatedcandles.Last().Time.Add(candletimespan)).ToList();//.Where(u => u.Time.ToLocalTime().Hour >= 11 && u.Time.ToLocalTime().Hour <= 20).ToList();
                if (sharedata.DataHasChanges)
                    sharedata.SaveHistoricalData();
                tickerplot.Add.Candlestick(candles.Select(u => new OHLC(u.Open, u.High, u.Low, u.Close, u.Time.ToLocalTime(), candletimespan)).ToArray());
                var acc = client.Users.GetAccounts().Accounts.FirstOrDefault();
                var ops = client.Operations.GetOperations(new Tinkoff.InvestApi.V1.OperationsRequest { AccountId = acc.Id, From = dfrom.ToTimestamp(), To = dto.ToTimestamp(), State = Tinkoff.InvestApi.V1.OperationState.Executed }).Operations;
                operations.ItemsSource = ops.Where(u => u.OperationType is Tinkoff.InvestApi.V1.OperationType.Buy or Tinkoff.InvestApi.V1.OperationType.Sell).OrderByDescending(u => u.Date).Select(u => $"{u.Date.ToDateTime().ToLocalTime():g} {u.InstrumentType}\r\n\t{u.OperationType} {Helper.FromMoneyValue(u.Payment):C}");
            }
            catch (Exception ex)
            {
                return;
            }
            PixelPadding padding = new(left: 100, right: 10, bottom: 50, top: 50);
            foreach (var subplot in plot.Multiplot.GetPlots())
            {
                subplot.Layout.Fixed(padding);
                subplot.Axes.DateTimeTicksBottom();
            }
            //padding.Top = 50;
            //depoplot.Layout.Fixed(padding);
            plot.Multiplot.SharedAxes.ShareX(plot.Multiplot.GetPlots());
            depoplot.Axes.AutoScale();
            depoplot.Axes.SetLimitsY(aggreagatedcandles.Min(u => u.Low), aggreagatedcandles.Max(u => u.High));
            plotcontainer.Children.Clear();
            plotcontainer.Children.Add(plot);
            plot.Refresh();
        }

        private void timeframeselector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshDisplay();
        }
    }
}
