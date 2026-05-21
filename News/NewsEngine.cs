using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using PropFirmGuardian.Core;
using PropFirmGuardian.Models;
using PropFirmGuardian.Utils;

namespace PropFirmGuardian.News
{
    public sealed class NewsEngine : IDisposable
    {
        private const int TimerIntervalMs = 1000;
        private const string CalendarUrl = "https://nfs.faireconomy.media/ff_calendar_thisweek.json";
        private readonly AccountMonitor _accountMonitor;
        private readonly FlattenProtocol _flattenProtocol;
        private readonly DeviationAnalyzer _deviationAnalyzer;
        private readonly object _newsLock;
        private readonly Timer _newsTimer;
        private readonly HttpClient _httpClient;
        private List<NewsEvent> _cachedEvents;
        private HashSet<DateTime> _activeNewsWindows;
        private HashSet<DateTime> _previousActiveNewsWindows;
        private List<string> _lastAffectedAccounts;
        private DateTime _nextCalendarRefreshUtc;
        private int _isChecking;
        private bool _isDisposed;

        public NewsEngine(AccountMonitor accountMonitor)
        {
            if (accountMonitor == null)
                throw new ArgumentNullException("accountMonitor");

            _accountMonitor = accountMonitor;
            _flattenProtocol = new FlattenProtocol(accountMonitor);
            _deviationAnalyzer = new DeviationAnalyzer();
            _newsLock = new object();
            _cachedEvents = new List<NewsEvent>();
            _activeNewsWindows = new HashSet<DateTime>();
            _previousActiveNewsWindows = new HashSet<DateTime>();
            _lastAffectedAccounts = new List<string>();
            _nextCalendarRefreshUtc = TimeZoneHelper.GetNextMidnightEt();
            _httpClient = new HttpClient();

            LoadCalendar();
            _newsTimer = new Timer(OnNewsTimerTick, null, TimerIntervalMs, TimerIntervalMs);
        }

        public event Action<IReadOnlyCollection<NewsEvent>> OnNewsWindowOpened;
        public event Action OnNewsWindowClosed;
        public event Action<string, NewsEvent> OnNewsProtectionTriggered;

        public NewsWindowStatus GetWindowStatus()
        {
            lock (_newsLock)
            {
                DateTime nowUtc = DateTime.UtcNow;
                List<NewsEvent> activeEvents = _cachedEvents
                    .Where(item => _activeNewsWindows.Contains(item.EventTimeUtc))
                    .OrderBy(item => item.EventTimeUtc)
                    .ToList();

                if (activeEvents.Count == 0)
                {
                    return new NewsWindowStatus
                    {
                        IsActive = false,
                        BannerText = "News: Clear",
                        TimeRemaining = TimeSpan.Zero,
                        AffectedAccounts = string.Empty
                    };
                }

                DateTime latestClose = activeEvents.Max(item => item.EventTimeUtc.AddMinutes(3));
                TimeSpan remaining = latestClose - nowUtc;
                if (remaining < TimeSpan.Zero)
                    remaining = TimeSpan.Zero;

                return new NewsWindowStatus
                {
                    IsActive = true,
                    BannerText = string.Format("News lockout active - {0:mm\\:ss} remaining", remaining),
                    TimeRemaining = remaining,
                    AffectedAccounts = string.Join(", ", _lastAffectedAccounts.ToArray())
                };
            }
        }

