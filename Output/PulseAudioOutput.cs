using System.Runtime.InteropServices;

namespace AudioLib.Output;

internal sealed class PulseAudioOutput : IAudioOutput
{
    private const string SimpleLibrary = "libpulse-simple.so.0";
    private const string PulseLibrary = "libpulse.so.0";
    private const int StreamPlayback = 1;
    private const int SampleS16Le = 3;
    private const int BufferedChunks = 3;

    private IntPtr _handle;

    [StructLayout(LayoutKind.Sequential)]
    private struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferAttributes
    {
        public uint MaxLength;
        public uint TargetLength;
        public uint PreBuffer;
        public uint MinimumRequest;
        public uint FragmentSize;
    }

    [DllImport(SimpleLibrary)]
    private static extern IntPtr pa_simple_new(string? server, string name, int direction, string? device, string streamName, ref SampleSpec spec, IntPtr channelMap, ref BufferAttributes attributes, out int error);

    [DllImport(SimpleLibrary)]
    private static extern int pa_simple_write(IntPtr handle, short[] data, UIntPtr bytes, out int error);

    [DllImport(SimpleLibrary)]
    private static extern int pa_simple_flush(IntPtr handle, out int error);

    [DllImport(SimpleLibrary)]
    private static extern void pa_simple_free(IntPtr handle);

    [DllImport(PulseLibrary)]
    private static extern IntPtr pa_strerror(int error);

    public PulseAudioOutput(string applicationName, int sampleRate, int chunkSamples)
    {
        SampleSpec spec = new()
        {
            Format = SampleS16Le,
            Rate = (uint) sampleRate,
            Channels = 1,
        };

        BufferAttributes attributes = new()
        {
            MaxLength = uint.MaxValue,
            TargetLength = (uint) (chunkSamples * sizeof(short) * BufferedChunks),
            PreBuffer = uint.MaxValue,
            MinimumRequest = uint.MaxValue,
            FragmentSize = uint.MaxValue,
        };

        int error;
        try
        {
            _handle = pa_simple_new(null, applicationName, StreamPlayback, null, "Music", ref spec, IntPtr.Zero, ref attributes, out error);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new AudioException("PulseAudio client library (libpulse-simple) was not found.", e);
        }

        if (_handle == IntPtr.Zero)
        {
            throw new AudioException("Could not connect to the sound server: " + DescribeError(error));
        }
    }

    public void Write(short[] samples, int count)
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        if (pa_simple_write(_handle, samples, (UIntPtr) (count * sizeof(short)), out int error) < 0)
        {
            throw new AudioException("Writing to the sound server failed: " + DescribeError(error));
        }
    }

    public void Flush()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        pa_simple_flush(_handle, out _);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        pa_simple_free(_handle);
        _handle = IntPtr.Zero;
    }

    private static string DescribeError(int error)
    {
        try
        {
            return Marshal.PtrToStringAnsi(pa_strerror(error)) ?? "error " + error;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return "error " + error;
        }
    }
}
