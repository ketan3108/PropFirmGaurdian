using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using PropFirmGuardian.Core;
using PropFirmGuardian.Models;
using PropFirmGuardian.News;
using PropFirmGuardian.Services;
using PropFirmGuardian.UI.ViewModels;

namespace PropFirmGuardian.UI
{
    public partial class GuardianDashboard : UserControl
    {
        private readonly ObservableCollection<AccountViewModel> _accounts;
        private readonly ConcurrentQueue<Action> _pendingUiUpdates;
        private readonly object _accountsLock;
        private readonly Dictionary<string, AccountConfig> _configsByAccount;
        private readonly DispatcherTimer _uiRefreshTimer;
        private string _engineStatus;
        private AccountMonitor _accountMonitor;
        private NewsEngine _newsEngine;
        private FlattenProtocol _flattenProtocol;
        private PersistenceService _persistenceService;
        private Action<string> _removeAccountAction;
        private Action _addActiveAccountsAction;
        private DateTime _recoveryAlertExpiresUtc;

        public GuardianDashboard()
        {
            _accounts = new ObservableCollection<AccountViewModel>();
            _pendingUiUpdates = new ConcurrentQueue<Action>();
            _accountsLock = new object();
            _configsByAccount = new Dictionary<string, AccountConfig>(StringComparer.OrdinalIgnoreCase);
            _engineStatus = "Status: Engine starting";

            InitializeComponent();

            AccountsGrid.ItemsSource = _accounts;
            BindingOperations.EnableCollectionSynchronization(_accounts, _accountsLock);

            _uiRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _uiRefreshTimer.Interval = TimeSpan.FromMilliseconds(200);
            _uiRefreshTimer.Tick += UiRefreshTimer_Tick;
            _uiRefreshTimer.Start();
        }

        public void SetFlattenProtocol(FlattenProtocol flattenProtocol)
        {
            _flattenProtocol = flattenProtocol;
        }

        public void ConfigureServices(
            AccountMonitor accountMonitor,
            NewsEngine newsEngine,
            FlattenProtocol flattenProtocol,
            PersistenceService persistenceService,
            IEnumerable<AccountConfig> configs,
            Action<string> removeAccountAction,
            Action addActiveAccountsAction)
        {
            _accountMonitor = accountMonitor;
            _newsEngine = newsEngine;
            _flattenProtocol = flattenProtocol;
            _persistenceService = persistenceService;
            _removeAccountAction = removeAccountAction;
            _addActiveAccountsAction = addActiveAccountsAction;

            _configsByAccount.Clear();
            if (configs == null)
                return;

            foreach (AccountConfig config in configs)
            {
                if (config != null && !string.IsNullOrWhiteSpace(config.AccountName))
                    _configsByAccount[config.AccountName] = config;
            }
        }

        public void AddAccount(AccountViewModel vm)
        {
            if (vm == null)
                return;

            _pendingUiUpdates.Enqueue(() =>
            {
                if (_accounts.Any(existing => string.Equals(existing.AccountName, vm.AccountName, StringComparison.OrdinalIgnoreCase)))
                    return;

                _accounts.Add(vm);
                NoAccountsOverlay.Visibility = _accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            });
        }

        public void RemoveAccount(string accountName)
        {
            if (string.IsNullOrWhiteSpace(accountName))
                return;

            _pendingUiUpdates.Enqueue(() =>
            {
                AccountViewModel vm = _accounts.FirstOrDefault(account =>
                    string.Equals(account.AccountName, accountName, StringComparison.OrdinalIgnoreCase));

                if (vm != null)
                    _accounts.Remove(vm);

                NoAccountsOverlay.Visibility = _accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            });
        }

        public void UpdateAccount(string accountName, Action<AccountViewModel> updateAction)
        {
            if (string.IsNullOrWhiteSpace(accountName) || updateAction == null)
                return;

            _pendingUiUpdates.Enqueue(() =>
            {
                AccountViewModel vm = _accounts.FirstOrDefault(account =>
                    string.Equals(account.AccountName, accountName, StringComparison.OrdinalIgnoreCase));

                if (vm != null)
                    updateAction(vm);
            });
        }

