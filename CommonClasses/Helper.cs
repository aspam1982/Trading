using Newtonsoft.Json;
using NLog;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Documents.Serialization;
using Tinkoff.InvestApi;
using Tinkoff.InvestApi.V1;
using Xceed.Wpf.Toolkit.PropertyGrid.Attributes;

namespace CommonClasses
{
    /// <summary>
    /// Общие функции преобразования типов T-Invest API, расчета гарантийного обеспечения,
    /// сопоставления таймфреймов и вспомогательные операции для свечных рядов.
    /// </summary>
    public static class Helper
    {
        private static double nanomultiplyer = 1000000000d;
        /// <summary>
        /// Преобразует Quotation из T-Invest API в double с учетом nano-части.
        /// </summary>
        public static double FromQuotation(Tinkoff.InvestApi.V1.Quotation value, double DefaultValue = 0)
        {
            return value == null ? DefaultValue : value.Units + value.Nano / nanomultiplyer;
        }
        public static Tinkoff.InvestApi.V1.Quotation ToQuotation(double value)
        {
            var units = Convert.ToInt64(Double.Truncate(value));
            var nano = Convert.ToInt32((value - units) * nanomultiplyer);
            return new Quotation
            {
                Units = units,
                Nano = nano
            };
        }
        public static double FromMoneyValue(Tinkoff.InvestApi.V1.MoneyValue value)
        {
            return value.Units + value.Nano / 1000000000d;
        }
        public static double MarginShort(Future f, Risk risk = Risk.Low)
        {
            return Helper.FromQuotation(risk == Risk.Low ? f.Dshort : f.DshortClient);
        }
        public static double MarginShort(Share s, Risk risk = Risk.Low)
        {
            return Helper.FromQuotation(risk == Risk.Low ? s.Dshort : s.DshortClient);
        }
        public static double Margin(Future f, bool isLong, Risk risk = Risk.Low)
        {
            return isLong ? MarginLong(f, risk) : MarginShort(f, risk);
        }
        public static double MarginLong(Future f, Risk risk = Risk.Low)
        {
            return Helper.FromQuotation(risk == Risk.Low ? f.Dlong : f.DlongClient);
        }
        public static double MarginLong(Share s, Risk risk = Risk.Low)
        {
            return Helper.FromQuotation(risk == Risk.Low ? s.Dlong : s.DlongClient);
        }
        public static double Margin(Share s, bool isLong, Risk risk = Risk.Low)
        {
            return isLong ? MarginLong(s, risk) : MarginShort(s, risk);
        }
        public static Tinkoff.InvestApi.V1.CandleInterval HistoricalTimeFrameToCandleInterval(HistoricalTimeFrame tf)
        {
            switch (tf)
            {
                case HistoricalTimeFrame.M1: return Tinkoff.InvestApi.V1.CandleInterval._1Min;
                case HistoricalTimeFrame.M2: return Tinkoff.InvestApi.V1.CandleInterval._2Min;
                case HistoricalTimeFrame.M3: return Tinkoff.InvestApi.V1.CandleInterval._3Min;
                case HistoricalTimeFrame.M5: return Tinkoff.InvestApi.V1.CandleInterval._5Min;
                case HistoricalTimeFrame.M10: return Tinkoff.InvestApi.V1.CandleInterval._10Min;
                case HistoricalTimeFrame.M15: return Tinkoff.InvestApi.V1.CandleInterval._15Min;
                case HistoricalTimeFrame.M30: return Tinkoff.InvestApi.V1.CandleInterval._30Min;
                case HistoricalTimeFrame.H1: return Tinkoff.InvestApi.V1.CandleInterval.Hour;
                case HistoricalTimeFrame.H2: return Tinkoff.InvestApi.V1.CandleInterval._2Hour;
                case HistoricalTimeFrame.H4: return Tinkoff.InvestApi.V1.CandleInterval._4Hour;
                case HistoricalTimeFrame.D1: return Tinkoff.InvestApi.V1.CandleInterval.Day;
                case HistoricalTimeFrame.W1: return Tinkoff.InvestApi.V1.CandleInterval.Week;
                case HistoricalTimeFrame.MN: return Tinkoff.InvestApi.V1.CandleInterval.Month;
                default: return Tinkoff.InvestApi.V1.CandleInterval.Hour;
            }
        }
        public static HistoricalTimeFrame CandleIntervalToHistoricalTimeFrame(Tinkoff.InvestApi.V1.CandleInterval ci)
        {
            switch (ci)
            {
                case Tinkoff.InvestApi.V1.CandleInterval._1Min: return HistoricalTimeFrame.M1;
                case Tinkoff.InvestApi.V1.CandleInterval._2Min: return HistoricalTimeFrame.M2;
                case Tinkoff.InvestApi.V1.CandleInterval._3Min: return HistoricalTimeFrame.M3;
                case Tinkoff.InvestApi.V1.CandleInterval._5Min: return HistoricalTimeFrame.M5;
                case Tinkoff.InvestApi.V1.CandleInterval._10Min: return HistoricalTimeFrame.M10;
                case Tinkoff.InvestApi.V1.CandleInterval._15Min: return HistoricalTimeFrame.M15;
                case Tinkoff.InvestApi.V1.CandleInterval._30Min: return HistoricalTimeFrame.M30;
                case Tinkoff.InvestApi.V1.CandleInterval.Hour: return HistoricalTimeFrame.H1;
                case Tinkoff.InvestApi.V1.CandleInterval._2Hour: return HistoricalTimeFrame.H2;
                case Tinkoff.InvestApi.V1.CandleInterval._4Hour: return HistoricalTimeFrame.H4;
                case Tinkoff.InvestApi.V1.CandleInterval.Day: return HistoricalTimeFrame.D1;
                case Tinkoff.InvestApi.V1.CandleInterval.Week: return HistoricalTimeFrame.W1;
                case Tinkoff.InvestApi.V1.CandleInterval.Month: return HistoricalTimeFrame.MN;
                default: return HistoricalTimeFrame.H1;
            }
        }
        public static TimeSpan CandleIntervalTimeSpan(CandleInterval ci)
        {
            switch (ci)
            {
                case Tinkoff.InvestApi.V1.CandleInterval._1Min: return new TimeSpan(0, 1, 0);
                case Tinkoff.InvestApi.V1.CandleInterval._2Min: return new TimeSpan(0, 2, 0);
                case Tinkoff.InvestApi.V1.CandleInterval._3Min: return new TimeSpan(0, 3, 0);
                case Tinkoff.InvestApi.V1.CandleInterval._5Min: return new TimeSpan(0, 5, 0);
                case Tinkoff.InvestApi.V1.CandleInterval._10Min: return new TimeSpan(0, 10, 0);
                case Tinkoff.InvestApi.V1.CandleInterval._15Min: return new TimeSpan(0, 15, 0);
                case Tinkoff.InvestApi.V1.CandleInterval._30Min: return new TimeSpan(0, 30, 0);
                case Tinkoff.InvestApi.V1.CandleInterval.Hour: return new TimeSpan(1, 0, 0);
                case Tinkoff.InvestApi.V1.CandleInterval._2Hour: return new TimeSpan(2, 0, 0);
                case Tinkoff.InvestApi.V1.CandleInterval._4Hour: return new TimeSpan(4, 0, 0);
                case Tinkoff.InvestApi.V1.CandleInterval.Day: return new TimeSpan(1, 0, 0, 0);
                case Tinkoff.InvestApi.V1.CandleInterval.Week: return new TimeSpan(7, 0, 0, 0);
                case Tinkoff.InvestApi.V1.CandleInterval.Month: return new TimeSpan(28, 0, 0, 0);
                default: return new TimeSpan(0, 1, 0, 0);
            }
        }
        public static HistoricalCandle ToHistoricalCandle(this HistoricCandle candle)
        {
            return new HistoricalCandle
            {
                Open = Helper.FromQuotation(candle.Open),
                Close = Helper.FromQuotation(candle.Close),
                Low = Helper.FromQuotation(candle.Low),
                High = Helper.FromQuotation(candle.High),
                Volume = Helper.FromQuotation(candle.Volume),
                Time = candle.Time.ToDateTime().ToUniversalTime(),
            };
        }
        public static TimeSpan HistoricalTimeFrameTimeSpan(HistoricalTimeFrame tf)
        {
            switch (tf)
            {
                case HistoricalTimeFrame.M1: return new TimeSpan(0, 0, 1, 0);
                case HistoricalTimeFrame.M2: return new TimeSpan(0, 0, 2, 0);
                case HistoricalTimeFrame.M3: return new TimeSpan(0, 0, 3, 0);
                case HistoricalTimeFrame.M5: return new TimeSpan(0, 0, 5, 0);
                case HistoricalTimeFrame.M10: return new TimeSpan(0, 0, 10, 0);
                case HistoricalTimeFrame.M15: return new TimeSpan(0, 0, 15, 0);
                case HistoricalTimeFrame.M30: return new TimeSpan(0, 0, 30, 0);
                case HistoricalTimeFrame.H1: return new TimeSpan(0, 1, 0, 0);
                case HistoricalTimeFrame.H2: return new TimeSpan(0, 2, 0, 0);
                case HistoricalTimeFrame.H4: return new TimeSpan(0, 4, 0, 0);
                case HistoricalTimeFrame.D1: return new TimeSpan(1, 0, 0, 0);
                case HistoricalTimeFrame.W1: return new TimeSpan(7, 0, 0, 0);
                case HistoricalTimeFrame.MN: return new TimeSpan(28, 0, 0, 0);
                default: return new TimeSpan(0, 1, 0, 0);
            }
        }
        public static string ToShortGuid(this Guid newGuid)
        {
            string modifiedBase64 = Convert.ToBase64String(newGuid.ToByteArray())
                .Replace('+', '-').Replace('/', '_') // avoid invalid URL characters
                .Substring(0, 22);
            return modifiedBase64;
        }

