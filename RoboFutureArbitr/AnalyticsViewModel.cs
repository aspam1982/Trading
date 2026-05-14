using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using CommonClasses;
using RobotFuturesArbitr;
using Tinkoff.InvestApi.V1;

namespace RoboFutureArbitr
{
    /// <summary>
    /// ViewModel окна статистики торгового робота.
    /// Получает операции, портфель и последние цены через RobotFuturesArbitr,
    /// группирует их по базовой акции и связанным фьючерсам для разбора результата.
    /// </summary>
    public class AnalyticsViewModel:INotifyPropertyChanged
    {
        private DateTime datefrom = DateTime.Now.AddDays(-1);
        private DateTime dateto = DateTime.Now;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void NotifyPropertyChanged([CallerMemberName] String propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        public RobotFuturesArbitr.RobotFuturesArbitr Robot { get; set; }
        public DateTime DateFrom 
        { 
            get => datefrom; 
            set 
            {
                if (value < dateto)
                {
                    datefrom = value;
                    NotifyPropertyChanged();
                }
            } 
        }
        public DateTime DateTo 
        { 
            get => dateto; 
            set 
            {
                if (value > datefrom)
                {
                    dateto = value;
                    NotifyPropertyChanged();
                }
            }
        }
        public bool IncludingCommission
        {
            get => includingCommission; set { if (value != includingCommission) { includingCommission = value; NotifyPropertyChanged(); } }
        }
        List<TreeViewItem> instrumentstreeview = new List<TreeViewItem>();
        private TreeViewItem selectedItem;
        private List<OperationPresenter> operations = new List<OperationPresenter>();

        public List<TreeViewItem> InstrumentsTreeView
        {
            get 
            {
                return instrumentstreeview;
            }
            set
            {
                instrumentstreeview = value;
                NotifyPropertyChanged();
            }
        }
        public Dictionary<string, Future> AllFutures { get; set; } = new Dictionary<string, Future>();
        public Dictionary<string, Share> AllShares { get; set; } = new Dictionary<string, Share>();
        public Dictionary<Share, List<Future>> AllActives { get; set; } = new Dictionary<Share, List<Future>>();
        public AnalyticsViewModel(RobotFuturesArbitr.RobotFuturesArbitr robot)
        {
            Robot = robot;
            if (Robot == null)
                return;
            var allinstruments = Robot.GetAllInstruments();
            AllFutures = allinstruments.Item1;
            AllShares = allinstruments.Item2;
            AllActives = new Dictionary<Share, List<Future>>();
            foreach (var s in AllShares)
                AllActives.Add(s.Value, AllFutures.Where(u => u.Value.BasicAsset == s.Key).Select(u => u.Value).ToList());
            this.PropertyChanged += AnalyticsViewModel_PropertyChanged1;
            NotifyPropertyChanged(nameof(DateFrom));
        }
        public List<PositionsFutures> MyFutures = new List<PositionsFutures>();
        public List<PositionsSecurities> MyShares = new List<PositionsSecurities>();
        public List<PositionsMoney> MyMoney = new List<PositionsMoney>();
        public List<Tinkoff.InvestApi.V1.LastPrice> LastPrices = new List<Tinkoff.InvestApi.V1.LastPrice>();
        private bool includingCommission;

        private void AnalyticsViewModel_PropertyChanged1(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DateFrom) || e.PropertyName == nameof(DateTo) || e.PropertyName == nameof(IncludingCommission))
            {
                var ops = Robot.GetLastOperations(DateFrom, DateTo);
                var portfolio = Robot.GetLastPortfolio();
                MyFutures = portfolio.Item3;
                MyShares = portfolio.Item2;
                MyMoney = portfolio.Item1;
                Operations = ops.Where(u => (AllShares.Select(s => s.Value.Uid).Contains(u.InstrumentUid) || AllFutures.Select(f => f.Value.Uid).Contains(u.InstrumentUid)) && u.State == OperationState.Executed)
                               .Select(u => new OperationPresenter { Operation = u, vm = this }).Reverse().ToList();
                if (!IncludingCommission)
                    Operations = Operations.Where(u => u.Operation.OperationType != OperationType.BrokerFee).ToList();
                LastPrices = Robot.GetLastPrices();
                var tv = new List<TreeViewItem>();
                foreach (var s in AllActives)
                {
                    if (Operations.Any(o => s.Value.Select(u => u.Uid).Contains(o.Operation.InstrumentUid)) || Operations.Any(o => o.Operation.InstrumentUid == s.Key.Uid))
                    {
                        var itm = new TreeViewItem();
                        itm.Header = $"[{s.Key.Ticker}] - {s.Key.Name}";
                        itm.Tag = s.Key;
                        tv.Add(itm);
                        foreach (var f in s.Value.Where(f => Operations.Any(o => f.Uid == o.Operation.InstrumentUid)))
                        {
                            var fitm = new TreeViewItem();
                            fitm.Header = $"[{f.Ticker}] - {f.Name}";
                            fitm.Tag = f;
                            itm.Items.Add(fitm);
                        }
                    }
                }
                InstrumentsTreeView = tv;
            }
            else if (e.PropertyName == nameof(SelectedItem) || e.PropertyName == nameof(Operations))
            {
                NotifyPropertyChanged(nameof(SelectedOperations));
                NotifyPropertyChanged(nameof(TotalInfo));
            }
        }

