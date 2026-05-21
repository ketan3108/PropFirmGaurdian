using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using PropFirmGuardian.Utils;

namespace PropFirmGuardian.Services
{
    public sealed class LicenseService
    {
        private const string PurchaseUrl = "https://propfirmguardian.com/purchase";
        private const string HardcodedTestHash = "eec141684458b698c6988529b9ccf1f5af6afffbb28a6f7945782c090f43f603";
        private readonly object _licenseLock;
        private readonly string _licenseFilePath;

        public LicenseService()
        {
            _licenseLock = new object();
            MachineId = ResolveMachineId();
            _licenseFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "NinjaTrader 8",
                "addons",
                "PropFirmGuardian",
                "License.enc");
            LicenseKey = LoadStoredLicenseKey();
            LastValidationTime = DateTime.MinValue;
            IsValidated = false;
        }

        public string MachineId { get; private set; }
        public string LicenseKey { get; private set; }
        public DateTime LastValidationTime { get; private set; }
        public bool IsValidated { get; private set; }
        public const int GracePeriodHours = 72;

        public Task StartValidationAsync()
        {
            return Task.Run(() =>
            {
                bool valid = ValidateLicense();
                if (!valid && !CheckGracePeriod())
                    ShowLicenseRequiredDialog();
            });
        }

        public void SetLicenseKey(string licenseKey)
        {
            lock (_licenseLock)
            {
                LicenseKey = licenseKey ?? string.Empty;
                SaveStoredLicenseKey();
            }
        }

        public bool ValidateLicense()
        {
            lock (_licenseLock)
            {
                if (string.IsNullOrWhiteSpace(LicenseKey))
                {
                    IsValidated = false;
                    return false;
                }

                try
                {
                    string hash = ComputeLicenseHash();

                    // V1 local stub: the hash is checked against a hardcoded test value.
                    // Week 6 production validation can POST this payload to Firebase; if
                    // that server is down, CheckGracePeriod keeps already-valid users alive.
                    if (string.Equals(hash, HardcodedTestHash, StringComparison.OrdinalIgnoreCase))
                    {
                        IsValidated = true;
                        LastValidationTime = DateTime.Now;
                        SaveStoredLicenseKey();
                        return true;
                    }

                    IsValidated = false;
                    return false;
                }
                catch (Exception exception)
                {
                    IsValidated = false;
                    Debug.WriteLine(string.Format("[PropFirmGuardian] License validation failed locally: {0}", exception.Message));
                    return false;
                }
            }
        }

        public bool CheckGracePeriod()
        {
            lock (_licenseLock)
            {
                if (IsValidated)
                    return true;

                if (LastValidationTime == DateTime.MinValue)
                    return false;

                return DateTime.Now - LastValidationTime < TimeSpan.FromHours(GracePeriodHours);
            }
        }

        public string GetLicenseStatus()
        {
            lock (_licenseLock)
            {
                if (IsValidated)
                    return "Valid";

                if (LastValidationTime != DateTime.MinValue)
                {
                    TimeSpan remaining = TimeSpan.FromHours(GracePeriodHours) - (DateTime.Now - LastValidationTime);
                    if (remaining > TimeSpan.Zero)
                        return string.Format("Grace Period ({0} hours remaining)", Math.Ceiling(remaining.TotalHours));
                }

                return "Expired";
            }
        }

        public string ComputeLicenseHash()
        {
            string material = MachineId + Environment.UserName + (LicenseKey ?? string.Empty);
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(material));
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                foreach (byte item in hash)
                    builder.Append(item.ToString("x2"));

                return builder.ToString();
            }
        }

        private void ShowLicenseRequiredDialog()
        {
            ThreadSafeDispatcher.SafeInvoke(() =>
            {
                MessageBox.Show(
                    "Please enter license key or purchase at " + PurchaseUrl,
                    "Prop Firm Guardian License Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            });
        }

        private string LoadStoredLicenseKey()
        {
            try
            {
                if (!File.Exists(_licenseFilePath))
                    return string.Empty;

                return EncryptionHelper.DecryptFromFile(_licenseFilePath, MachineId);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(string.Format("[PropFirmGuardian] Unable to load stored license key: {0}", exception.Message));
                return string.Empty;
            }
        }

        private void SaveStoredLicenseKey()
        {
            try
            {
                string directory = Path.GetDirectoryName(_licenseFilePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                EncryptionHelper.EncryptToFile(LicenseKey ?? string.Empty, _licenseFilePath, MachineId);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(string.Format("[PropFirmGuardian] Unable to save license key: {0}", exception.Message));
            }
        }

        private static string ResolveMachineId()
        {
            try
            {
                Type globalsType = typeof(NinjaTrader.Core.Globals);
                PropertyInfo machineIdProperty = globalsType.GetProperty("MachineId", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (machineIdProperty != null)
                {
                    object value = machineIdProperty.GetValue(null, null);
                    if (value != null && !string.IsNullOrWhiteSpace(value.ToString()))
                        return value.ToString();
                }

                PropertyInfo machineNameProperty = globalsType.GetProperty("MachineName", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (machineNameProperty != null)
                {
                    object value = machineNameProperty.GetValue(null, null);
                    if (value != null && !string.IsNullOrWhiteSpace(value.ToString()))
                        return value.ToString();
                }
            }
            catch (Exception exception)
            {
                Debug.WriteLine(string.Format("[PropFirmGuardian] Unable to read NT8 machine id: {0}", exception.Message));
            }

            if (!string.IsNullOrWhiteSpace(Environment.MachineName))
                return Environment.MachineName;

            return Environment.UserName;
        }
    }
}
