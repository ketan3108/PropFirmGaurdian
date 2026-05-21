using System;
using System.Diagnostics;
using System.Media;

namespace PropFirmGuardian.Services
{
    public sealed class AudioService
    {
        private int _volume;
        private bool _isMuted;

        public AudioService()
        {
            _volume = 70;
        }

        public int Volume
        {
            get { return _volume; }
            set { _volume = Math.Max(0, Math.Min(100, value)); }
        }

        public bool IsMuted
        {
            get { return _isMuted; }
            set { _isMuted = value; }
        }

        public void PlayWarning()
        {
            Play(SystemSounds.Asterisk);
        }

        public void PlayLock()
        {
            Play(SystemSounds.Exclamation);
        }

        public void PlayFlatten()
        {
            Play(SystemSounds.Hand);
        }

        public void PlayRecovery()
        {
            Play(SystemSounds.Beep);
        }

        private void Play(SystemSound sound)
        {
            if (_isMuted || sound == null)
                return;

            try
            {
                sound.Play();
            }
            catch (Exception exception)
            {
                Debug.WriteLine(string.Format("[AUDIO] Playback failed: {0}", exception.Message));
            }
        }
    }
}