        public TreeViewItem SelectedItem 
        { 
            get => selectedItem;
            set
            {
                selectedItem = value;
                NotifyPropertyChanged();
            }
        }

        public AnalyticsViewModel():this(null)
        {
        }
        List<OperationPresenter> Operations
        {
            get => operations;
            set
            {
                operations = value;
                NotifyPropertyChanged();
            }
        }
        public List<OperationPresenter> SelectedOperations
        {
            get 
            {
                if (SelectedItem == null)
                    return Operations.ToList();
                else
                {
                    Share s = null;
                    List<string> uids = new List<string>();
                    if (SelectedItem.Tag is Share)
                    {
                        s = (SelectedItem.Tag as Share);
                        uids.Add(s.Uid);
                        uids.AddRange(AllActives[s].Select(u => u.Uid));
                    }
                    else
                    {
                        var f = (SelectedItem.Tag as Future);
                        uids.Add(f.Uid);
                        uids.Add(AllShares[f.BasicAsset].Uid);
                    }
                    return Operations.Where(o => uids.Contains(o.Operation.InstrumentUid)).ToList();
                }
            }
        }
        public (List<PositionsSecurities> ,List<PositionsFutures>) SelectedPositions
        {
            get
            {
                if (SelectedItem == null)
                    return (MyShares,MyFutures);
                else
                {
                    Share s = null;
                    List<string> uids = new List<string>();
                    if (SelectedItem.Tag is Share)
                    {
                        s = (SelectedItem.Tag as Share);
                        uids.Add(s.Uid);
                        uids.AddRange(AllActives[s].Select(u => u.Uid));
                    }
                    else
                    {
                        var f = (SelectedItem.Tag as Future);
                        uids.Add(f.Uid);
                        uids.Add(AllShares[f.BasicAsset].Uid);
                    }
                    return (MyShares.Where(o => uids.Contains(o.InstrumentUid)).ToList(), MyFutures.Where(o => uids.Contains(o.InstrumentUid)).ToList());
                }
            }
        }
        public string TotalInfo
        {
            get
            {
                var summ = 0d;
                var so = SelectedOperations;
                so.ForEach(u => summ += Helper.FromMoneyValue(u.Operation.Payment));
                var sp = SelectedPositions;
                foreach(var p in sp.Item2)
                {
                    Future f = AllFutures.First(u => u.Value.Uid == p.InstrumentUid).Value;
                    summ += (p.Balance + p.Blocked) * Helper.FromQuotation(LastPrices.First(u => u.InstrumentUid == f.Uid).Price) / Helper.FromQuotation(f.MinPriceIncrement) * Helper.FromQuotation(f.MinPriceIncrementAmount, 1);
                }
                foreach (var p in sp.Item1)
                {
                    Share s = AllShares.First(u => u.Value.Uid == p.InstrumentUid).Value;
                    summ += (p.Balance + p.Blocked) * Helper.FromQuotation(LastPrices.First(u => u.InstrumentUid == s.Uid).Price);
                }
                return $"Итог = {summ:C}";
            }
        }

        public class OperationPresenter
        {
            public Operation Operation { get; set; }
            public AnalyticsViewModel vm { get; set; }
            public override string ToString()
            {
                if (vm.AllShares.Any(u => u.Value.Uid == Operation.InstrumentUid))
                {
                    var share = vm.AllShares.First(u => u.Value.Uid == Operation.InstrumentUid).Value;
                    return $"{Operation.Date.ToDateTime().ToLocalTime():G} [{share.Ticker}] {share.Name} {Operation.Date.ToDateTime().ToLocalTime()} {Operation.OperationType} {Helper.FromMoneyValue(Operation.Payment):C}";
                }
                else
                {
                    var Future = vm.AllFutures.First(u => u.Value.Uid == Operation.InstrumentUid).Value;
                    return $"{Operation.Date.ToDateTime().ToLocalTime():G} [{Future.Ticker}] {Future.Name} {Operation.Date.ToDateTime().ToLocalTime()} {Operation.OperationType} {Helper.FromMoneyValue(Operation.Payment):C}";
                }
            }

        }
    }
}
