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
        private float _lastLeft;
        private float _lastRight;

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
            if (channels <= 0)
                return;

            int frameCount = data.Length / channels;
            int stereoSamplesNeeded = frameCount * 2;

            if (_stereoTemp.Length != stereoSamplesNeeded)
                _stereoTemp = new float[stereoSamplesNeeded];

            if (_muted || _emulator == null || _emulator.Apu == null || !_emulator.AudioEnabled)
            {
                // Do not hard-zero instantly
                WriteHeldSamples(data, channels, frameCount, 0f);
                return;
            }

            float volume = 1f;
            if (CodeDmgPlugin.ConfigSettings != null)
                volume = Mathf.Clamp01(CodeDmgPlugin.ConfigSettings.Volume.Value / 100f);

            int read = _emulator.Apu.ReadSamples(_stereoTemp, 0, stereoSamplesNeeded);

            for (int i = read; i < stereoSamplesNeeded; i += 2)
            {
                _stereoTemp[i] = _lastLeft;
                if (i + 1 < stereoSamplesNeeded)
                    _stereoTemp[i + 1] = _lastRight;
            }

            if (stereoSamplesNeeded >= 2)
            {
                _lastLeft = _stereoTemp[stereoSamplesNeeded - 2];
                _lastRight = _stereoTemp[stereoSamplesNeeded - 1];
            }

            if (channels == 1)
            {
                for (int frame = 0; frame < frameCount; frame++)
                {
                    float left = _stereoTemp[frame * 2];
                    float right = _stereoTemp[frame * 2 + 1];
                    data[frame] = (left + right) * 0.5f * volume;
                }
            }
            else
            {
                for (int frame = 0; frame < frameCount; frame++)
                {
                    float left = _stereoTemp[frame * 2] * volume;
                    float right = _stereoTemp[frame * 2 + 1] * volume;
                    int baseIndex = frame * channels;

                    data[baseIndex] = left;
                    data[baseIndex + 1] = right;

                    float mono = (left + right) * 0.5f;
                    for (int c = 2; c < channels; c++)
                        data[baseIndex + c] = mono;
                }
            }
        }

        private void WriteHeldSamples(float[] data, int channels, int frameCount, float gain)
        {
            if (channels == 1)
            {
                float mono = (_lastLeft + _lastRight) * 0.5f * gain;
                for (int i = 0; i < frameCount; i++)
                    data[i] = mono;
            }
            else
            {
                float left = _lastLeft * gain;
                float right = _lastRight * gain;

                for (int frame = 0; frame < frameCount; frame++)
                {
                    int baseIndex = frame * channels;
                    data[baseIndex] = left;
                    data[baseIndex + 1] = right;

                    float mono = (left + right) * 0.5f;
                    for (int c = 2; c < channels; c++)
                        data[baseIndex + c] = mono;
                }
            }
        }
    }
}