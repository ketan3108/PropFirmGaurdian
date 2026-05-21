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
            DailyLossLimitTextBox.Text = config.DailyLossLimit.ToString(CultureInfo.InvariantCulture);
            TrailingDrawdownTextBox.Text = config.TrailingDrawdown.ToString(CultureInfo.InvariantCulture);
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

            if (profile.DailyLossLimit.HasValue)
                DailyLossLimitTextBox.Text = profile.DailyLossLimit.Value.ToString(CultureInfo.InvariantCulture);

            if (profile.TrailingDrawdownAmount.HasValue)
                TrailingDrawdownTextBox.Text = profile.TrailingDrawdownAmount.Value.ToString(CultureInfo.InvariantCulture);

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
            double maxPositionSize;
            double safetyBuffer;
            int dailyTradeLimit;
            int emergencyOverrideTrades;

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
                DailyLossLimit = dailyLossLimit,
                TrailingDrawdown = trailingDrawdown,
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
