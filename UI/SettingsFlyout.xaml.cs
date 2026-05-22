using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using PropFirmGuardian.Intelligence;
using PropFirmGuardian.Models;

namespace PropFirmGuardian.UI
{
    public partial class SettingsFlyout : UserControl
    {
        private readonly AccountConfig _sourceConfig;
        private readonly Action<AccountConfig> _onSave;
        private bool _isPopulating;
        private bool _saveOnRequestClose;

        public SettingsFlyout(AccountConfig config, Action<AccountConfig> onSave)
        {
            if (config == null)
                throw new ArgumentNullException("config");

            _sourceConfig = config;
            _onSave = onSave;
            _saveOnRequestClose = true;

            InitializeComponent();
            PopulatePresetList();
            PopulateFields(config);
        }

        public event Action RequestClose;

        private void PopulatePresetList()
        {
            PresetComboBox.ItemsSource = PropFirmPresets.GetAllPresetNames();
        }

        private void PopulateFields(AccountConfig config)
        {
            _isPopulating = true;

            AccountNameTextBox.Text = config.AccountName ?? string.Empty;
            PresetComboBox.SelectedItem = string.IsNullOrWhiteSpace(config.PropFirmName) ? "Custom" : config.PropFirmName;
            SelectComboText(ProgramTypeComboBox, string.IsNullOrWhiteSpace(config.ProgramType) ? "Eval" : config.ProgramType);
            AccountSizeTextBox.Text = config.AccountSize.ToString(CultureInfo.InvariantCulture);
            DailyLossLimitTextBox.Text = config.DailyLossLimit.ToString(CultureInfo.InvariantCulture);
            TrailingDrawdownTextBox.Text = config.TrailingDrawdown.ToString(CultureInfo.InvariantCulture);
            StaticMaxLossTextBox.Text = config.StaticMaxLoss.ToString(CultureInfo.InvariantCulture);
            ProfitTargetTextBox.Text = config.ProfitTarget.ToString(CultureInfo.InvariantCulture);
            ConsistencyThresholdTextBox.Text = config.ConsistencyThreshold.ToString(CultureInfo.InvariantCulture);
            RequiredTradingDaysTextBox.Text = config.RequiredTradingDays.ToString(CultureInfo.InvariantCulture);
            MaxPositionSizeTextBox.Text = config.MaxPositionSize.ToString(CultureInfo.InvariantCulture);
            EnableDailyLimitCheckBox.IsChecked = config.EnableDailyLimit;
            DailyTradeLimitTextBox.Text = config.DailyTradeLimit.ToString(CultureInfo.InvariantCulture);
            AllowEmergencyOverrideCheckBox.IsChecked = config.AllowEmergencyOverride;
            EmergencyOverrideTradesTextBox.Text = config.EmergencyOverrideTrades.ToString(CultureInfo.InvariantCulture);
            SafetyBufferTextBox.Text = config.SafetyBuffer.ToString(CultureInfo.InvariantCulture);
            IsLivePaCheckBox.IsChecked = config.IsLivePA;
            IsEvalCheckBox.IsChecked = config.IsEval;
            IsExcludedCheckBox.IsChecked = config.IsExcluded;
            ShadowModeCheckBox.IsChecked = config.ShadowModeEnabled;
            HardEnforcementCheckBox.IsChecked = config.HardEnforcementEnabled;
            EndOfSessionGuardCheckBox.IsChecked = config.EndOfSessionGuardEnabled;
            WeekendGuardCheckBox.IsChecked = config.WeekendGuardEnabled;
            OvernightGuardCheckBox.IsChecked = config.OvernightGuardEnabled;
            PreMarketGuardCheckBox.IsChecked = config.PreMarketGuardEnabled;
            SelectSessionReset(config.SessionResetTime);

            _isPopulating = false;
        }

        private void PresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isPopulating || PresetComboBox.SelectedItem == null)
                return;

            PropFirmProfile profile = PropFirmPresets.GetPreset(PresetComboBox.SelectedItem.ToString());
            AccountSizeTextBox.Text = profile.AccountSize.ToString(CultureInfo.InvariantCulture);
            SelectComboText(ProgramTypeComboBox, profile.ProgramType);