        public void SetEngineStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
                return;

            _pendingUiUpdates.Enqueue(() => _engineStatus = status);
        }

        private void UiRefreshTimer_Tick(object sender, EventArgs e)
        {
            Action update;
            while (_pendingUiUpdates.TryDequeue(out update))
                update();

            foreach (AccountViewModel account in _accounts)
            {
                if (account.LockoutTimeRemaining.HasValue)
                {
                    TimeSpan remaining = account.LockoutTimeRemaining.Value - _uiRefreshTimer.Interval;
                    account.LockoutTimeRemaining = remaining > TimeSpan.Zero ? remaining : (TimeSpan?)null;
                }

                account.RefreshStatusColor();
            }

            EngineStatusText.Text = _engineStatus;
            FooterSummaryText.Text = string.Format("Accounts: {0}", _accounts.Count);
            NoAccountsOverlay.Visibility = _accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateMetricCards();

            UpdateNewsBanner();
            UpdateRecoveryAlert();
        }

        private void EmergencyFlattenAll_Click(object sender, RoutedEventArgs e)
        {
            if (_flattenProtocol == null)
                return;

            foreach (AccountViewModel account in _accounts.ToArray())
            {
                if (account.Status == AccountState.Locked
                    || account.Status == AccountState.Flattening
                    || account.Status == AccountState.NewsLocked
                    || account.Status == AccountState.Disconnected)
                {
                    continue;
                }

                _flattenProtocol.ExecuteFlatten(account.AccountName, "Emergency flatten all");
            }
        }

        private void AddActiveAccounts_Click(object sender, RoutedEventArgs e)
        {
            if (_addActiveAccountsAction != null)
                _addActiveAccountsAction();
        }

        public void ShowRecoveryAlert(string message, TimeSpan duration)
        {
            _pendingUiUpdates.Enqueue(() =>
            {
                RecoveryAlertText.Text = message;
                RecoveryAlertBorder.Visibility = Visibility.Visible;
                _recoveryAlertExpiresUtc = DateTime.UtcNow.Add(duration);
            });
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            AccountViewModel vm = button != null ? button.Tag as AccountViewModel : null;
            if (vm == null)
                return;

            AccountConfig config = GetConfigForAccount(vm.AccountName);
            SettingsFlyout flyout = new SettingsFlyout(config, OnSettingsSaved);
            flyout.RequestClose += CloseSettingsFlyout;
            SettingsContentHost.Content = flyout;
            SettingsOverlay.Visibility = Visibility.Visible;
        }

        private void RemoveAccount_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            AccountViewModel vm = button != null ? button.Tag as AccountViewModel : null;
            if (vm == null)
                return;

            AccountConfig config = GetConfigForAccount(vm.AccountName);
            config.IsExcluded = true;
            PersistConfig(config);

            if (_removeAccountAction != null)
                _removeAccountAction(vm.AccountName);

