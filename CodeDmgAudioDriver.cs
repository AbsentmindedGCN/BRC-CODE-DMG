using UnityEngine;

namespace BRCCodeDmg
{
    [RequireComponent(typeof(AudioSource))]
    public sealed class CodeDmgAudioDriver : MonoBehaviour
    {
        private AudioSource _audioSource;
        private CodeDmgEmulator _emulator;
        private bool _muted = true;

        private float[] _stereoTemp = new float[0];

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

            if (channels <= 0)
                return;

            int frameCount = data.Length / channels;
            int stereoSamplesNeeded = frameCount * 2;

            if (_stereoTemp.Length != stereoSamplesNeeded)
                _stereoTemp = new float[stereoSamplesNeeded];

            for (int i = 0; i < stereoSamplesNeeded; i++)
                _stereoTemp[i] = 0f;

            int read = _emulator.Apu.ReadSamples(_stereoTemp, 0, stereoSamplesNeeded);
            int framesRead = read / 2;

            for (int frame = 0; frame < framesRead; frame++)
            {
                float left = _stereoTemp[frame * 2];
                float right = _stereoTemp[frame * 2 + 1];

                // Force mono so hard-panned channels can't disappear
                float mono = (left + right) * 0.5f;

                int baseIndex = frame * channels;
                for (int c = 0; c < channels; c++)
                    data[baseIndex + c] = mono;
            }
        }
    }
}