using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using PropFirmGuardian.Core;
using PropFirmGuardian.Intelligence;
using PropFirmGuardian.Models;
using PropFirmGuardian.News;
using PropFirmGuardian.Services;
using PropFirmGuardian.UI;
using PropFirmGuardian.UI.ViewModels;
using PropFirmGuardian.Utils;

namespace NinjaTrader.NinjaScript.AddOns
{
    [AddOn("PropFirmGuardian", Description = "Production-grade risk management", Author = "PropFirmGuardian", Version = "1.0")]
    public class PropFirmGuardianAddOn : AddOnBase
    {
        private NTWindow _dashboardWindow;
        private GuardianDashboard _dashboardControl;
        private NTMenuItem _menuItem;
        private NinjaTrader.Gui.ControlCenter _controlCenter;

        private readonly HashSet<Account> _subscribedAccounts = new HashSet<Account>();
        private readonly HashSet<Connection> _subscribedConnections = new HashSet<Connection>();
        private readonly List<AccountConfig> _accountConfigs = new List<AccountConfig>();
        private readonly HashSet<string> _historyBackfilledAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private AccountMonitor _accountMonitor;
        private RiskEngine _riskEngine;
        private FlattenProtocol _flattenProtocol;
        private OrderInterceptor _orderInterceptor;
        private TiltDetector _tiltDetector;
        private SessionTracker _sessionTracker;
        private NewsEngine _newsEngine;
        private PersistenceService _persistence;
        private CrashRecovery _crashRecovery;
        private AudioAlertService _audio;
        private LicenseService _license;
        private DailyTradeLimiter _dailyTradeLimiter;
        private ConsistencyTracker _consistencyTracker;
        private TimeBasedGuard _timeBasedGuard;
        private PassProbabilityEngine _passProbabilityEngine;

        private bool _isConnectionStatusSubscribed;
        private bool _isShuttingDown;
        private Timer _discoveryTimer;
        private System.Threading.Timer _enforcementTimer;
        private int _isDiscoveringAccounts;
        private int _isEnforcingLocks;
        private int _isFlattenDispatchScheduled;
        private readonly AccountMonitor.AccountMonitorState[] _enforcementStateBuffer = new AccountMonitor.AccountMonitorState[256];
        private readonly string[] _flattenRequestAccountNames = new string[256];
        private readonly object _flattenRequestLock = new object();
        private readonly Action _processFlattenRequestsAction;
        private readonly List<string> _pendingRecoveryAlerts = new List<string>();
        private readonly ConcurrentDictionary<string, DateTime> _lastCancelAllUtcByAccount =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, PositionLifecycleState> _positionLifecycleStates =
            new ConcurrentDictionary<string, PositionLifecycleState>(StringComparer.OrdinalIgnoreCase);
        private int _flattenRequestCount;

        public PropFirmGuardianAddOn()
        {
            _processFlattenRequestsAction = ProcessFlattenRequestsOnDispatcher;
        }

        protected override void OnStateChange()
        {
            OnStateChange(State);
        }

        private void OnStateChange(State state)
        {
            if (state == State.SetDefaults)
            {
                Name = "PropFirmGuardian";
                Description = "Production-grade risk management";
            }
            else if (state == State.DataLoaded)
            {
                _isShuttingDown = false;
                InitializeEngine();
            }
            else if (state == State.Terminated)
            {
                Cleanup();
            }
        }

        protected override void OnWindowCreated(Window window)
        {
            NinjaTrader.Gui.ControlCenter controlCenter = window as NinjaTrader.Gui.ControlCenter;
            if (controlCenter == null || _menuItem != null)
                return;

            _controlCenter = controlCenter;
            _menuItem = new NTMenuItem
            {
                Header = "Prop Firm Guardian"
            };
            _menuItem.Click += OnMenuItemClick;

            // NT8 AddOnBase in this installed build exposes menu insertion through the
            // Control Center window lifecycle, so registration happens here after
            // DataLoaded has initialized the engine.
            _controlCenter.MainMenu.Add(_menuItem);
        }

        protected override void OnWindowDestroyed(Window window)
        {
            if (window == _controlCenter)
                RemoveMenuItem();
        }

        private void InitializeEngine()
        {
            try
            {
                _license = new LicenseService();
                if (!_license.ValidateLicense() && !_license.CheckGracePeriod())
                {
                    _license.StartValidationAsync();
                    Debug.WriteLine("[PropFirmGuardian] License not validated; continuing engine startup under V1 non-blocking grace mode.");
                }

                _persistence = new PersistenceService();
                _accountConfigs.Clear();
                _accountConfigs.AddRange(_persistence.LoadConfig());

                EnsureDefaultConfigsForDiscoveredAccounts();

                _accountMonitor = new AccountMonitor();
                _persistence.SetAutoSaveStateProvider(() => _accountMonitor.ExportSnapshots());

                _riskEngine = new RiskEngine(_accountMonitor);
                _accountMonitor.SetRiskEngine(_riskEngine);
                _flattenProtocol = new FlattenProtocol(_accountMonitor);
                _orderInterceptor = new OrderInterceptor(_accountMonitor);
                _tiltDetector = new TiltDetector(_accountMonitor);
                _sessionTracker = new SessionTracker(_accountMonitor, _tiltDetector);
                _dailyTradeLimiter = new DailyTradeLimiter(_accountMonitor);
                _consistencyTracker = new ConsistencyTracker();
                _timeBasedGuard = new TimeBasedGuard(_accountMonitor);
                _passProbabilityEngine = new PassProbabilityEngine(_accountMonitor);
                _audio = new AudioAlertService();
                _crashRecovery = new CrashRecovery(_accountMonitor, _persistence);
                _crashRecovery.OnRecoveryAlert += OnRecoveryAlert;

                WireEngineEvents();
                SubscribeConnections();
                SubscribeAccounts();

                _crashRecovery.PerformRecovery();

                try
                {
                    _newsEngine = new NewsEngine(_accountMonitor);
                    _newsEngine.LoadCalendar();
                    WireNewsEvents();
                }
                catch (Exception newsException)
                {
                    Debug.WriteLine(string.Format("[PropFirmGuardian] News engine failed to start; continuing without news protection: {0}", newsException.Message));
                }

                _persistence.SaveConfig(_accountConfigs);
                _discoveryTimer = new Timer(OnDiscoveryTimerTick, null, 500, 2000);
                _enforcementTimer = new System.Threading.Timer(EnforceLockdowns, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(50));
            }
            catch (Exception exception)
            {
                Debug.WriteLine(string.Format("[PropFirmGuardian] Engine startup failed: {0}", exception));
            }
        }