            if (profile.DailyLossLimit.HasValue)
                DailyLossLimitTextBox.Text = profile.DailyLossLimit.Value.ToString(CultureInfo.InvariantCulture);

            if (profile.TrailingDrawdownAmount.HasValue)
                TrailingDrawdownTextBox.Text = profile.TrailingDrawdownAmount.Value.ToString(CultureInfo.InvariantCulture);

            StaticMaxLossTextBox.Text = profile.StaticMaxLoss.HasValue ? profile.StaticMaxLoss.Value.ToString(CultureInfo.InvariantCulture) : "0";
            ProfitTargetTextBox.Text = profile.ProfitTarget.HasValue ? profile.ProfitTarget.Value.ToString(CultureInfo.InvariantCulture) : "0";
            ConsistencyThresholdTextBox.Text = profile.ConsistencyThreshold.ToString(CultureInfo.InvariantCulture);
            RequiredTradingDaysTextBox.Text = profile.RequiredTradingDays.ToString(CultureInfo.InvariantCulture);
            DailyTradeLimitTextBox.Text = profile.DailyTradeLimit.ToString(CultureInfo.InvariantCulture);

            if (profile.MaxPositionSize.HasValue)
                MaxPositionSizeTextBox.Text = profile.MaxPositionSize.Value.ToString(CultureInfo.InvariantCulture);

