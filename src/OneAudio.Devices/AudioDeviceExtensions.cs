using OneAudio.Devices;

namespace OneAudio;

/// <summary>The one-call device layer, as members of <see cref="Audio"/>: <c>using OneAudio;</c> and a reference to the device package are all an app needs.</summary>
public static class AudioDeviceExtensions
{
    extension(Audio)
    {
        /// <summary>
        /// Plays a mixer on the default output device and returns the running output. Shared mode, about 20 ms of buffer, a soft-knee limiter,
        /// and it follows the default device. Dispose the result to stop.
        /// </summary>
        /// <param name="mixer">The mixer to play; its rate and channel count are used.</param>
        /// <param name="options">Optional device options; the rate and channels are taken from the mixer.</param>
        /// <returns>The started output. If no device can be opened it runs silent and retries; check <see cref="AudioOutputDevice.Counters"/>.</returns>
        public static AudioOutputDevice Play(AudioMixer mixer, DeviceStreamOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(mixer);
            var output = AudioOutputDevice.Open(mixer, options);
            output.Start();
            return output;
        }

        /// <summary>Plays any <see cref="IAudioSource"/> on the default output device; see <see cref="Play"/>.</summary>
        /// <param name="source">What to play.</param>
        /// <param name="options">Optional device options.</param>
        /// <returns>The started output.</returns>
        public static AudioOutputDevice CreateOutput(IAudioSource source, DeviceStreamOptions? options = null)
        {
            var output = AudioOutputDevice.Open(source, options);
            output.Start();
            return output;
        }

        /// <summary>Plays a render callback on the default output device at the options' rate and channels (48 kHz stereo by default).</summary>
        /// <param name="callback">Fills each block; see <see cref="RenderCallback"/> for the rules.</param>
        /// <param name="options">Optional device options.</param>
        /// <returns>The started output.</returns>
        public static AudioOutputDevice CreateOutput(RenderCallback callback, DeviceStreamOptions? options = null)
        {
            var output = AudioOutputDevice.Open(callback, options);
            output.Start();
            return output;
        }

        /// <summary>Records from the default input device; read the samples with <see cref="AudioInputDevice.Read"/>. 48 kHz stereo unless the options say otherwise.</summary>
        /// <param name="options">Optional device options.</param>
        /// <returns>The started input.</returns>
        public static AudioInputDevice CreateInput(DeviceStreamOptions? options = null)
        {
            var input = AudioInputDevice.Open(options);
            input.Start();
            return input;
        }

        /// <summary>Records from the default input device into a callback on the device thread; see <see cref="CaptureCallback"/>.</summary>
        /// <param name="callback">Receives each captured block.</param>
        /// <param name="options">Optional device options.</param>
        /// <returns>The started input.</returns>
        public static AudioInputDevice CreateInput(CaptureCallback callback, DeviceStreamOptions? options = null)
        {
            var input = AudioInputDevice.Open(callback, options);
            input.Start();
            return input;
        }
    }
}
