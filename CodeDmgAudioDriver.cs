using UnityEngine;

namespace BRCCodeDmg
{
    [RequireComponent(typeof(AudioSource))]
    public sealed class CodeDmgAudioDriver : MonoBehaviour
    {
        private AudioSource _audioSource;
        private CodeDmgEmulator _emulator;
        private bool _muted = true;

        private void Awake()
        {
            _audioSource = GetComponent<AudioSource>();

            _audioSource.playOnAwake = true;
            _audioSource.loop = true;
            _audioSource.mute = false;
            _audioSource.volume = 1f;
            _audioSource.pitch = 1f;
            _audioSource.spatialBlend = 0f;
            _audioSource.dopplerLevel = 0f;
            _audioSource.spread = 0f;
            _audioSource.reverbZoneMix = 0f;

            _audioSource.bypassEffects = true;
            _audioSource.bypassListenerEffects = true;
            _audioSource.bypassReverbZones = true;
            _audioSource.outputAudioMixerGroup = null;

            if (!_audioSource.isPlaying)
                _audioSource.Play();
        }

        public void SetEmulator(CodeDmgEmulator emulator)
        {
            _emulator = emulator;
        }

        public void SetMuted(bool muted)
        {
            _muted = muted;
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            for (int i = 0; i < data.Length; i++)
                data[i] = 0f;

            if (_muted || _emulator == null || _emulator.Apu == null || !_emulator.AudioEnabled)
                return;

            int read = _emulator.Apu.ReadSamples(data, 0, data.Length);

            for (int i = read; i < data.Length; i++)
                data[i] = 0f;
        }
    }
}