            SelectSessionReset(profile.SessionResetTime);
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!SaveCurrentSettings())
                return;

            _saveOnRequestClose = false;
            Close();
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrentSettings();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _saveOnRequestClose = false;
            Close();
        }

        public bool SaveBeforeClose()
        {
            if (!_saveOnRequestClose)
                return true;

            return SaveCurrentSettings();
        }

        private bool SaveCurrentSettings()
        {
            AccountConfig config;
            string validationError;
            if (!TryBuildConfig(out config, out validationError))
            {
                ValidationTextBlock.Text = validationError;
                return false;
            }

            if (_onSave != null)
                _onSave(config);

            ValidationTextBlock.Foreground = System.Windows.Media.Brushes.LightGreen;
            ValidationTextBlock.Text = "Settings saved.";
            return true;
        }

        private bool TryBuildConfig(out AccountConfig config, out string validationError)
        {
            config = null;
            validationError = string.Empty;

            double dailyLossLimit;
            double trailingDrawdown;
            double staticMaxLoss;
            double profitTarget;
            double accountSize;
            double consistencyThreshold;
            double maxPositionSize;
            double safetyBuffer;
            int dailyTradeLimit;
            int emergencyOverrideTrades;
            int requiredTradingDays;

            if (!double.TryParse(DailyLossLimitTextBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out dailyLossLimit) || dailyLossLimit < 0.0)
            {
                validationError = "Daily Loss Limit must be 0 or greater.";
                return false;
            }

            if (!double.TryParse(TrailingDrawdownTextBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out trailingDrawdown) || trailingDrawdown < 0.0)
            {
                validationError = "Trailing Drawdown must be 0 or greater.";
                return false;
            }

            if (!double.TryParse(AccountSizeTextBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out accountSize) || accountSize < 0.0)
            {
                validationError = "Account Size must be 0 or greater.";
                return false;
            }

            if (!double.TryParse(StaticMaxLossTextBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out staticMaxLoss) || staticMaxLoss < 0.0)
            {
                validationError = "Static Max Loss must be 0 or greater.";
                return false;
            }

            if (!double.TryParse(ProfitTargetTextBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out profitTarget) || profitTarget < 0.0)
            {
                validationError = "Profit Target must be 0 or greater.";
                return false;
            }

            if (!double.TryParse(ConsistencyThresholdTextBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out consistencyThreshold) || consistencyThreshold < 0.0 || consistencyThreshold > 1.0)
            {
                validationError = "Consistency Threshold must be between 0.0 and 1.0.";
                return false;
            }

            if (!int.TryParse(RequiredTradingDaysTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out requiredTradingDays) || requiredTradingDays < 1 || requiredTradingDays > 60)
            {
                validationError = "Required Trading Days must be between 1 and 60.";
                return false;
            }

            if (!double.TryParse(MaxPositionSizeTextBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out maxPositionSize) || maxPositionSize < 0.0)
            {
                validationError = "Max Position Size must be 0 or greater.";
                return false;
            }

            if (!double.TryParse(SafetyBufferTextBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out safetyBuffer) || safetyBuffer < 0.0)
            {
                validationError = "Safety Buffer must be 0 or greater.";
                return false;
            }

            if (!int.TryParse(DailyTradeLimitTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out dailyTradeLimit) || dailyTradeLimit < 3 || dailyTradeLimit > 50)
            {
                validationError = "Daily Trade Limit must be between 3 and 50.";
                return false;
            }

            if (!int.TryParse(EmergencyOverrideTradesTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out emergencyOverrideTrades) || emergencyOverrideTrades < 0 || emergencyOverrideTrades > 5)
            {
                validationError = "Emergency Override Trades must be between 0 and 5.";
                return false;
            }

            config = new AccountConfig
            {
                AccountName = _sourceConfig.AccountName,
                PropFirmName = PresetComboBox.SelectedItem != null ? PresetComboBox.SelectedItem.ToString() : "Custom",
                ProgramType = GetComboText(ProgramTypeComboBox),
                AccountSize = accountSize,
                DailyLossLimit = dailyLossLimit,
                TrailingDrawdown = trailingDrawdown,
                StaticMaxLoss = staticMaxLoss,
                ProfitTarget = profitTarget,
                ConsistencyThreshold = consistencyThreshold,
                RequiredTradingDays = requiredTradingDays,
                MaxPositionSize = maxPositionSize,
                SafetyBuffer = safetyBuffer,
                IsLivePA = IsLivePaCheckBox.IsChecked == true,
                IsEval = IsEvalCheckBox.IsChecked == true,
                SessionResetTime = ParseSelectedResetTime(),
                IsExcluded = IsExcludedCheckBox.IsChecked == true,
                ShadowModeEnabled = ShadowModeCheckBox.IsChecked == true,
                HardEnforcementEnabled = HardEnforcementCheckBox.IsChecked == true,
                EnableDailyLimit = EnableDailyLimitCheckBox.IsChecked == true,
                DailyTradeLimit = dailyTradeLimit,
                AllowEmergencyOverride = AllowEmergencyOverrideCheckBox.IsChecked == true,
                EmergencyOverrideTrades = emergencyOverrideTrades,
                EndOfSessionGuardEnabled = EndOfSessionGuardCheckBox.IsChecked == true,
                WeekendGuardEnabled = WeekendGuardCheckBox.IsChecked == true,
                OvernightGuardEnabled = OvernightGuardCheckBox.IsChecked == true,
                PreMarketGuardEnabled = PreMarketGuardCheckBox.IsChecked == true
            };

            return true;
        }

        private void SelectSessionReset(TimeSpan resetTime)
        {
            string target = resetTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
            foreach (object item in SessionResetComboBox.Items)
            {
                ComboBoxItem comboBoxItem = item as ComboBoxItem;
                if (comboBoxItem != null && string.Equals(comboBoxItem.Content.ToString(), target, StringComparison.OrdinalIgnoreCase))
                {
                    SessionResetComboBox.SelectedItem = comboBoxItem;
                    return;
                }
            }

            SessionResetComboBox.Text = target;
        }

        private static void SelectComboText(ComboBox comboBox, string value)
        {
            foreach (object item in comboBox.Items)
            {
                ComboBoxItem comboBoxItem = item as ComboBoxItem;
                if (comboBoxItem != null && string.Equals(comboBoxItem.Content.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedItem = comboBoxItem;
                    return;
                }
            }

            comboBox.Text = value ?? string.Empty;
        }

        private static string GetComboText(ComboBox comboBox)
        {
            ComboBoxItem selectedItem = comboBox.SelectedItem as ComboBoxItem;
            return selectedItem != null ? selectedItem.Content.ToString() : comboBox.Text;
        }

        private TimeSpan ParseSelectedResetTime()
        {
            ComboBoxItem selectedItem = SessionResetComboBox.SelectedItem as ComboBoxItem;
            string value = selectedItem != null ? selectedItem.Content.ToString() : SessionResetComboBox.Text;

            TimeSpan resetTime;
            if (TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out resetTime))
                return resetTime;

            return TimeSpan.Zero;
        }

        private void Close()
        {
            Action handler = RequestClose;
            if (handler != null)
                handler();
        }
    }
}