        private void WireEngineEvents()
        {
            // Event flow:
            // NT8 account events -> AccountMonitor/RiskEngine/SessionTracker/OrderInterceptor.
            // AccountMonitor breach event -> Audio + FlattenProtocol.
            // TiltDetector lockout event -> AccountMonitor state only.
            // FlattenProtocol state transitions are terminal for that cycle and do not
            // call RiskEngine again, preventing circular breach/flatten loops.
            _accountMonitor.OnBreachDetected += OnBreachDetected;
            _riskEngine.OnBreachDetected += OnRiskBreachDetected;
            _tiltDetector.OnTiltLockout += OnTiltLockout;
            _tiltDetector.OnDeathSpiral += OnDeathSpiral;
            _tiltDetector.OnTiltWarning += OnTiltWarning;
            _orderInterceptor.OnOrderBlocked += OnOrderBlocked;
            _dailyTradeLimiter.OnDailyLimitWarning += OnDailyLimitWarning;
            _dailyTradeLimiter.OnDailyLimitReached += OnDailyLimitReached;
            _timeBasedGuard.OnGuardActivated += OnGuardActivated;
        }

        private void WireNewsEvents()
        {
            if (_newsEngine == null)
                return;

            _newsEngine.OnNewsProtectionTriggered += OnNewsProtectionTriggered;
        }

        private void OnMenuItemClick(object sender, RoutedEventArgs eventArgs)
        {
            if (_isShuttingDown)
                return;

            EnsureEngineStarted();

            if (_dashboardWindow != null)
            {
                SubscribeConnections();
                RefreshAccountDiscovery();
                PopulateDashboardAccounts();
                UpdateDashboardEngineStatus();
                _dashboardWindow.Activate();
                return;
            }

            SubscribeConnections();
            RefreshAccountDiscovery();
            _dashboardControl = new GuardianDashboard();
            _dashboardControl.ConfigureServices(_accountMonitor, _newsEngine, _flattenProtocol, _persistence, _accountConfigs, RemoveAccountFromMonitoring, AddActiveAccountsToMonitoring);
            PopulateDashboardAccounts();
            UpdateDashboardEngineStatus();
            FlushPendingRecoveryAlerts();

            _dashboardWindow = new NTWindow
            {
                Caption = "Prop Firm Guardian",
                Content = _dashboardControl,
                Width = 980,
                Height = 560,
                MinWidth = 760,
                MinHeight = 420
            };

            _dashboardWindow.Closed += OnDashboardWindowClosed;
            _dashboardWindow.Show();
        }

        private void OnDashboardWindowClosed(object sender, EventArgs eventArgs)
        {
            if (_dashboardWindow != null)
                _dashboardWindow.Closed -= OnDashboardWindowClosed;

            _dashboardWindow = null;
            _dashboardControl = null;
        }

        private void Cleanup()
        {
            _isShuttingDown = true;

            if (_persistence != null && _accountMonitor != null)
                _persistence.SaveStateImmediately(_accountMonitor.ExportSnapshots());

            UnwireEngineEvents();
            UnsubscribeAccounts();
            UnsubscribeConnections();
            RemoveMenuItem();

            if (_dashboardWindow != null)
                _dashboardWindow.Closed -= OnDashboardWindowClosed;

            _dashboardWindow = null;
            _dashboardControl = null;

            if (_newsEngine != null)
                _newsEngine.Dispose();

            if (_discoveryTimer != null)
                _discoveryTimer.Dispose();

            if (_enforcementTimer != null)
            {
                _enforcementTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _enforcementTimer.Dispose();
            }

            if (_persistence != null)
                _persistence.Dispose();

            if (_accountMonitor != null)
                _accountMonitor.Dispose();

            if (_timeBasedGuard != null)
                _timeBasedGuard.Dispose();

            if (_audio != null)
                _audio.Dispose();

            _newsEngine = null;
            _sessionTracker = null;
            _tiltDetector = null;
            _orderInterceptor = null;
            _flattenProtocol = null;
            _riskEngine = null;
            _crashRecovery = null;
            _persistence = null;
            _accountMonitor = null;
            _audio = null;
            _license = null;
            _dailyTradeLimiter = null;
            _consistencyTracker = null;
            _timeBasedGuard = null;
            _passProbabilityEngine = null;
            _discoveryTimer = null;
            _enforcementTimer = null;
            _accountConfigs.Clear();
            _historyBackfilledAccounts.Clear();
        }

        private void UnwireEngineEvents()
        {
            if (_accountMonitor != null)
                _accountMonitor.OnBreachDetected -= OnBreachDetected;

            if (_riskEngine != null)
                _riskEngine.OnBreachDetected -= OnRiskBreachDetected;

            if (_tiltDetector != null)
            {
                _tiltDetector.OnTiltLockout -= OnTiltLockout;
                _tiltDetector.OnDeathSpiral -= OnDeathSpiral;
                _tiltDetector.OnTiltWarning -= OnTiltWarning;
            }

            if (_orderInterceptor != null)
                _orderInterceptor.OnOrderBlocked -= OnOrderBlocked;

            if (_dailyTradeLimiter != null)
            {
                _dailyTradeLimiter.OnDailyLimitWarning -= OnDailyLimitWarning;
                _dailyTradeLimiter.OnDailyLimitReached -= OnDailyLimitReached;
            }

            if (_timeBasedGuard != null)
                _timeBasedGuard.OnGuardActivated -= OnGuardActivated;

            if (_newsEngine != null)
                _newsEngine.OnNewsProtectionTriggered -= OnNewsProtectionTriggered;

            if (_crashRecovery != null)
                _crashRecovery.OnRecoveryAlert -= OnRecoveryAlert;
        }

        private void RemoveMenuItem()
        {
            if (_menuItem != null)
                _menuItem.Click -= OnMenuItemClick;

            if (_controlCenter != null && _menuItem != null)
                _controlCenter.MainMenu.Remove(_menuItem);

            _menuItem = null;
            _controlCenter = null;
        }

        private void SubscribeAccounts()
        {
            RefreshAccountDiscovery();
        }

        private void RefreshAccountDiscovery()
        {
            EnsureEngineStarted();

            if (_accountMonitor == null)
            {
                UpdateDashboardEngineStatus();
                return;
            }

            foreach (Account account in EnumerateVisibleAccounts())
            {
                if (!IsSupportedAccount(account))
                    continue;

                AccountConfig config = GetOrCreateConfig(account.Name);
                if (config.IsExcluded)
                    continue;

                if (!_subscribedAccounts.Add(account))
                    continue;

                _accountMonitor.InitializeAccount(account, config);
                BackfillTodayTradeCount(account, config);

                account.AccountItemUpdate += OnAccountItemUpdate;
                account.PositionUpdate += OnPositionUpdate;
                account.OrderUpdate += OnOrderUpdate;
                account.ExecutionUpdate += OnExecutionUpdate;

                Debug.WriteLine(string.Format("[PropFirmGuardian] Subscribed account events: {0}", account.Name));
            }

            PopulateDashboardAccounts();
            UpdateDashboardEngineStatus();
        }

