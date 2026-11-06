using System.Runtime.InteropServices;

namespace AudioLib.Output;

internal static class AudioOutputFactory
{
    public static IAudioOutput Create(string applicationName, int sampleRate, int chunkSamples)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new WinMmAudioOutput(sampleRate, chunkSamples);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return new PulseAudioOutput(applicationName, sampleRate, chunkSamples);
        }

        throw new AudioException("Audio output is only supported on Linux and Windows.");
    }
}