            RemoveAccount(vm.AccountName);
        }

        private void CloseSettings_Click(object sender, RoutedEventArgs e)
        {
            CloseSettingsFlyout();
        }

        private void ShadowMode_Click(object sender, RoutedEventArgs e)
        {
            ToggleButton toggle = sender as ToggleButton;
            AccountViewModel vm = toggle != null ? toggle.Tag as AccountViewModel : null;
            if (vm == null)
                return;

            string accountName = vm.AccountName;
            bool nextValue = !vm.IsShadowMode;
            AccountConfig config = GetConfigForAccount(accountName);
            config.ShadowModeEnabled = nextValue;
            PersistConfig(config);
            UpdateAccount(accountName, account => account.IsShadowMode = nextValue);
        }

        private AccountConfig GetConfigForAccount(string accountName)
        {
            AccountConfig config;
            if (_configsByAccount.TryGetValue(accountName, out config))
                return config;

            config = new AccountConfig
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
                HardEnforcementEnabled = true
            };
            _configsByAccount[accountName] = config;
            return config;
        }

        private void OnSettingsSaved(AccountConfig config)
        {
            _configsByAccount[config.AccountName] = config;
            PersistConfig(config);

            UpdateAccount(config.AccountName, account =>
            {
                account.IsShadowMode = config.ShadowModeEnabled;
                account.DailyLimitRemaining = config.DailyLossLimit;
                account.DailyTradeLimit = config.DailyTradeLimit;
            });

            if (config.IsExcluded)
            {
                if (_removeAccountAction != null)
                    _removeAccountAction(config.AccountName);

                RemoveAccount(config.AccountName);
                CloseSettingsFlyout();
            }
        }

        private void PersistConfig(AccountConfig config)
        {
            if (_accountMonitor != null)
                _accountMonitor.UpdateConfig(config);

            if (_persistenceService != null)
                _persistenceService.SaveConfig(_configsByAccount.Values.ToList());
        }

        private void CloseSettingsFlyout()
        {
            SettingsFlyout flyout = SettingsContentHost.Content as SettingsFlyout;
            if (flyout != null)
            {
                if (!flyout.SaveBeforeClose())
                    return;

                flyout.RequestClose -= CloseSettingsFlyout;
            }

            SettingsContentHost.Content = null;
            SettingsOverlay.Visibility = Visibility.Collapsed;
        }

        private void UpdateNewsBanner()
        {
            if (_newsEngine == null)
            {
                NewsBannerText.Text = "News: Engine offline";
                NewsBannerText.Foreground = System.Windows.Media.Brushes.Gray;
                NewsAffectedAccountsText.Text = string.Empty;
                return;
            }

            NewsWindowStatus status = _newsEngine.GetWindowStatus();
            NewsBannerText.Text = status.BannerText;
            NewsBannerText.Foreground = status.IsActive
                ? System.Windows.Media.Brushes.Red
                : System.Windows.Media.Brushes.LightGray;

            NewsAffectedAccountsText.Text = status.IsActive && !string.IsNullOrWhiteSpace(status.AffectedAccounts)
                ? "Affected: " + status.AffectedAccounts
                : string.Empty;

            MarketStatusText.Text = status.IsActive ? "News lockout active" : "News clear";
        }

        private void UpdateMetricCards()
        {
            AccountViewModel selected = AccountsGrid.SelectedItem as AccountViewModel;
            AccountViewModel source = selected ?? _accounts.FirstOrDefault();
            if (source == null)
            {
                PassProbabilityText.Text = "50%";
                PassProbabilityMessageText.Text = "Select an account to view trajectory.";
                TodayTradesText.Text = "Trades: 0/10";
                ActiveGuardsText.Text = "No active guards";
                return;
            }

            PassProbabilityText.Text = string.Format("{0:0}%", source.PassProbability);
            PassProbabilityMessageText.Text = source.RequiredDailyAverage > 0.0
                ? string.Format("Need +${0:0}/day average.", source.RequiredDailyAverage)
                : "Trajectory updates as data arrives.";
            TodayTradesText.Text = "Trades: " + source.TradesTodayDisplay;
            ActiveGuardsText.Text = string.IsNullOrWhiteSpace(source.ActiveGuards) ? "No active guards" : source.ActiveGuards;
        }

        private void UpdateRecoveryAlert()
        {
            if (RecoveryAlertBorder.Visibility == Visibility.Visible
                && _recoveryAlertExpiresUtc != DateTime.MinValue
                && DateTime.UtcNow >= _recoveryAlertExpiresUtc)
            {
                RecoveryAlertText.Text = string.Empty;
                RecoveryAlertBorder.Visibility = Visibility.Collapsed;
                _recoveryAlertExpiresUtc = DateTime.MinValue;
            }
        }

    }
}