        private void UnsubscribeAccounts()
        {
            foreach (Account account in _subscribedAccounts)
            {
                account.AccountItemUpdate -= OnAccountItemUpdate;
                account.PositionUpdate -= OnPositionUpdate;
                account.OrderUpdate -= OnOrderUpdate;
                account.ExecutionUpdate -= OnExecutionUpdate;
            }

            _subscribedAccounts.Clear();
        }

        private void RemoveAccountFromMonitoring(string accountName)
        {
            if (string.IsNullOrWhiteSpace(accountName))
                return;

            Account accountToRemove = null;
            foreach (Account account in _subscribedAccounts)
            {
                if (account != null && string.Equals(account.Name, accountName, StringComparison.OrdinalIgnoreCase))
                {
                    accountToRemove = account;
                    break;
                }
            }

            if (accountToRemove != null)
            {
                accountToRemove.AccountItemUpdate -= OnAccountItemUpdate;
                accountToRemove.PositionUpdate -= OnPositionUpdate;
                accountToRemove.OrderUpdate -= OnOrderUpdate;
                accountToRemove.ExecutionUpdate -= OnExecutionUpdate;
                _subscribedAccounts.Remove(accountToRemove);
            }

            AccountConfig config = GetOrCreateConfig(accountName);
            config.IsExcluded = true;

            if (_accountMonitor != null)
                _accountMonitor.RemoveAccount(accountName);

            if (_persistence != null)
                _persistence.SaveConfig(_accountConfigs);

            if (_dashboardControl != null)
                _dashboardControl.RemoveAccount(accountName);

            UpdateDashboardEngineStatus();
            Debug.WriteLine(string.Format("[PropFirmGuardian] Removed account from monitoring: {0}", accountName));
        }

        private void AddActiveAccountsToMonitoring()
        {
            EnsureEngineStarted();

            bool changed = false;
            foreach (Account account in EnumerateVisibleAccounts())
            {
                if (!IsSupportedAccount(account))
                    continue;

                if (account.ConnectionStatus != ConnectionStatus.Connected)
                    continue;

                AccountConfig config = GetOrCreateConfig(account.Name);
                if (config.IsExcluded)
                {
                    config.IsExcluded = false;
                    changed = true;
                }

                if (!_subscribedAccounts.Contains(account))
                {
                    _subscribedAccounts.Add(account);
                    _accountMonitor.InitializeAccount(account, config);
                    BackfillTodayTradeCount(account, config);
                    account.AccountItemUpdate += OnAccountItemUpdate;
                    account.PositionUpdate += OnPositionUpdate;
                    account.OrderUpdate += OnOrderUpdate;
                    account.ExecutionUpdate += OnExecutionUpdate;
                    Debug.WriteLine(string.Format("[PropFirmGuardian] Added active account to monitoring: {0}", account.Name));
                    changed = true;
                }
            }

            if (changed && _persistence != null)
                _persistence.SaveConfig(_accountConfigs);

            PopulateDashboardAccounts();
            UpdateDashboardEngineStatus();
        }

        private void BackfillTodayTradeCount(Account account, AccountConfig config)
        {
            if (account == null || config == null || _accountMonitor == null)
                return;

            if (!_historyBackfilledAccounts.Add(account.Name))
                return;

            int entryCount = CountTodayEntryExecutions(account, config.SessionResetTime);
            if (entryCount <= 0)
                entryCount = CountTodayEntryOrders(account, config.SessionResetTime);

            if (entryCount <= 0)
                return;

            AccountMonitor.AccountMonitorState state;
            if (!_accountMonitor.TryGetAccountState(account.Name, out state))
                return;

            DateTime sessionStart = GetSessionStart(DateTime.Now, config.SessionResetTime);
            lock (state.LockObject)
            {
                state.Snapshot.TradesToday = Math.Max(state.Snapshot.TradesToday, entryCount);
                state.Snapshot.DailyTradeLimit = config.DailyTradeLimit;
                state.Snapshot.LastUpdateTime = DateTime.UtcNow;
            }

            if (_dailyTradeLimiter != null)
                _dailyTradeLimiter.SeedTradesToday(account.Name, entryCount, sessionStart);

            Debug.WriteLine(string.Format(
                "[PropFirmGuardian] Backfilled current-session trade count: Account={0}, Entries={1}, SessionStart={2:O}",
                account.Name,
                entryCount,
                sessionStart));
        }

        private static int CountTodayEntryExecutions(Account account, TimeSpan sessionResetTime)
        {
            object executions = GetFirstPropertyValue(account, "Executions", "ExecutionHistory", "AccountExecutions", "Execution");

            System.Collections.IEnumerable enumerable = executions as System.Collections.IEnumerable;
            if (enumerable == null)
                return 0;

            DateTime sessionStart = GetSessionStart(DateTime.Now, sessionResetTime);
            HashSet<string> countedOrders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int entryCount = 0;

            foreach (object execution in enumerable)
            {
                DateTime executionTime;
                if (!TryGetDateTimeProperty(execution, "Time", out executionTime) || executionTime < sessionStart)
                    continue;

                object order = GetPropertyValue(execution, "Order");
                string actionText = Convert.ToString(GetFirstPropertyValue(order, "OrderAction", "Action", "OrderActionType"));
                if (string.IsNullOrWhiteSpace(actionText))
                    actionText = Convert.ToString(GetFirstPropertyValue(execution, "OrderAction", "Action", "MarketPosition"));

                if (!IsEntryOrderAction(actionText))
                    continue;

                string orderId = Convert.ToString(GetPropertyValue(execution, "OrderId"));
                if (string.IsNullOrWhiteSpace(orderId))
                    orderId = Convert.ToString(GetPropertyValue(order, "OrderId"));

                if (!string.IsNullOrWhiteSpace(orderId) && !countedOrders.Add(orderId))
                    continue;

                entryCount++;
            }

            return entryCount;
        }

        private static int CountTodayEntryOrders(Account account, TimeSpan sessionResetTime)
        {
            object orders = GetFirstPropertyValue(account, "Orders", "OrderHistory", "AccountOrders");
            System.Collections.IEnumerable enumerable = orders as System.Collections.IEnumerable;
            if (enumerable == null)
                return 0;

            DateTime sessionStart = GetSessionStart(DateTime.Now, sessionResetTime);
            HashSet<string> countedOrders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int entryCount = 0;

            foreach (object order in enumerable)
            {
                if (!IsFilledOrder(order))
                    continue;

                DateTime orderTime;
                if (!TryGetAnyDateTimeProperty(order, out orderTime) || orderTime < sessionStart)
                    continue;

                string actionText = Convert.ToString(GetFirstPropertyValue(order, "OrderAction", "Action", "OrderActionType"));
                if (!IsEntryOrderAction(actionText))
                    continue;

                string orderId = Convert.ToString(GetFirstPropertyValue(order, "OrderId", "Id", "Name"));
                if (!string.IsNullOrWhiteSpace(orderId) && !countedOrders.Add(orderId))
                    continue;

                entryCount++;
            }

            Debug.WriteLine(string.Format("[PropFirmGuardian] Order-history backfill scanned {0} entry orders for {1}", entryCount, account.Name));
            return entryCount;
        }