        public void LoadCalendar()
        {
            try
            {
                string json = _httpClient.GetStringAsync(CalendarUrl).GetAwaiter().GetResult();
                JArray rows = JArray.Parse(json);
                List<NewsEvent> events = new List<NewsEvent>();

                foreach (JToken row in rows)
                {
                    NewsEvent newsEvent = ParseNewsEvent(row);
                    if (newsEvent == null)
                        continue;

                    if (!string.Equals(newsEvent.Impact, "High", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!string.Equals(newsEvent.Currency, "USD", StringComparison.OrdinalIgnoreCase))
                        continue;

                    events.Add(newsEvent);
                }

                lock (_newsLock)
                {
                    _cachedEvents = events.OrderBy(item => item.EventTimeUtc).ToList();
                    _nextCalendarRefreshUtc = TimeZoneHelper.GetNextMidnightEt();
                }

                Debug.WriteLine(string.Format("[PropFirmGuardian] Loaded {0} high-impact USD news events.", events.Count));
            }
            catch (Exception exception)
            {
                Debug.WriteLine(string.Format("[PropFirmGuardian] News calendar load failed: {0}", exception.Message));
                lock (_newsLock)
                    _nextCalendarRefreshUtc = DateTime.UtcNow.AddMinutes(15);
            }
        }

        public void CheckNewsWindows()
        {
            if (_isDisposed)
                return;

            if (DateTime.UtcNow >= _nextCalendarRefreshUtc)
                LoadCalendar();

            List<NewsEvent> openedEvents = null;
            bool closed = false;

            lock (_newsLock)
            {
                int previousCount = _activeNewsWindows.Count;
                HashSet<DateTime> active = new HashSet<DateTime>();
                DateTime nowUtc = DateTime.UtcNow;

                foreach (NewsEvent newsEvent in _cachedEvents)
                {
                    int openOffsetMinutes = _deviationAnalyzer.ShouldFlattenEarly(newsEvent) ? -5 : -2;
                    DateTime openUtc = newsEvent.EventTimeUtc.AddMinutes(openOffsetMinutes);
                    DateTime closeUtc = newsEvent.EventTimeUtc.AddMinutes(3);

                    if (nowUtc >= openUtc && nowUtc <= closeUtc)
                        active.Add(newsEvent.EventTimeUtc);
                }

                Debug.WriteLine(string.Format("[NEWS] Checking windows: active count = {0}", active.Count));

                if (active.Count > 1)
                    Debug.WriteLine(string.Format("[NEWS] Overlap detected: {0} active windows", active.Count));

                _activeNewsWindows = active;

                if (_activeNewsWindows.Count > 0 && previousCount == 0)
                {
                    openedEvents = _cachedEvents
                        .Where(item => _activeNewsWindows.Contains(item.EventTimeUtc))
                        .ToList();
                }
                else if (_activeNewsWindows.Count == 0 && previousCount > 0)
                {
                    closed = true;
                }

                if (closed)
                {
                    foreach (NewsEvent newsEvent in _cachedEvents)
                    {
                        if (_previousActiveNewsWindows.Contains(newsEvent.EventTimeUtc))
                            Debug.WriteLine(string.Format("[NEWS] Window CLOSED: {0}", newsEvent.Title));
                    }
                }

                _previousActiveNewsWindows = new HashSet<DateTime>(_activeNewsWindows);
            }

            if (openedEvents != null)
                HandleNewsWindowOpened(openedEvents);

            if (closed)
                HandleNewsWindowClosed();
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            _newsTimer.Dispose();
            _httpClient.Dispose();
        }

        private void OnNewsTimerTick(object state)
        {
            if (Interlocked.Exchange(ref _isChecking, 1) == 1)
                return;

            try
            {
                CheckNewsWindows();
            }
            finally
            {
                Interlocked.Exchange(ref _isChecking, 0);
            }
        }

        private void HandleNewsWindowOpened(IReadOnlyCollection<NewsEvent> activeEvents)
        {
            foreach (NewsEvent newsEvent in activeEvents)
                Debug.WriteLine(string.Format("[NEWS] Window OPENING: {0} at {1:O}", newsEvent.Title, newsEvent.EventTimeUtc));

            List<string> affectedAccounts = new List<string>();

            foreach (AccountMonitor.AccountMonitorState accountState in _accountMonitor.GetAccountStates())
            {
                if (accountState == null || accountState.Config == null || accountState.Snapshot == null)
                    continue;

                NewsEvent firstEvent = activeEvents.FirstOrDefault();
                string accountName = accountState.Snapshot.AccountName;

                if (accountState.Config.IsExcluded)
                {
                    Debug.WriteLine(string.Format("[NEWS] Account {0} excluded - skipping", accountName));
                    continue;
                }

                if (accountState.Config.IsLivePA)
                {
                    Debug.WriteLine(string.Format("[NEWS] Account {0} is PA - FLATTENING", accountName));
                    lock (accountState.LockObject)
                    {
                        accountState.Snapshot.NewsLockoutActive = true;
                        accountState.Snapshot.LastUpdateTime = DateTime.UtcNow;
                    }

                    affectedAccounts.Add(accountName);
                    _flattenProtocol.ExecuteFlatten(accountName, "High-impact USD news protection");
                }
                else if (accountState.Config.IsEval)
                {
                    Debug.WriteLine(string.Format("[NEWS] Account {0} is Eval - BYPASSING", accountName));
                }
                else
                {
                    lock (accountState.LockObject)
                    {
                        accountState.Snapshot.NewsLockoutActive = true;
                        accountState.Snapshot.LastKnownStatus = AccountState.NewsLocked;
                        accountState.Snapshot.LastUpdateTime = DateTime.UtcNow;
                    }
                    affectedAccounts.Add(accountName);
                }

                Action<string, NewsEvent> triggered = OnNewsProtectionTriggered;
                if (triggered != null)
                    triggered(accountName, firstEvent);
            }

            lock (_newsLock)
                _lastAffectedAccounts = affectedAccounts;

            Action<IReadOnlyCollection<NewsEvent>> opened = OnNewsWindowOpened;
            if (opened != null)
                opened(activeEvents);
        }

        private void HandleNewsWindowClosed()
        {
            foreach (AccountMonitor.AccountMonitorState accountState in _accountMonitor.GetAccountStates())
            {
                if (accountState == null || accountState.Snapshot == null)
                    continue;

                lock (accountState.LockObject)
                {
                    accountState.Snapshot.NewsLockoutActive = false;
                    accountState.Snapshot.LastUpdateTime = DateTime.UtcNow;

                    bool lockoutExpired = !accountState.Snapshot.LockedUntil.HasValue
                        || accountState.Snapshot.LockedUntil.Value <= DateTime.Now;

                    if (accountState.Snapshot.LastKnownStatus == AccountState.NewsLocked && lockoutExpired)
                    {
                        accountState.Snapshot.LastKnownStatus = AccountState.Active;
                        accountState.Snapshot.LockReason = string.Empty;
                    }
                    else if (accountState.Snapshot.LastKnownStatus == AccountState.Locked && lockoutExpired)
                    {
                        accountState.Snapshot.LastKnownStatus = AccountState.Active;
                        accountState.Snapshot.LockReason = string.Empty;
                        accountState.Snapshot.LockedUntil = null;
                    }
                }
            }

            Action closed = OnNewsWindowClosed;
            if (closed != null)
                closed();
        }

        private NewsEvent ParseNewsEvent(JToken row)
        {
            string impact = ReadString(row, "impact");
            string currency = ReadString(row, "currency");
            string title = ReadString(row, "title");
            string dateText = ReadString(row, "date");

            DateTime eventTimeUtc;
            if (!TryParseEventTimeUtc(dateText, out eventTimeUtc))
                return null;

            NewsEvent newsEvent = new NewsEvent
            {
                EventTimeUtc = eventTimeUtc,
                Title = title,
                Currency = currency,
                Impact = impact,
                Forecast = ReadNullableDouble(row, "forecast"),
                Previous = ReadNullableDouble(row, "previous"),
                Actual = ReadNullableDouble(row, "actual")
            };

            _deviationAnalyzer.AnalyzeEvent(newsEvent);
            return newsEvent;
        }

        private static bool TryParseEventTimeUtc(string dateText, out DateTime eventTimeUtc)
        {
            eventTimeUtc = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(dateText))
                return false;

            DateTime parsed;
            if (!DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
                return false;

            if (parsed.Kind == DateTimeKind.Utc)
            {
                eventTimeUtc = parsed;
                return true;
            }

            eventTimeUtc = TimeZoneHelper.ToUtc(parsed);
            return true;
        }

        private static string ReadString(JToken row, string propertyName)
        {
            JToken value = row[propertyName];
            return value != null ? value.ToString() : string.Empty;
        }

        private static double? ReadNullableDouble(JToken row, string propertyName)
        {
            string raw = ReadString(row, propertyName);
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            raw = raw.Replace("%", string.Empty).Replace(",", string.Empty).Trim();

            double value;
            if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                return value;

            return null;
        }
    }

    public sealed class NewsWindowStatus
    {
        public bool IsActive { get; set; }
        public string BannerText { get; set; }
        public TimeSpan TimeRemaining { get; set; }
        public string AffectedAccounts { get; set; }
    }
}
