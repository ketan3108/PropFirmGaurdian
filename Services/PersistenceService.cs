using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Xml.Serialization;
using PropFirmGuardian.Models;
using PropFirmGuardian.Utils;

namespace PropFirmGuardian.Services
{
    public sealed class PersistenceService : IDisposable
    {
        private const int AutoSaveIntervalMs = 5000;
        private readonly object _fileLock;
        private readonly string _encryptionKey;
        private readonly Timer _autoSaveTimer;
        private Func<ConcurrentDictionary<string, SessionSnapshot>> _stateProvider;
        private bool _isDisposed;

        public PersistenceService()
        {
            _fileLock = new object();
            _encryptionKey = GetMachineKey();
            StateFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "NinjaTrader 8",
                "addons",
                "PropFirmGuardian",
                "State.enc");
            ConfigFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "NinjaTrader 8",
                "addons",
                "PropFirmGuardian",
                "Config.enc");
            _autoSaveTimer = new Timer(OnAutoSaveTimer, null, AutoSaveIntervalMs, AutoSaveIntervalMs);
        }

        public string StateFilePath { get; private set; }
        public string ConfigFilePath { get; private set; }

        public void SetAutoSaveStateProvider(Func<ConcurrentDictionary<string, SessionSnapshot>> stateProvider)
        {
            _stateProvider = stateProvider;
        }

        public void SaveState(ConcurrentDictionary<string, SessionSnapshot> states)
        {
            if (states == null)
                states = new ConcurrentDictionary<string, SessionSnapshot>(StringComparer.OrdinalIgnoreCase);

            int accountCount = states.Count;

            try
            {
                SessionSnapshotList list = new SessionSnapshotList
                {
                    Items = states.Values.ToList()
                };

                string xml = Serialize(list);
                WriteEncryptedAtomically(xml, StateFilePath);
                long fileSize = File.Exists(StateFilePath) ? new FileInfo(StateFilePath).Length : 0L;
                Debug.WriteLine(string.Format("[PERSIST] State saved: {0} accounts | {1} bytes", accountCount, fileSize));
            }
            catch (Exception exception)
            {
                Debug.WriteLine(string.Format("[PERSIST] SaveState failed: {0}", exception.Message));
            }
        }

        public ConcurrentDictionary<string, SessionSnapshot> LoadState()
        {
            ConcurrentDictionary<string, SessionSnapshot> states =
                new ConcurrentDictionary<string, SessionSnapshot>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(StateFilePath))
            {
                Debug.WriteLine("[PERSIST] No state file found, starting fresh");
                return states;
            }

            try
            {
                string xml = EncryptionHelper.DecryptFromFile(StateFilePath, _encryptionKey);
                SessionSnapshotList list = Deserialize<SessionSnapshotList>(xml);
                if (list == null || list.Items == null)
                {
                    Debug.WriteLine("[PERSIST] State loaded: 0 accounts from file");
                    return states;
                }

                foreach (SessionSnapshot snapshot in list.Items)
                {
                    if (snapshot != null && !string.IsNullOrWhiteSpace(snapshot.AccountName))
                        states[snapshot.AccountName] = snapshot;
                }

                Debug.WriteLine(string.Format("[PERSIST] State loaded: {0} accounts from file", states.Count));
            }
            catch (Exception exception)
            {
                Debug.WriteLine(string.Format("[PERSIST] Decrypt failed, starting fresh: {0}", exception.Message));
            }

            return states;
        }

        public void SaveConfig(List<AccountConfig> configs)
        {
            if (configs == null)
                configs = new List<AccountConfig>();

            try
            {
                AccountConfigList list = new AccountConfigList
                {
                    Items = configs
                };

                string xml = Serialize(list);
                WriteEncryptedAtomically(xml, ConfigFilePath);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(string.Format("[PropFirmGuardian] SaveConfig failed: {0}", exception.Message));
            }
        }

        public List<AccountConfig> LoadConfig()
        {
            if (!File.Exists(ConfigFilePath))
                return new List<AccountConfig>();

            try
            {
                string xml = EncryptionHelper.DecryptFromFile(ConfigFilePath, _encryptionKey);
                AccountConfigList list = Deserialize<AccountConfigList>(xml);
                return list != null && list.Items != null ? list.Items : new List<AccountConfig>();
            }
            catch (Exception exception)
            {
                Debug.WriteLine(string.Format("[PropFirmGuardian] LoadConfig failed; encrypted config may be missing or corrupt: {0}", exception.Message));
                return new List<AccountConfig>();
            }
        }

        public void SaveStateImmediately(ConcurrentDictionary<string, SessionSnapshot> states)
        {
            Debug.WriteLine("[PERSIST] Immediate state save requested");
            SaveState(states);
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _autoSaveTimer.Dispose();
        }

        private void OnAutoSaveTimer(object state)
        {
            if (_isDisposed || _stateProvider == null)
                return;

            try
            {
                SaveState(_stateProvider());
            }
            catch (Exception exception)
            {
                Debug.WriteLine(string.Format("[PropFirmGuardian] Auto-save failed: {0}", exception.Message));
            }
        }

        private void WriteEncryptedAtomically(string xml, string finalPath)
        {
            lock (_fileLock)
            {
                string directory = Path.GetDirectoryName(finalPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                byte[] encrypted = EncryptionHelper.Encrypt(xml, _encryptionKey);
                string tempPath = finalPath + ".tmp";
                File.WriteAllBytes(tempPath, encrypted);

                if (File.Exists(finalPath))
                {
                    string backupPath = finalPath + ".bak";
                    File.Replace(tempPath, finalPath, backupPath, true);
                }
                else
                {
                    File.Move(tempPath, finalPath);
                }
            }
        }

        private static string Serialize<T>(T value)
        {
            XmlSerializer serializer = new XmlSerializer(typeof(T));
            using (StringWriter writer = new StringWriter())
            {
                serializer.Serialize(writer, value);
                return writer.ToString();
            }
        }

        private static T Deserialize<T>(string xml)
        {
            XmlSerializer serializer = new XmlSerializer(typeof(T));
            using (StringReader reader = new StringReader(xml))
                return (T)serializer.Deserialize(reader);
        }

        private static string GetMachineKey()
        {
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT UUID FROM Win32_ComputerSystemProduct"))
                {
                    foreach (ManagementObject item in searcher.Get())
                    {
                        object uuid = item["UUID"];
                        if (uuid != null && !string.IsNullOrWhiteSpace(uuid.ToString()))
                            return Environment.MachineName + "|" + uuid;
                    }
                }
            }
            catch
            {
            }

            return Environment.MachineName + "|" + Environment.UserName;
        }

        [Serializable]
        public sealed class SessionSnapshotList
        {
            public List<SessionSnapshot> Items { get; set; }
        }

        [Serializable]
        public sealed class AccountConfigList
        {
            public List<AccountConfig> Items { get; set; }
        }
    }
}
