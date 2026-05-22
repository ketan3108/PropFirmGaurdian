using System;
using System.ComponentModel;
using System.Windows.Media;
using PropFirmGuardian.Models;

namespace PropFirmGuardian.UI.ViewModels
{
    public class AccountViewModel : INotifyPropertyChanged
    {
        private string _accountName;
        private string _connectionStatus;
        private double _realizedPnL;
        private double _unrealizedPnL;
        private double _peakPnL;
        private double _drawdownFromPeak;
        private double _dailyLimitRemaining;
        private AccountState _status;
        private TimeSpan? _lockoutTimeRemaining;
        private bool _isShadowMode;
        private bool _isHardLock;
        private int _tradesToday;
        private int _dailyTradeLimit;
        private double _sessionQualityScore;
        private string _sessionQualityMessage;
        private string _activeGuards;
        private double _passProbability;
        private double _requiredDailyAverage;
        private string _passProbabilityTooltip;

        public event PropertyChangedEventHandler PropertyChanged;

        public string AccountName
        {
            get { return _accountName; }
            set { SetProperty(ref _accountName, value, "AccountName"); }
        }

        public string ConnectionStatus
        {
            get { return _connectionStatus; }
            set { SetProperty(ref _connectionStatus, value, "ConnectionStatus"); }
        }

        public double RealizedPnL
        {
            get { return _realizedPnL; }
            set { SetProperty(ref _realizedPnL, value, "RealizedPnL"); }
        }

        public double UnrealizedPnL
        {
            get { return _unrealizedPnL; }
            set { SetProperty(ref _unrealizedPnL, value, "UnrealizedPnL"); }
        }

        public double PeakPnL
        {
            get { return _peakPnL; }
            set { SetProperty(ref _peakPnL, value, "PeakPnL"); }
        }

        public double DrawdownFromPeak
        {
            get { return _drawdownFromPeak; }
            set { SetProperty(ref _drawdownFromPeak, value, "DrawdownFromPeak"); }
        }

        public double DailyLimitRemaining
        {
            get { return _dailyLimitRemaining; }
            set { SetProperty(ref _dailyLimitRemaining, value, "DailyLimitRemaining"); }
        }

        public AccountState Status
        {
            get { return _status; }
            set
            {
                if (SetProperty(ref _status, value, "Status"))
                {
                    OnPropertyChanged("StatusColor");
                    OnPropertyChanged("StatusDisplay");
                }
            }
        }

        public TimeSpan? LockoutTimeRemaining
        {
            get { return _lockoutTimeRemaining; }
            set { SetProperty(ref _lockoutTimeRemaining, value, "LockoutTimeRemaining"); }
        }

        public bool IsShadowMode
        {
            get { return _isShadowMode; }
            set { SetProperty(ref _isShadowMode, value, "IsShadowMode"); }
        }

        public bool IsHardLock
        {
            get { return _isHardLock; }
            set
            {
                if (SetProperty(ref _isHardLock, value, "IsHardLock"))
                    OnPropertyChanged("StatusDisplay");
            }
        }

        public string StatusDisplay
        {
            get
            {
                if (IsHardLock)
                    return "HARD LOCK";

                if (Status == AccountState.Active || Status == AccountState.Warning)
                {
                    if (TradeCapSeverity >= 3)
                        return "Locked";
                    if (TradeCapSeverity >= 2)
                        return "Limit";
                    if (TradeCapSeverity >= 1)
                        return "Warning";
                }

                return Status.ToString();
            }
        }

        public int TradesToday
        {
            get { return _tradesToday; }
            set
            {
                if (SetProperty(ref _tradesToday, value, "TradesToday"))
                {
                    OnPropertyChanged("TradesTodayDisplay");
                    OnPropertyChanged("TradesTodayPercent");
                    OnPropertyChanged("TradeCapSeverity");
                    OnPropertyChanged("StatusColor");
                    OnPropertyChanged("StatusDisplay");
                }
            }
        }

        public int DailyTradeLimit
        {
            get { return _dailyTradeLimit; }
            set
            {
                if (SetProperty(ref _dailyTradeLimit, value, "DailyTradeLimit"))
                {
                    OnPropertyChanged("TradesTodayDisplay");
                    OnPropertyChanged("TradesTodayPercent");
                    OnPropertyChanged("TradeCapSeverity");
                    OnPropertyChanged("StatusColor");
                    OnPropertyChanged("StatusDisplay");
                }
            }
        }

        public string TradesTodayDisplay
        {
            get { return DailyTradeLimit > 0 ? string.Format("{0}/{1}", TradesToday, DailyTradeLimit) : string.Format("{0}/-", TradesToday); }
        }

        public double TradesTodayPercent
        {
            get { return DailyTradeLimit > 0 ? Math.Min(100.0, Math.Max(0.0, TradesToday * 100.0 / DailyTradeLimit)) : 0.0; }
        }

        public int TradeCapSeverity
        {
            get
            {
                if (DailyTradeLimit <= 0)
                    return 0;

                if (TradesToday >= DailyTradeLimit)
                    return 3;

                double percent = TradesTodayPercent;
                if (percent >= 80.0)
                    return 2;

                if (percent >= 60.0)
                    return 1;

                return 0;
            }
        }

        public double SessionQualityScore
        {
            get { return _sessionQualityScore; }
            set { SetProperty(ref _sessionQualityScore, value, "SessionQualityScore"); }
        }

        public string SessionQualityMessage
        {
            get { return _sessionQualityMessage; }
            set { SetProperty(ref _sessionQualityMessage, value, "SessionQualityMessage"); }
        }

        public string ActiveGuards
        {
            get { return _activeGuards; }
            set { SetProperty(ref _activeGuards, value, "ActiveGuards"); }
        }

        public double PassProbability
        {
            get { return _passProbability; }
            set { SetProperty(ref _passProbability, value, "PassProbability"); }
        }

        public double RequiredDailyAverage
        {
            get { return _requiredDailyAverage; }
            set { SetProperty(ref _requiredDailyAverage, value, "RequiredDailyAverage"); }
        }

        public string PassProbabilityTooltip
        {
            get { return _passProbabilityTooltip; }
            set { SetProperty(ref _passProbabilityTooltip, value, "PassProbabilityTooltip"); }
        }

        public Brush StatusColor
        {
            get
            {
                switch (Status)
                {
                    case AccountState.Active:
                        if (TradeCapSeverity >= 3)
                            return new SolidColorBrush(Color.FromRgb(153, 27, 27));
                        if (TradeCapSeverity >= 2)
                            return Brushes.Red;
                        if (TradeCapSeverity >= 1)
                            return Brushes.Gold;
                        return Brushes.LimeGreen;
                    case AccountState.Warning:
                        return TradeCapSeverity >= 2 ? Brushes.Red : Brushes.Gold;
                    case AccountState.Flattening:
                        return Brushes.Red;
                    case AccountState.Locked:
                        return Brushes.Red;
                    case AccountState.NewsLocked:
                        return Brushes.Orange;
                    case AccountState.Disconnected:
                        return Brushes.Gray;
                    case AccountState.HardLocked:
                        return new SolidColorBrush(Color.FromRgb(153, 27, 27));
                    default:
                        return Brushes.White;
                }
            }
        }

        public void RefreshStatusColor()
        {
            OnPropertyChanged("StatusColor");
        }

        private bool SetProperty<T>(ref T storage, T value, string propertyName)
        {
            if (Equals(storage, value))
                return false;

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
