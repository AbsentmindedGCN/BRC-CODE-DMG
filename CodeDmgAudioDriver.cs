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

        // ── FIX: Removed the "force mono" workaround. ──────────────────────────
        // The previous code mixed left + right into a single mono signal to paper
        // over channels that seemed to disappear. The root cause of that problem
        // was the APU frame sequencer running 256× too fast (Timer.cs bit-4 bug),
        // which caused length counters to expire almost instantly and made channels
        // sound absent. Now that the timing is correct, proper stereo output is
        // restored: the left APU output goes to the left speaker/headphone channel
        // and the right APU output goes to the right.
        //
        // If Unity reports a mono output device (channels == 1) we downmix
        // gracefully to keep things working on speakers that don't support stereo.
        // ─────────────────────────────────────────────────────────────────────────
        private void OnAudioFilterRead(float[] data, int channels)
        {
            for (int i = 0; i < data.Length; i++)
                data[i] = 0f;

            if (_muted || _emulator == null || _emulator.Apu == null || !_emulator.AudioEnabled)
                return;

            if (channels <= 0)
                return;

            // Read volume from config (0-100), convert to 0.0-1.0 scale
            float volume = 1f;
            if (CodeDmgPlugin.ConfigSettings != null)
                volume = Mathf.Clamp01(CodeDmgPlugin.ConfigSettings.Volume.Value / 100f);

            int frameCount = data.Length / channels;
            int stereoSamplesNeeded = frameCount * 2;

            if (_stereoTemp.Length != stereoSamplesNeeded)
                _stereoTemp = new float[stereoSamplesNeeded];

            for (int i = 0; i < stereoSamplesNeeded; i++)
                _stereoTemp[i] = 0f;

            int read = _emulator.Apu.ReadSamples(_stereoTemp, 0, stereoSamplesNeeded);
            int framesRead = read / 2;

            if (channels == 1)
            {
                for (int frame = 0; frame < framesRead; frame++)
                {
                    float left  = _stereoTemp[frame * 2];
                    float right = _stereoTemp[frame * 2 + 1];
                    data[frame] = (left + right) * 0.5f * volume;
                }
            }
            else
            {
                for (int frame = 0; frame < framesRead; frame++)
                {
                    float left  = _stereoTemp[frame * 2]  * volume;
                    float right = _stereoTemp[frame * 2 + 1] * volume;
                    int baseIndex = frame * channels;

                    data[baseIndex + 0] = left;
                    data[baseIndex + 1] = right;

                    for (int c = 2; c < channels; c++)
                        data[baseIndex + c] = (left + right) * 0.5f;
                }
            }
        }
    }
}