        private static bool IsFilledOrder(object order)
        {
            string state = Convert.ToString(GetFirstPropertyValue(order, "OrderState", "State"));
            if (!string.IsNullOrWhiteSpace(state) && state.IndexOf("Filled", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            int filled = GetIntProperty(order, "Filled");
            int quantity = GetIntProperty(order, "Quantity");
            return filled > 0 || quantity > 0;
        }

        private static bool IsEntryOrderAction(string actionText)
        {
            if (string.IsNullOrWhiteSpace(actionText))
                return false;

            if (actionText.IndexOf("SellShort", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return actionText.IndexOf("Buy", StringComparison.OrdinalIgnoreCase) >= 0
                && actionText.IndexOf("Cover", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static DateTime GetSessionStart(DateTime now, TimeSpan sessionResetTime)
        {
            DateTime sessionStart = now.Date.Add(sessionResetTime);
            if (now < sessionStart)
                sessionStart = sessionStart.AddDays(-1);

            return sessionStart;
        }

        private static bool TryGetDateTimeProperty(object source, string propertyName, out DateTime value)
        {
            value = DateTime.MinValue;
            object rawValue = GetPropertyValue(source, propertyName);
            if (rawValue is DateTime)
            {
                value = (DateTime)rawValue;
                return true;
            }

            if (rawValue == null)
                return false;

            return DateTime.TryParse(Convert.ToString(rawValue), out value);
        }

        private static bool TryGetAnyDateTimeProperty(object source, out DateTime value)
        {
            return TryGetDateTimeProperty(source, "Time", out value)
                || TryGetDateTimeProperty(source, "TimeSubmitted", out value)
                || TryGetDateTimeProperty(source, "TimeFilled", out value)
                || TryGetDateTimeProperty(source, "SubmitTime", out value)
                || TryGetDateTimeProperty(source, "UpdateTime", out value);
        }

        private static int GetIntProperty(object source, string propertyName)
        {
            object value = GetPropertyValue(source, propertyName);
            if (value == null)
                return 0;

            int parsed;
            return int.TryParse(Convert.ToString(value), out parsed) ? parsed : 0;
        }

        private static object GetFirstPropertyValue(object source, params string[] propertyNames)
        {
            if (propertyNames == null)
                return null;

            foreach (string propertyName in propertyNames)
            {
                object value = GetPropertyValue(source, propertyName);
                if (value != null)
                    return value;
            }

            return null;
        }

        private static object GetPropertyValue(object source, string propertyName)
        {
            if (source == null || string.IsNullOrWhiteSpace(propertyName))
                return null;

            try
            {
                System.Reflection.PropertyInfo property = source.GetType().GetProperty(propertyName);
                return property != null ? property.GetValue(source, null) : null;
            }
            catch
            {
                return null;
            }
        }

        private void SubscribeConnections()
        {
            foreach (Connection connection in Connection.Connections)
            {
                if (connection == null || !_subscribedConnections.Add(connection))
                    continue;

                Debug.WriteLine(string.Format("[PropFirmGuardian] Subscribed connection events: {0}", connection.Options != null ? connection.Options.Name : "Unknown"));
            }

            if (!_isConnectionStatusSubscribed)
            {
                Connection.ConnectionStatusUpdate += OnConnectionStatusUpdate;
                _isConnectionStatusSubscribed = true;
            }
        }

        private void UnsubscribeConnections()
        {
            if (_isConnectionStatusSubscribed)
            {
                Connection.ConnectionStatusUpdate -= OnConnectionStatusUpdate;
                _isConnectionStatusSubscribed = false;
            }

            _subscribedConnections.Clear();
        }

        private void OnAccountItemUpdate(object sender, AccountItemEventArgs e)
        {
            if (_accountMonitor == null || e.Account == null)
                return;

            if (e.AccountItem == AccountItem.RealizedProfitLoss)
            {
                _accountMonitor.UpdateRealizedPnL(e.Account.Name, e.Value);
                QueueDashboardUpdate(e.Account.Name);
                EvaluateAndFlattenIfNeeded(e.Account.Name);
            }
            else if (e.AccountItem == AccountItem.UnrealizedProfitLoss)
            {
                _accountMonitor.UpdateUnrealizedPnL(e.Account.Name, e.Value);
                QueueDashboardUpdate(e.Account.Name);
                EvaluateAndFlattenIfNeeded(e.Account.Name);
            }

            Debug.WriteLine(string.Format(
                "[PropFirmGuardian] Account item update | Account={0}, Item={1}, Value={2}, Currency={3}, Time={4:O}",
                e.Account.Name,
                e.AccountItem,
                e.Value,
                e.Currency,
                e.Time));
        }

        private void OnPositionUpdate(object sender, PositionEventArgs e)
        {
            if (_accountMonitor == null || e.Position == null || e.Position.Account == null)
                return;

            double unrealizedPnL = 0.0;
            if (e.Position.MarketPosition != MarketPosition.Flat)
            {
                double marketPrice = e.Position.GetMarketPrice();
                unrealizedPnL = e.Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, marketPrice);
            }

            string accountName = e.Position.Account.Name;
            _accountMonitor.UpdateUnrealizedPnL(accountName, unrealizedPnL);
            TrackPositionLifecycle(e.Position.Account, e.Position, e.MarketPosition, e.Quantity, e.AveragePrice);

            if (IsAccountEnforcementLocked(accountName) && e.Position.MarketPosition != MarketPosition.Flat)
                EnforceLockedAccount(e.Position.Account, "Locked account position update");

            QueueDashboardUpdate(accountName);
            EvaluateAndFlattenIfNeeded(accountName);

            Debug.WriteLine(string.Format(
                "[PropFirmGuardian] Position update | Account={0}, Instrument={1}, MarketPosition={2}, Quantity={3}, AveragePrice={4}",
                accountName,
                e.Position.Instrument != null ? e.Position.Instrument.FullName : "Unknown",
                e.MarketPosition,
                e.Quantity,
                e.AveragePrice));
        }

        private void OnOrderUpdate(object sender, OrderEventArgs e)
        {
            if (e.Order == null || e.Order.Account == null)
                return;

            _orderInterceptor.OnOrderUpdate(e.Order, e.Order.Account);

            if (IsAccountEnforcementLocked(e.Order.Account.Name))
            {
                if (IsActiveOrderState(e.Order.OrderState))
                    e.Order.Account.Cancel(new[] { e.Order });

                if (e.Order.OrderState == OrderState.Filled || e.Order.OrderState == OrderState.PartFilled)
                    EnforceLockedAccount(e.Order.Account, "Locked account order filled");
            }

            Debug.WriteLine(string.Format(
                "[PropFirmGuardian] Order update | Account={0}, Instrument={1}, OrderId={2}, State={3}, Filled={4}, Quantity={5}, AverageFillPrice={6}",
                e.Order.Account.Name,
                e.Order.Instrument != null ? e.Order.Instrument.FullName : "Unknown",
                e.OrderId,
                e.OrderState,
                e.Filled,
                e.Quantity,
                e.AverageFillPrice));
        }

        private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            if (e.Execution == null || e.Execution.Account == null)
                return;

            string accountName = e.Execution.Account.Name;

            QueueDashboardUpdate(accountName);
            EvaluateAndFlattenIfNeeded(accountName);

            Debug.WriteLine(string.Format(
                "[PropFirmGuardian] Execution update | Account={0}, Instrument={1}, ExecutionId={2}, OrderId={3}, Price={4}, Quantity={5}, MarketPosition={6}, Time={7:O}",
                accountName,
                e.Execution.Instrument != null ? e.Execution.Instrument.FullName : "Unknown",
                e.ExecutionId,
                e.OrderId,
                e.Price,
                e.Quantity,
                e.MarketPosition,
                e.Time));
        }

        private void OnConnectionStatusUpdate(object sender, ConnectionStatusEventArgs e)
        {
            RefreshAccountDiscovery();
            HandleConnectionStatusForAccounts(e.Connection, e.Status);

            Debug.WriteLine(string.Format(
                "[PropFirmGuardian] Connection status update | PreviousStatus={0}, Status={1}, PreviousPriceStatus={2}, PriceStatus={3}, Error={4}, NativeError={5}",
                e.PreviousStatus,
                e.Status,
                e.PreviousPriceStatus,
                e.PriceStatus,
                e.Error,
                e.NativeError));
        }

        private void HandleConnectionStatusForAccounts(Connection connection, ConnectionStatus status)
        {
            bool disconnected = status == ConnectionStatus.Disconnected
                || status == ConnectionStatus.ConnectionLost
                || status == ConnectionStatus.Disconnecting;

            foreach (Account account in _subscribedAccounts)
            {
                if (connection != null && account.Connection != connection)
                    continue;

                if (disconnected)
                {
                    _accountMonitor.TransitionState(account.Name, AccountState.Disconnected, "Connection lost");
                    if (_persistence != null)
                    {
                        Debug.WriteLine(string.Format("[PERSIST] Disconnect detected for {0}; forcing immediate state save", account.Name));
                        _persistence.SaveStateImmediately(_accountMonitor.ExportSnapshots());
                    }
                }
                else if (_accountMonitor.GetCurrentState(account.Name) == AccountState.Disconnected)
                {
                    _accountMonitor.TransitionState(account.Name, AccountState.Active, "Connection restored");
                }

                QueueDashboardUpdate(account.Name);
            }
        }

        private void TrackPositionLifecycle(Account account, Position position, MarketPosition marketPosition, int quantity, double averagePrice)
        {
            if (account == null || position == null)
                return;

            string accountName = account.Name;
            string instrumentName = position.Instrument != null ? position.Instrument.FullName : "Unknown";
            string key = accountName + "|" + instrumentName;
            PositionLifecycleState lifecycle = _positionLifecycleStates.GetOrAdd(key, name => new PositionLifecycleState());
            int absoluteQuantity = Math.Abs(quantity);
            bool isFlat = marketPosition == MarketPosition.Flat || absoluteQuantity == 0;

            lock (lifecycle.LockObject)
            {
                if (!lifecycle.IsOpen && !isFlat)
                {
                    lifecycle.IsOpen = true;
                    lifecycle.EntryTime = DateTime.Now;
                    lifecycle.EntryPrice = averagePrice;
                    lifecycle.Quantity = absoluteQuantity;
                    lifecycle.Instrument = instrumentName;
                    lifecycle.EntryRealizedPnL = ReadRealizedPnL(account);

                    if (_dailyTradeLimiter != null)
                        _dailyTradeLimiter.RecordEntry(accountName, lifecycle.EntryTime);

                    Debug.WriteLine(string.Format(
                        "[SESSION] Position opened: {0} | {1} | qty={2} | price={3}",
                        accountName,
                        instrumentName,
                        absoluteQuantity,
                        averagePrice));
                    return;
                }

                if (lifecycle.IsOpen && !isFlat)
                {
                    lifecycle.Quantity = Math.Max(lifecycle.Quantity, absoluteQuantity);
                    return;
                }

                if (!lifecycle.IsOpen || !isFlat)
                    return;

                double realizedPnL = ReadRealizedPnL(account) - lifecycle.EntryRealizedPnL;
                TradeRecord trade = new TradeRecord
                {
                    EntryTime = lifecycle.EntryTime,
                    ExitTime = DateTime.Now,
                    Quantity = lifecycle.Quantity,
                    EntryPrice = lifecycle.EntryPrice,
                    ExitPrice = averagePrice,
                    RealizedPnL = realizedPnL,
                    Instrument = lifecycle.Instrument
                };

                lifecycle.Reset();

                if (_sessionTracker != null)
                {
                    _sessionTracker.OnTradeClosed(accountName, trade);
                    UpdateSessionQuality(accountName);
                }
            }
        }

        private void OnDiscoveryTimerTick(object state)
        {
            if (_isShuttingDown || Interlocked.Exchange(ref _isDiscoveringAccounts, 1) == 1)
                return;

            try
            {
                SubscribeConnections();
                RefreshAccountDiscovery();
            }
            catch (Exception exception)
            {
                Debug.WriteLine(string.Format("[PropFirmGuardian] Account discovery timer error: {0}", exception.Message));
            }
            finally
            {
                Interlocked.Exchange(ref _isDiscoveringAccounts, 0);
            }
        }

        private void EnforceLockdowns(object state)
        {
            if (_isShuttingDown || Interlocked.Exchange(ref _isEnforcingLocks, 1) == 1)
                return;

            try
            {
                if (_accountMonitor == null)
                    return;

                int count = _accountMonitor.CopyAccountStatesTo(_enforcementStateBuffer);
                for (int index = 0; index < count; index++)
                {
                    AccountMonitor.AccountMonitorState accountState = _enforcementStateBuffer[index];
                    _enforcementStateBuffer[index] = null;

                    if (accountState == null || accountState.Config == null || !accountState.Config.HardEnforcementEnabled)
                        continue;

                    Account account;
                    AccountState status;
                    lock (accountState.LockObject)
                    {
                        status = accountState.Snapshot.LastKnownStatus;
                        if (status != AccountState.Locked && status != AccountState.Flattening && status != AccountState.NewsLocked)
                            continue;

                        account = accountState.AccountRef;
                    }

                    if (account == null)
                        continue;

                    if (!IsAccountConnected(account))
                        continue;

                    foreach (Order order in account.Orders)
                    {
                        if (order == null || !IsActiveOrderState(order.OrderState))
                            continue;

                        Debug.WriteLine(string.Format(
                            "[ENFORCE] Account {0} has working order {1} state={2}. Cancelling.",
                            account.Name,
                            order.OrderId,
                            order.OrderState));
                        account.Cancel(new[] { order });
                    }

                    foreach (Position position in account.Positions)
                    {
                        if (position == null || position.Quantity == 0)
                            continue;

                        Debug.WriteLine(string.Format(
                            "[ENFORCE] Account {0} is {1} but has position {2} qty={3}. Flattening NOW.",
                            account.Name,
                            status,
                            position.Instrument != null ? position.Instrument.FullName : "Unknown",
                            position.Quantity));
                        QueueFlattenRequest(account.Name);
                        break;
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.WriteLine(string.Format("[PropFirmGuardian] Lock enforcement timer error: {0}", exception.Message));
            }
            finally
            {
                Interlocked.Exchange(ref _isEnforcingLocks, 0);
            }
        }

        private void QueueFlattenRequest(string accountName)
        {
            if (string.IsNullOrWhiteSpace(accountName))
                return;

            lock (_flattenRequestLock)
            {
                for (int index = 0; index < _flattenRequestCount; index++)
                {
                    if (string.Equals(_flattenRequestAccountNames[index], accountName, StringComparison.OrdinalIgnoreCase))
                        return;
                }

                if (_flattenRequestCount >= _flattenRequestAccountNames.Length)
                    return;

                _flattenRequestAccountNames[_flattenRequestCount] = accountName;
                _flattenRequestCount++;
            }

            if (Interlocked.Exchange(ref _isFlattenDispatchScheduled, 1) == 0)
                ThreadSafeDispatcher.SafeInvoke(_processFlattenRequestsAction, DispatcherPriority.Render);
        }

        private void ProcessFlattenRequestsOnDispatcher()
        {
            Interlocked.Exchange(ref _isFlattenDispatchScheduled, 0);

            lock (_flattenRequestLock)
            {
                for (int index = 0; index < _flattenRequestCount; index++)
                {
                    string accountName = _flattenRequestAccountNames[index];
                    _flattenRequestAccountNames[index] = null;

                    if (!string.IsNullOrWhiteSpace(accountName) && _flattenProtocol != null)
                        _flattenProtocol.ExecuteFlatten(accountName, "Hard enforcement: locked account with open position");
                }

                _flattenRequestCount = 0;
            }
        }

        private void EnforceLockedAccount(Account account, string reason)
        {
            if (account == null || _flattenProtocol == null)
                return;

            try
            {
                if (IsAccountConnected(account))
                    CancelActiveOrders(account);

                bool hasOpenPosition = false;
                foreach (Position position in account.Positions)
                {
                    if (position != null && (position.Quantity != 0 || position.MarketPosition != MarketPosition.Flat))
                    {
                        hasOpenPosition = true;
                        break;
                    }
                }

                if (hasOpenPosition)
                    _flattenProtocol.ExecuteFlatten(account.Name, reason);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(string.Format("[PropFirmGuardian] Lock enforcement failed for {0}: {1}", account.Name, exception.Message));
            }
        }

        private bool IsAccountEnforcementLocked(string accountName)
        {
            if (_accountMonitor == null || string.IsNullOrWhiteSpace(accountName))
                return false;

            AccountState state = _accountMonitor.GetCurrentState(accountName);
            return state == AccountState.Locked
                || state == AccountState.HardLocked
                || state == AccountState.Flattening
                || state == AccountState.NewsLocked;
        }

        private static void CancelActiveOrders(Account account)
        {
            if (account == null)
                return;

            foreach (Order order in account.Orders)
            {
                if (order != null && IsActiveOrderState(order.OrderState))
                    account.Cancel(new[] { order });
            }
        }

        private static bool IsAccountConnected(Account account)
        {
            if (account == null)
                return false;

            try
            {
                return account.ConnectionStatus == ConnectionStatus.Connected;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsActiveOrderState(OrderState orderState)
        {
            return orderState == OrderState.Accepted
                || orderState == OrderState.AcceptedByRisk
                || orderState == OrderState.Submitted
                || orderState == OrderState.TriggerPending
                || orderState == OrderState.Working
                || orderState == OrderState.PartFilled
                || orderState == OrderState.ChangePending
                || orderState == OrderState.ChangeSubmitted
                || orderState == OrderState.CancelPending
                || orderState == OrderState.CancelSubmitted;
        }

        private void OnBreachDetected(string accountName, string rule, double amount)
        {
            if (_audio != null)
                _audio.Speak(string.Format("Critical. Account {0} breached {1}.", accountName, rule), AlertPriority.Critical);

            if (_flattenProtocol != null && _accountMonitor != null && !IsTerminalState(_accountMonitor.GetCurrentState(accountName)))
                _flattenProtocol.ExecuteFlatten(accountName, rule);

            if (_persistence != null && _accountMonitor != null)
                _persistence.SaveStateImmediately(_accountMonitor.ExportSnapshots());
        }

        private void OnRiskBreachDetected(string accountName, string ruleType, double amount)
        {
            Debug.WriteLine(string.Format("[MAIN] Breach event received: {0} | {1}", accountName, ruleType));

            if (_audio != null)
                _audio.Speak(string.Format("Critical. Account {0} breached {1}.", accountName, ruleType), AlertPriority.Critical);

            if (_flattenProtocol != null)
                _flattenProtocol.ExecuteFlatten(accountName, string.Format("Risk breach: {0}", ruleType));

            if (_persistence != null && _accountMonitor != null)
                _persistence.SaveStateImmediately(_accountMonitor.ExportSnapshots());
        }

        private void OnTiltLockout(string accountName, string pattern, TimeSpan duration)
        {
            if (_audio != null)
                _audio.Speak(string.Format("Tilt {0} detected. Account {1} locked.", pattern, accountName), AlertPriority.Warning);

            if (_accountMonitor != null)
                _accountMonitor.TransitionState(accountName, AccountState.Locked, pattern);

            QueueDashboardUpdate(accountName);
        }

        private void OnDeathSpiral(string accountName, TimeSpan duration)
        {
            if (_audio != null)
                _audio.SpeakTiltLockout(accountName, (int)duration.TotalMinutes);

            QueueDashboardUpdate(accountName);
        }

        private void OnTiltWarning(string accountName, string pattern, string message)
        {
            if (_audio != null)
                _audio.Speak(message, AlertPriority.Warning);
        }

        private void OnOrderBlocked(string accountName, TimeSpan remainingLockout)
        {
            if (_audio != null)
                _audio.Speak(string.Format("Order blocked. Account {0} is locked.", accountName), AlertPriority.Warning);
        }

        private void OnDailyLimitWarning(string accountName, int tradesToday, int limit)
        {
            if (_audio != null)
                _audio.Speak("Warning. Daily trade limit approaching.", AlertPriority.Warning);

            QueueDashboardUpdate(accountName);
        }

        private void OnDailyLimitReached(string accountName)
        {
            if (_audio != null)
                _audio.Speak(string.Format("Account {0} locked. Daily trade limit reached.", accountName), AlertPriority.Critical);

            QueueDashboardUpdate(accountName);
            if (_persistence != null && _accountMonitor != null)
                _persistence.SaveStateImmediately(_accountMonitor.ExportSnapshots());
        }

        private void OnGuardActivated(string accountName, string guardName)
        {
            QueueDashboardUpdate(accountName);
        }

        private void OnNewsProtectionTriggered(string accountName, NewsEvent newsEvent)
        {
            if (_audio != null)
                _audio.Speak("News lockout active.", AlertPriority.Warning);

            QueueDashboardUpdate(accountName);
        }

        private void OnRecoveryAlert(string accountName, string message)
        {
            string banner = string.Format("RECOVERY ALERT: {0} - {1}", accountName, message);
            Debug.WriteLine(string.Format("[RECOVERY] Alert queued for dashboard: {0}", banner));

            if (_dashboardControl != null)
                _dashboardControl.ShowRecoveryAlert(banner, TimeSpan.FromSeconds(30));
            else
                _pendingRecoveryAlerts.Add(banner);
        }

        private void FlushPendingRecoveryAlerts()
        {
            if (_dashboardControl == null || _pendingRecoveryAlerts.Count == 0)
                return;

            foreach (string alert in _pendingRecoveryAlerts)
                _dashboardControl.ShowRecoveryAlert(alert, TimeSpan.FromSeconds(30));

            _pendingRecoveryAlerts.Clear();
        }

        private void EvaluateAndFlattenIfNeeded(string accountName)
        {
            if (_riskEngine == null || _flattenProtocol == null || _accountMonitor == null)
                return;

            AccountState state = _accountMonitor.GetCurrentState(accountName);
            if (IsTerminalState(state))
                return;

            _riskEngine.EvaluateRules(accountName);
        }

        private void UpdateSessionQuality(string accountName)
        {
            if (_sessionTracker == null || _consistencyTracker == null || _accountMonitor == null)
                return;

            SessionQualityResult result = _consistencyTracker.Calculate(accountName, _sessionTracker.GetClosedTrades(accountName));
            AccountMonitor.AccountMonitorState state;
            if (!_accountMonitor.TryGetAccountState(accountName, out state))
                return;

            lock (state.LockObject)
            {
                state.Snapshot.SessionQualityScore = result.Score;
                state.Snapshot.SessionQualityMessage = result.Message;
                if (result.Score < 10.0 && state.Snapshot.LastKnownStatus != AccountState.Locked)
                {
                    state.Snapshot.LastKnownStatus = AccountState.Warning;
                    state.Snapshot.LockReason = "Session quality critical";
                }
            }
        }

        private static bool IsTerminalState(AccountState state)
        {
            return state == AccountState.Flattening
                || state == AccountState.Locked
                || state == AccountState.HardLocked
                || state == AccountState.NewsLocked
                || state == AccountState.Disconnected;
        }

        private void QueueDashboardUpdate(string accountName)
        {
            if (_dashboardControl == null || _accountMonitor == null)
                return;

            AccountMonitor.AccountMonitorState state;
            if (!_accountMonitor.TryGetAccountState(accountName, out state))
                return;

            lock (state.LockObject)
            {
                double peak = state.Snapshot.PeakUnrealizedPnL;
                double realized = state.Snapshot.CurrentRealizedPnL;
                double unrealized = ReadUnrealizedPnL(state.AccountRef);
                double drawdown = Math.Max(0.0, peak - (realized + unrealized));
                string connectionStatus = state.AccountRef != null ? state.AccountRef.ConnectionStatus.ToString() : "Disconnected";
                AccountState status = state.Snapshot.LastKnownStatus;
                TimeSpan? remaining = state.Snapshot.LockedUntil.HasValue
                    ? state.Snapshot.LockedUntil.Value - DateTime.Now
                    : (TimeSpan?)null;
                AccountConfig config = state.Config;
                PassProbabilityResult passResult = _passProbabilityEngine != null && state.AccountRef != null
                    ? _passProbabilityEngine.GetPassProbability(state.AccountRef)
                    : new PassProbabilityResult(state.Snapshot.PassProbability, state.Snapshot.RequiredDailyAverage, string.Empty);

                state.Snapshot.PassProbability = passResult.Probability;
                state.Snapshot.RequiredDailyAverage = passResult.RequiredDailyAverage;

                _dashboardControl.UpdateAccount(accountName, vm =>
                {
                    vm.ConnectionStatus = connectionStatus;
                    vm.RealizedPnL = realized;
                    vm.UnrealizedPnL = unrealized;
                    vm.PeakPnL = peak;
                    vm.DrawdownFromPeak = drawdown;
                    vm.Status = status;
                    vm.IsHardLock = state.Snapshot.IsHardLock;
                    vm.LockoutTimeRemaining = remaining.HasValue && remaining.Value > TimeSpan.Zero ? remaining : null;
                    vm.TradesToday = state.Snapshot.TradesToday;
                    vm.DailyTradeLimit = state.Snapshot.DailyTradeLimit > 0 ? state.Snapshot.DailyTradeLimit : (config != null ? config.DailyTradeLimit : 0);
                    vm.SessionQualityScore = state.Snapshot.SessionQualityScore;
                    vm.SessionQualityMessage = state.Snapshot.SessionQualityMessage;
                    vm.ActiveGuards = state.Snapshot.ActiveGuards;
                    vm.PassProbability = passResult.Probability;
                    vm.RequiredDailyAverage = passResult.RequiredDailyAverage;
                    vm.PassProbabilityTooltip = passResult.Tooltip;
                    vm.DailyLimitRemaining = config != null && config.DailyLossLimit > 0.0
                        ? Math.Max(0.0, config.DailyLossLimit + realized)
                        : 0.0;
                    vm.IsShadowMode = config != null && config.ShadowModeEnabled;
                });
            }
        }

        private void PopulateDashboardAccounts()
        {
            if (_dashboardControl == null)
                return;

            foreach (Account account in _subscribedAccounts)
            {
                AccountConfig config = GetOrCreateConfig(account.Name);
                if (config.IsExcluded)
                    continue;

                AccountMonitor.AccountMonitorState state = null;
                if (_accountMonitor != null)
                    _accountMonitor.TryGetAccountState(account.Name, out state);

                SessionSnapshot snapshot = state != null ? state.Snapshot : null;
                _dashboardControl.AddAccount(new AccountViewModel
                {
                    AccountName = account.Name,
                    ConnectionStatus = account.ConnectionStatus.ToString(),
                    RealizedPnL = ReadRealizedPnL(account),
                    UnrealizedPnL = 0.0,
                    PeakPnL = snapshot != null ? snapshot.PeakUnrealizedPnL : 0.0,
                    DrawdownFromPeak = 0.0,
                    DailyLimitRemaining = config.DailyLossLimit,
                    Status = _accountMonitor != null ? _accountMonitor.GetCurrentState(account.Name) : AccountState.Active,
                    LockoutTimeRemaining = null,
                    TradesToday = snapshot != null ? snapshot.TradesToday : 0,
                    DailyTradeLimit = snapshot != null && snapshot.DailyTradeLimit > 0 ? snapshot.DailyTradeLimit : config.DailyTradeLimit,
                    SessionQualityScore = snapshot != null ? snapshot.SessionQualityScore : 50.0,
                    SessionQualityMessage = snapshot != null ? snapshot.SessionQualityMessage : "No closed trades yet.",
                    ActiveGuards = snapshot != null ? snapshot.ActiveGuards : string.Empty,
                    PassProbability = snapshot != null ? snapshot.PassProbability : 50.0,
                    RequiredDailyAverage = snapshot != null ? snapshot.RequiredDailyAverage : 0.0,
                    PassProbabilityTooltip = snapshot != null && snapshot.RequiredDailyAverage > 0.0
                        ? string.Format("Required average: ${0:0}/day.", snapshot.RequiredDailyAverage)
                        : "Trajectory will update as trading data is collected.",
                    IsHardLock = snapshot != null && snapshot.IsHardLock,
                    IsShadowMode = config.ShadowModeEnabled
                });
            }
        }

        private IEnumerable<Account> EnumerateVisibleAccounts()
        {
            HashSet<Account> accounts = new HashSet<Account>();

            foreach (Account account in Account.All)
            {
                if (account != null && accounts.Add(account))
                    yield return account;
            }

            foreach (Connection connection in Connection.Connections)
            {
                if (connection == null || connection.Accounts == null)
                    continue;

                foreach (Account account in connection.Accounts)
                {
                    if (account != null && accounts.Add(account))
                        yield return account;
                }
            }
        }

        private void UpdateDashboardEngineStatus()
        {
            if (_dashboardControl == null)
                return;

            int accountAllCount = 0;
            int connectionAccountCount = 0;

            try
            {
                accountAllCount = Account.All.Count;
            }
            catch
            {
            }

            try
            {
                foreach (Connection connection in Connection.Connections)
                {
                    if (connection != null && connection.Accounts != null)
                        connectionAccountCount += connection.Accounts.Count;
                }
            }
            catch
            {
            }

            _dashboardControl.SetEngineStatus(string.Format(
                "Status: {0} | Account.All={1} | Connection accounts={2} | Subscribed={3}",
                _accountMonitor != null ? "Engine active" : "ENGINE NOT STARTED",
                accountAllCount,
                connectionAccountCount,
                _subscribedAccounts.Count));
        }

        private void EnsureEngineStarted()
        {
            if (_accountMonitor != null)
                return;

            InitializeEngine();
        }

        private void EnsureDefaultConfigsForDiscoveredAccounts()
        {
            bool createdDefaultConfig = false;

            foreach (Account account in Account.All)
            {
                if (!IsSupportedAccount(account))
                    continue;

                if (_accountConfigs.Any(config => string.Equals(config.AccountName, account.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                _accountConfigs.Add(CreateDefaultConfig(account.Name));
                createdDefaultConfig = true;
            }

            if (createdDefaultConfig && _persistence != null)
                _persistence.SaveConfig(_accountConfigs);
        }

        private AccountConfig GetOrCreateConfig(string accountName)
        {
            AccountConfig config = _accountConfigs.FirstOrDefault(item =>
                string.Equals(item.AccountName, accountName, StringComparison.OrdinalIgnoreCase));

            if (config != null)
                return config;

            config = CreateDefaultConfig(accountName);
            _accountConfigs.Add(config);
            if (_persistence != null)
                _persistence.SaveConfig(_accountConfigs);

            return config;
        }

        private static AccountConfig CreateDefaultConfig(string accountName)
        {
            return new AccountConfig
            {
                AccountName = accountName,
                PropFirmName = "Custom",
                DailyLossLimit = 0.0,
                TrailingDrawdown = 0.0,
                MaxPositionSize = 0.0,
                SafetyBuffer = 25.0,
                IsLivePA = false,
                IsEval = false,
                SessionResetTime = TimeSpan.Zero,
                IsExcluded = false,
                ShadowModeEnabled = false,
                HardEnforcementEnabled = true,
                DailyTradeLimit = 10,
                EnableDailyLimit = true,
                AllowEmergencyOverride = true,
                EmergencyOverrideTrades = 2
            };
        }

        private static bool IsSupportedAccount(Account account)
        {
            return account != null
                && !string.IsNullOrEmpty(account.Name)
                && !account.Name.StartsWith("Playback", StringComparison.OrdinalIgnoreCase);
        }

        private static double ReadRealizedPnL(Account account)
        {
            if (account == null)
                return 0.0;

            try
            {
                return account.Get(AccountItem.RealizedProfitLoss, account.Denomination);
            }
            catch
            {
                return 0.0;
            }
        }

        private static double ReadUnrealizedPnL(Account account)
        {
            if (account == null)
                return 0.0;

            double unrealized = 0.0;
            foreach (Position position in account.Positions)
            {
                if (position == null || position.MarketPosition == MarketPosition.Flat)
                    continue;

                try
                {
                    unrealized += position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, position.GetMarketPrice());
                }
                catch
                {
                }
            }

            return unrealized;
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class AddOnAttribute : Attribute
    {
        public AddOnAttribute(string name)
        {
            Name = name;
        }

        public string Name { get; private set; }
        public string Description { get; set; }
        public string Author { get; set; }
        public string Version { get; set; }
    }

    internal sealed class PositionLifecycleState
    {
        public readonly object LockObject = new object();
        public bool IsOpen;
        public DateTime EntryTime;
        public double EntryPrice;
        public int Quantity;
        public double EntryRealizedPnL;
        public string Instrument;

        public void Reset()
        {
            IsOpen = false;
            EntryTime = DateTime.MinValue;
            EntryPrice = 0.0;
            Quantity = 0;
            EntryRealizedPnL = 0.0;
            Instrument = string.Empty;
        }
    }
}