        public static Guid ParseShortGuid(string shortGuid)
        {
            string base64 = shortGuid.Replace('-', '+').Replace('_', '/') + "==";
            Byte[] bytes = Convert.FromBase64String(base64);
            return new Guid(bytes);
        }
        /// <summary>
        /// Возвращает пары свечей с одинаковым временем из двух отсортированных рядов.
        /// Используется при сравнении базового актива и производного инструмента.
        /// </summary>
        public static IEnumerable<Tuple<HistoricalCandle,HistoricalCandle>> GetHistoricalCandlesPairs(IEnumerable<HistoricalCandle> source1, IEnumerable<HistoricalCandle> source2)
        {
            int i1 = 0; int i2 = 0;
            var cnt1 = source1.Count();
            var cnt2 = source2.Count();
            do
            {
                if (source1.ElementAt(i1).Time == source2.ElementAt(i2).Time)
                {
                    yield return new Tuple<HistoricalCandle, HistoricalCandle>(source1.ElementAt(i1), source2.ElementAt(i2));
                    i1++;
                    i2++;
                }
                else if (source1.ElementAt(i1).Time < source2.ElementAt(i2).Time)
                    i1++;
                else
                    i2++;
            }
            while (i1 < cnt1 && i2 < cnt2);
        }
    }
    /// <summary>
    /// Профиль риска для выбора биржевого или клиентского гарантийного обеспечения.
    /// </summary>
    public enum Risk
    {
        Low = 0,
        High = 1
    }
    /// <summary>
    /// Базовые настройки торговых роботов: ключ аккаунта, тикер, таймфрейм,
    /// автозапуск и общий механизм сохранения/загрузки JSON-настроек.
    /// </summary>
    public abstract class RobotBaseSettings:INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void NotifyPropertyChanged([CallerMemberName] String propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        private string apiKey = "";
        private string displayName = "";
        private string accountApiKey = "";
        [Category("Базовые атрибуты")]
        [PropertyOrder(2)]
        [DisplayName("Отображаемое имя")]
        public string DisplayName
        {
            get => displayName;
            set
            {
                if (displayName != value)
                {
                    displayName = value;
                    NotifyPropertyChanged();
                }
            }
        }
        [Category("Базовые атрибуты")]
        [PropertyOrder(1)]
        [DisplayName("Ключ доступа")]
        [Newtonsoft.Json.JsonIgnore]
        public string AccountApiKey
        {
            get => apiKey;
            set
            {
                if (apiKey != value)
                {
                    apiKey = value;
                    try
                    {
                        // При выборе имени секрета сразу подтягиваем имя первого брокерского счета для UI.
                        var client = InvestApiClientFactory.Create(WindowsCredentialManager.ReadSecret(apiKey)??"key not found");
                        var account = client.Users.GetAccounts().Accounts.First();
                        DisplayName = account.Name;
                    }
                    catch { }
                    NotifyPropertyChanged();
                }
            }
        }
        [Browsable(false)]
        public string ApiKey
        {
            get => apiKey;
            set
            {
                apiKey = value;
                NotifyPropertyChanged();
            }
        }
        [Category("Базовые атрибуты")]
        [PropertyOrder(3)]
        public string Ticker { get; set; }
        [Category("Базовые атрибуты")]
        [PropertyOrder(4)]
        public HistoricalTimeFrame TimeFrame { get; set; }
        [Category("Базовые атрибуты")]
        [PropertyOrder(5)]
        public bool AutoStart { get; set; } = false;
        public string Serialize()
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(this, Formatting.Indented);
        }
        public static T Deserialize<T>(string text) where T :RobotBaseSettings
        {
            return Newtonsoft.Json.JsonConvert.DeserializeObject<T>(text);
        }
        public abstract void SaveSettings();
        public override string ToString()
        {
            var sb = new StringBuilder();
            Type type = this.GetType();

            // Получаем все публичные свойства
            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(u => !new string[] { nameof(ApiKey), nameof(AutoStart), nameof(AccountApiKey), nameof(DisplayName) }.Contains(u.Name))) 
            {
                object value = prop.GetValue(this, null);
                sb.Append($" {prop.Name}: {value ?? "null"}");
            }
            return sb.ToString();
        }
    }
    /// <summary>
    /// Набор параметров RSI/MA-стратегии RobotGrokAdvice.
    /// </summary>
    public class GrokAdviceSettings:RobotBaseSettings
    {
        private static GrokAdviceSettings _default;
        public static GrokAdviceSettings Default
        {
            get
            {
                if (_default == null)
                {
                    try
                    {
                        if (File.Exists(SettingsFileName))
                            _default = Deserialize<GrokAdviceSettings>(File.ReadAllText(SettingsFileName, Encoding.UTF8)) as GrokAdviceSettings;
                    }
                    catch
                    {
                    }
                    if (_default == null)
                        _default = new GrokAdviceSettings();
                }
                return _default;
            }
            set
            {
                _default = value;
            }
        }
        private static string SettingsFileName = "RobotGrokAdvice.settings";
        public override void SaveSettings()
        {
            File.WriteAllText(SettingsFileName, Serialize(), Encoding.UTF8);
        }
        [Category("Общие")]
        [PropertyOrder(1)]
        public double DepoUsage { get; set; }
        [Category("Общие")]
        [PropertyOrder(2)]
        public double MaxRiskMultiplicator { get; set; }
        [Category("Общие")]
        [PropertyOrder(3)]
        public TimeSpan FutureSwitchPeriod { get; set; }
        [Category("Параметры")]
        [PropertyOrder(1)]
        public uint rsiLength { get; set; }
        [Category("Параметры")]
        [PropertyOrder(2)]
        public uint rsiCandlesToCheck { get; set; }
        [Category("Параметры")]
        [PropertyOrder(3)]
        public double rsiOverbought { get; set; }
        [Category("Параметры")]
        [PropertyOrder(4)]
        public uint maLenght { get; set; }
        [Category("Параметры")]
        [PropertyOrder(5)]
        public bool useTrendFilter { get; set; }
    }
    /// <summary>
    /// Набор параметров стратегии RobotGrokAdvice1 на пересечении средних,
    /// ATR-фильтре волатильности и дистанциях stop-loss/take-profit.
    /// </summary>
    public class GrokAdvice1Settings:RobotBaseSettings
    {
        private static GrokAdvice1Settings _default;
        public static GrokAdvice1Settings Default
        {
            get
            {
                if (_default == null)
                {
                    try
                    {
                        if (File.Exists(SettingsFileName))
                            _default = Deserialize<GrokAdvice1Settings>(File.ReadAllText(SettingsFileName, Encoding.UTF8));
                    }
                    catch
                    {
                    }
                    if (_default == null)
                        _default = new GrokAdvice1Settings();
                }
                return _default;
            }
            set
            {
                _default = value;
            }
        }
        private static string SettingsFileName = "RobotGrokAdvice1.settings";
        public override void SaveSettings()
        {
            File.WriteAllText(SettingsFileName, Serialize(), Encoding.UTF8);
        }
        [Category("Общие")]
        [PropertyOrder(1)]
        public double DepoUsage { get; set; }
        [Category("Общие")]
        [PropertyOrder(2)]
        public double MaxRiskMultiplicator { get; set; }
        [Category("Общие")]
        [PropertyOrder(3)]
        public TimeSpan FutureSwitchPeriod { get; set; }
        [Category("Параметры")]
        [PropertyOrder(1)]
        public uint lengthFast { get; set; }
        [Category("Параметры")]
        [PropertyOrder(2)]
        public uint lengthSlow { get; set; }
        [Category("Параметры")]
        [PropertyOrder(3)]
        public uint atrLength { get; set; }
        [Category("Параметры")]
        [PropertyOrder(4)]
        public double atrMultiplierSL { get; set; }
        [Category("Параметры")]
        [PropertyOrder(5)]
        public double atrMultiplierTP { get; set; }
        [Category("Параметры")]
        [PropertyOrder(6)]
        public uint loopback { get; set; }
        [Category("Параметры")]
        [PropertyOrder(7)]
        public double riskPercent { get; set; }
        [Category("Параметры")]
        [PropertyOrder(8)]
        public bool useBuyVolatileFilter { get; set; }
        [Category("Параметры")]
        [PropertyOrder(9)]
        public bool useSellVolatileFilter { get; set; }
    }
    /// <summary>
    /// Набор параметров стратегии RobotMovingAverage.
    /// </summary>
    public class MovingAverageSettings:RobotBaseSettings
    {
        private static MovingAverageSettings _default;
        public static MovingAverageSettings Default
        {
            get
            {
                if (_default == null)
                {
                    try
                    {
                        if (File.Exists(SettingsFileName))
                            _default = Deserialize<MovingAverageSettings>(File.ReadAllText(SettingsFileName, Encoding.UTF8));
                    }
                    catch
                    {
                    }
                    if (_default == null)
                        _default = new MovingAverageSettings();
                }
                return _default;
            }
            set
            {
                _default = value;
            }
        }
        private static string SettingsFileName = "RobotMovingAverage.settings";
        public override void SaveSettings()
        {
            File.WriteAllText(SettingsFileName, Serialize(), Encoding.UTF8);
        }
        [Category("Общие")]
        [PropertyOrder(1)]
        public double DepoUsage { get; set; }
        [Category("Общие")]
        [PropertyOrder(2)]
        public double MaxRiskMultiplicator { get; set; }
        [Category("Общие")]
        [PropertyOrder(3)]
        public TimeSpan FutureSwitchPeriod { get; set; }
        [Category("Параметры")]
        [PropertyOrder(1)]
        public uint MALen { get; set; }
        [Category("Параметры")]
        [PropertyOrder(2)]
        public uint MAStep { get; set; }
        [Category("Параметры")]
        [PropertyOrder(3)]
        public uint MAStart { get; set; }
    }
    /// <summary>
    /// Общий контракт торгового робота для WPF-обвязки RoboTrader.
    /// Наследники реализуют запуск, остановку и торговую логику, а базовый класс
    /// хранит общие поля UI и историю изменения депозита.
    /// </summary>
    public abstract class RobotBase : INotifyPropertyChanged
    {
        public abstract Logger Logger { get; }

        public event PropertyChangedEventHandler PropertyChanged;
        public void NotifyPropertyChanged([CallerMemberName] String propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        public abstract void Start();
        public abstract void Stop();
        public abstract RobotBaseSettings Settings { get; set; }
        public abstract bool IsRunning { get; }
        public MarginAttributes Margin { get; set; } = new MarginAttributes();
        public List<string> Positions { get; set; } = new List<string>();
        public abstract string Status { get; }
        public abstract string TitleText {get;}
        private HistoricalCandle CurrentDepoCandle { get; set; }
        private string DepoHistoryFileName { get=> $"{this.GetType().Name}_{Settings.Ticker}_{Settings.TimeFrame}.depo"; }
        /// <summary>
        /// Добавляет значение депозита в минутную OHLC-свечу истории.
        /// Такой формат позволяет потом строить график капитала теми же средствами, что и цену.
        /// </summary>
        public void AddDepoValue (double depovalue, DateTime time)
        {
            if (depovalue == 0)
                return;
            var utime = time.ToUniversalTime();
            utime = new DateTime(utime.Year, utime.Month, utime.Day, utime.Hour, utime.Minute, 0);
            if (CurrentDepoCandle != null)
            {
                if (utime == CurrentDepoCandle.Time)
                {
                    CurrentDepoCandle.Close = depovalue;
                    if (depovalue > CurrentDepoCandle.High)
                        CurrentDepoCandle.High = depovalue;
                    if (depovalue < CurrentDepoCandle.Low)
                        CurrentDepoCandle.Low = depovalue;
                }
                else
                {
                    using (var fs = new FileStream(
                        DepoHistoryFileName,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.Read))
                    using (var writer = new StreamWriter(fs))
                    {
                        writer.WriteLine($"{Newtonsoft.Json.JsonConvert.SerializeObject(CurrentDepoCandle)},");
                    }
                    CurrentDepoCandle = null;
                }
            }
            if (CurrentDepoCandle == null)
                CurrentDepoCandle = new HistoricalCandle { Open = depovalue, Close = depovalue, Low = depovalue, High = depovalue, Time = utime };
            NotifyPropertyChanged(nameof(CurrentDepoCandle));
        }
        public List<HistoricalCandle> GetDepoHistory()
        {
            List<HistoricalCandle> res = null;
            try
            {
                var text = $"[{File.ReadAllText(DepoHistoryFileName, Encoding.UTF8)}]";
                res = JsonConvert.DeserializeObject<List<HistoricalCandle>>(text).Where(u => u != null).ToList();
                if (CurrentDepoCandle != null && res.Any() && DateTime.SpecifyKind(res.Last().Time, DateTimeKind.Utc) != CurrentDepoCandle.Time)
                    res.Append(CurrentDepoCandle);
            }
            catch (Exception ex)
            { 

            }
            if (res == null)
                res = new List<HistoricalCandle>();
            res = res.Where(u => u.Open > 0 && u.Close > 0 && u.Low > 0).ToList();
            return res;
        }
    }
    /// <summary>
    /// Обертка над Windows Credential Manager для хранения API-ключей вне файлов настроек.
    /// </summary>
    public static class WindowsCredentialManager
    {
        private const int CredTypeGeneric = 1;
        private const int PersistLocalMachine = 2;

        public class WindowsCredentialInfo
        {
            public string TargetName { get; set; } = "";
            public string UserName { get; set; } = "";
            public DateTime LastWritten { get; set; }
        }

        /// <summary>
        /// Возвращает generic credentials, у которых UserName соответствует SQL-like фильтру:
        /// % означает любую последовательность символов, _ - один символ.
        /// Сравнение выполняется без учета регистра.
        /// </summary>
        public static List<WindowsCredentialInfo> ListCredentialsByUserName(string userNameLikeFilter)
        {
            var filter = string.IsNullOrWhiteSpace(userNameLikeFilter) ? "%" : userNameLikeFilter;
            var result = new List<WindowsCredentialInfo>();

            if (!CredEnumerate(null, 0, out var count, out var credentialsPtr))
                return result;

            try
            {
                for (var i = 0; i < count; i++)
                {
                    var credentialPtr = Marshal.ReadIntPtr(credentialsPtr, i * IntPtr.Size);
                    var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
                    if (credential.Type != CredTypeGeneric)
                        continue;

                    var userName = credential.UserName ?? "";
                    if (!Like(userName, filter))
                        continue;

                    result.Add(new WindowsCredentialInfo
                    {
                        TargetName = credential.TargetName,
                        UserName = userName,
                        LastWritten = FileTimeToDateTime(credential.LastWritten)
                    });
                }
            }
            finally
            {
                CredFree(credentialsPtr);
            }

            return result.OrderBy(u => u.UserName).ThenBy(u => u.TargetName).ToList();
        }

        public static string? ReadSecret(string targetName)
        {
            if (string.IsNullOrWhiteSpace(targetName))
                return null;

            if (!CredRead(targetName, CredTypeGeneric, 0, out var credentialPtr))
                return null;

            try
            {
                var credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize <= 0)
                    return null;

                var blob = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
                return Encoding.Unicode.GetString(blob).TrimEnd('\0');
            }
            finally
            {
                CredFree(credentialPtr);
            }
        }

        public static bool WriteSecret(string targetName, string secret)
        {
            return WriteSecret(targetName, targetName, secret);
        }

        public static bool WriteSecret(string targetName, string userName, string secret)
        {
            if (string.IsNullOrWhiteSpace(targetName))
                return false;

            var secretBytes = Encoding.Unicode.GetBytes(secret ?? string.Empty);
            var credential = new CREDENTIAL
            {
                Type = CredTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = (uint)secretBytes.Length,
                Persist = PersistLocalMachine,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                Comment = null,
                TargetAlias = null,
                UserName = string.IsNullOrWhiteSpace(userName) ? targetName : userName
            };

            var blobHandle = GCHandle.Alloc(secretBytes, GCHandleType.Pinned);
            try
            {
                credential.CredentialBlob = blobHandle.AddrOfPinnedObject();
                return CredWrite(ref credential, 0);
            }
            finally
            {
                blobHandle.Free();
            }
        }

        /// <summary>
        /// Удаляет generic credential по TargetName из Windows Credential Manager.
        /// </summary>
        public static bool DeleteSecret(string targetName)
        {
            if (string.IsNullOrWhiteSpace(targetName))
                return false;

            return CredDelete(targetName, CredTypeGeneric, 0);
        }

        [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite([In] ref CREDENTIAL userCredential, [In] uint flags);

        [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, int type, int flags);

        [DllImport("Advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredEnumerate(string? filter, uint flags, out uint count, out IntPtr credentials);

        [DllImport("Advapi32.dll", SetLastError = true)]
        private static extern void CredFree([In] IntPtr cred);

        private static bool Like(string value, string pattern)
        {
            var regex = "^" + Regex.Escape(pattern)
                .Replace("%", ".*")
                .Replace("_", ".") + "$";
            return Regex.IsMatch(value ?? "", regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static DateTime FileTimeToDateTime(FILETIME fileTime)
        {
            var high = ((long)fileTime.dwHighDateTime) << 32;
            var low = (uint)fileTime.dwLowDateTime;
            var ticks = high + low;
            return ticks <= 0 ? DateTime.MinValue : DateTime.FromFileTimeUtc(ticks).ToLocalTime();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public int Type;
            public string TargetName;
            public string? Comment;
            public FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }
    }

}
