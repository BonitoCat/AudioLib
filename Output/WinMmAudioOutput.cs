using System.Runtime.InteropServices;

namespace AudioLib.Output;

internal sealed class WinMmAudioOutput : IAudioOutput
{
    private const string Library = "winmm.dll";
    private const uint WaveMapper = uint.MaxValue;
    private const ushort PcmFormat = 1;
    private const uint HeaderDone = 0x1;
    private const int BufferCount = 6;

    private static readonly int HeaderSize = Marshal.SizeOf(typeof(WaveHeader));
    private static readonly int FlagsOffset = (int) Marshal.OffsetOf(typeof(WaveHeader), nameof(WaveHeader.Flags));

    private readonly IntPtr[] _headers = new IntPtr[BufferCount];
    private readonly IntPtr[] _buffers = new IntPtr[BufferCount];
    private readonly bool[] _submitted = new bool[BufferCount];
    private readonly int _bufferSamples;
    private IntPtr _device;
    private int _next;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [DllImport(Library)]
    private static extern int waveOutOpen(out IntPtr device, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);

    [DllImport(Library)]
    private static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, uint headerSize);

    [DllImport(Library)]
    private static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, uint headerSize);

    [DllImport(Library)]
    private static extern int waveOutWrite(IntPtr device, IntPtr header, uint headerSize);

    [DllImport(Library)]
    private static extern int waveOutReset(IntPtr device);

    [DllImport(Library)]
    private static extern int waveOutClose(IntPtr device);

    public WinMmAudioOutput(int sampleRate, int chunkSamples)
    {
        _bufferSamples = chunkSamples;

        WaveFormat format = new()
        {
            FormatTag = PcmFormat,
            Channels = 1,
            SamplesPerSecond = (uint) sampleRate,
            AverageBytesPerSecond = (uint) (sampleRate * sizeof(short)),
            BlockAlign = sizeof(short),
            BitsPerSample = 16,
        };

        int result = waveOutOpen(out _device, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0)
        {
            throw new AudioException("Could not open the audio device (waveOutOpen error " + result + ").");
        }

        for (int i = 0; i < BufferCount; i++)
        {
            _headers[i] = Marshal.AllocHGlobal(HeaderSize);
            _buffers[i] = Marshal.AllocHGlobal(chunkSamples * sizeof(short));
        }
    }

    public void Write(short[] samples, int count)
    {
        if (_device == IntPtr.Zero)
        {
            return;
        }

        int index = _next;
        IntPtr header = _headers[index];
        WaitAndRelease(index);

        int length = Math.Min(count, _bufferSamples);
        Marshal.Copy(samples, 0, _buffers[index], length);
        Marshal.StructureToPtr(new WaveHeader { Data = _buffers[index], BufferLength = (uint) (length * sizeof(short)) }, header, false);

        int result = waveOutPrepareHeader(_device, header, (uint) HeaderSize);
        if (result == 0)
        {
            result = waveOutWrite(_device, header, (uint) HeaderSize);
        }

        if (result != 0)
        {
            throw new AudioException("Writing audio failed (waveOut error " + result + ").");
        }

        _submitted[index] = true;
        _next = (index + 1) % BufferCount;
    }

    public void Flush()
    {
        if (_device == IntPtr.Zero)
        {
            return;
        }

        waveOutReset(_device);
    }

    public void Dispose()
    {
        if (_device == IntPtr.Zero)
        {
            return;
        }

        waveOutReset(_device);
        for (int i = 0; i < BufferCount; i++)
        {
            WaitAndRelease(i);
            Marshal.FreeHGlobal(_headers[i]);
            Marshal.FreeHGlobal(_buffers[i]);
        }

        waveOutClose(_device);
        _device = IntPtr.Zero;
    }

    private void WaitAndRelease(int index)
    {
        if (!_submitted[index])
        {
            return;
        }

        IntPtr header = _headers[index];
        while (((uint) Marshal.ReadInt32(header, FlagsOffset) & HeaderDone) == 0)
        {
            Thread.Sleep(1);
        }

        waveOutUnprepareHeader(_device, header, (uint) HeaderSize);
        _submitted[index] = false;
    }
}
