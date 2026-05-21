using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Media;
using System.Speech.Synthesis;
using PropFirmGuardian.Utils;

namespace PropFirmGuardian.Services
{
    public enum AlertPriority
    {
        Info,
        Warning,
        Critical
    }

    public sealed class AudioAlertService : IDisposable
    {
        private readonly Queue<string> _infoQueue;
        private SpeechSynthesizer _synthesizer;
        private bool _isAvailable;
        private bool _isEnabled;
        private bool _isSpeakingInfo;
        private int _volume;

        public AudioAlertService()
        {
            _infoQueue = new Queue<string>();
            _isEnabled = true;
            _volume = 80;

            try
            {
                _synthesizer = new SpeechSynthesizer();
                _synthesizer.SetOutputToDefaultAudioDevice();
                _synthesizer.Volume = _volume;
                _synthesizer.Rate = 0;
                _synthesizer.SpeakCompleted += OnSpeakCompleted;
                _isAvailable = true;
            }
            catch (Exception exception)
            {
                _isAvailable = false;
                Debug.WriteLine(string.Format("[PropFirmGuardian] TTS unavailable; audio alerts will use beep fallback: {0}", exception.Message));
            }
        }

        public bool IsEnabled
        {
            get { return _isEnabled; }
            set { _isEnabled = value; }
        }

        public int Volume
        {
            get { return _volume; }
            set
            {
                _volume = Math.Max(0, Math.Min(100, value));
                if (_synthesizer != null)
                    _synthesizer.Volume = _volume;
            }
        }

        public bool IsAvailable
        {
            get { return _isAvailable; }
        }

        public void Speak(string message, AlertPriority priority)
        {
            if (!_isEnabled || string.IsNullOrWhiteSpace(message))
                return;

            if (!_isAvailable || _synthesizer == null)
            {
                if (priority == AlertPriority.Warning || priority == AlertPriority.Critical)
                    PlayBeep();
                return;
            }

            ThreadSafeDispatcher.SafeInvoke(() =>
            {
                try
                {
                    if (priority == AlertPriority.Warning || priority == AlertPriority.Critical)
                    {
                        _synthesizer.SpeakAsyncCancelAll();
                        _isSpeakingInfo = false;
                        _synthesizer.SpeakAsync(message);
                    }
                    else
                    {
                        _infoQueue.Enqueue(message);
                        PlayNextInfoMessage();
                    }
                }
                catch (Exception exception)
                {
                    _isAvailable = false;
                    Debug.WriteLine(string.Format("[PropFirmGuardian] TTS playback failed: {0}", exception.Message));
                    PlayBeep();
                }
            });
        }

        public void PlayBeep()
        {
            try
            {
                SystemSounds.Beep.Play();
            }
            catch (Exception exception)
            {
                Debug.WriteLine(string.Format("[PropFirmGuardian] Beep fallback failed: {0}", exception.Message));
            }
        }

        public void SpeakDailyLossWarning(string accountName)
        {
            Speak(string.Format("Warning. Account {0} at ninety percent of daily loss limit.", accountName), AlertPriority.Warning);
        }

        public void SpeakBreachFlattened(string accountName)
        {
            Speak(string.Format("Critical. Account {0} breached and flattened.", accountName), AlertPriority.Critical);
        }

        public void SpeakTiltLockout(string accountName, int minutes)
        {
            Speak(string.Format("Tilt sequence detected. Account {0} locked for {1} minutes.", accountName, minutes), AlertPriority.Critical);
        }

        public void SpeakNewsLockout()
        {
            Speak("News lockout in two minutes. Flattening live accounts.", AlertPriority.Warning);
        }

        public void Dispose()
        {
            if (_synthesizer == null)
                return;

            _synthesizer.SpeakCompleted -= OnSpeakCompleted;
            _synthesizer.Dispose();
            _synthesizer = null;
            _isAvailable = false;
        }

        private void PlayNextInfoMessage()
        {
            if (_isSpeakingInfo || _infoQueue.Count == 0)
                return;

            _isSpeakingInfo = true;
            _synthesizer.SpeakAsync(_infoQueue.Dequeue());
        }

        private void OnSpeakCompleted(object sender, SpeakCompletedEventArgs e)
        {
            ThreadSafeDispatcher.SafeInvoke(() =>
            {
                _isSpeakingInfo = false;
                PlayNextInfoMessage();
            });
        }
    }
}
