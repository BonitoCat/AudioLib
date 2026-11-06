using AudioLib.Midi;
using AudioLib.Output;
using AudioLib.Synthesis;

namespace AudioLib;

public sealed class MidiPlayer : IDisposable
{
    public const int DefaultSampleRate = 44100;
    public const double MinimumSpeed = 0.1;
    public const double MaximumSpeed = 8.0;

    private const double ChunkSeconds = 0.02;

    private readonly object _renderLock = new();
    private readonly object _stateLock = new();
    private readonly ManualResetEventSlim _resumeSignal = new(true);
    private readonly SquareSynth _synth;
    private readonly string _applicationName;
    private readonly int _sampleRate;
    private Thread? _thread;
    private volatile bool _stopRequested;
    private volatile bool _paused;
    private double _speed = 1.0;
    private float _volume = 1.0f;
    private bool _loop = true;

    public event Action? Finished;
    public event Action<Exception>? OnError;

    public MidiSong? Song { get; private set; }
    public bool IsPaused => _paused;

    public bool IsPlaying
    {
        get
        {
            lock (_stateLock)
            {
                return _thread != null;
            }
        }
    }

    public bool Loop
    {
        get
        {
            lock (_renderLock)
            {
                return _loop;
            }
        }
        set
        {
            lock (_renderLock)
            {
                _loop = value;
            }
        }
    }

    public double Speed
    {
        get
        {
            lock (_renderLock)
            {
                return _speed;
            }
        }
        set
        {
            lock (_renderLock)
            {
                _speed = Math.Max(MinimumSpeed, Math.Min(MaximumSpeed, value));
            }
        }
    }

    public float Volume
    {
        get
        {
            lock (_renderLock)
            {
                return _volume;
            }
        }
        set
        {
            lock (_renderLock)
            {
                _volume = Math.Max(0f, Math.Min(1f, value));
            }
        }
    }

    public TimeSpan Position
    {
        get
        {
            lock (_renderLock)
            {
                return TimeSpan.FromSeconds(_synth.Time);
            }
        }
        set
        {
            lock (_renderLock)
            {
                _synth.Seek(value.TotalSeconds);
            }
        }
    }

    public MidiPlayer(string applicationName = "AudioLib", int sampleRate = DefaultSampleRate)
    {
        _applicationName = applicationName;
        _sampleRate = sampleRate;
        _synth = new(sampleRate);
    }

    public void Load(string path)
    {
        Load(MidiFile.Load(path));
    }

    public void Load(Stream stream)
    {
        Load(MidiFile.Load(stream));
    }

    public void Load(MidiSong song)
    {
        lock (_renderLock)
        {
            Song = song;
            _synth.Load(song);
        }
    }

    public void Play()
    {
        if (Song == null)
        {
            throw new InvalidOperationException("No song has been loaded.");
        }

        lock (_stateLock)
        {
            if (_thread != null)
            {
                Resume();
                return;
            }

            int chunkSamples = (int) (_sampleRate * ChunkSeconds);
            IAudioOutput output = AudioOutputFactory.Create(_applicationName, _sampleRate, chunkSamples);

            _stopRequested = false;
            _paused = false;
            _resumeSignal.Set();

            _thread = new(() => RenderLoop(output, chunkSamples))
            {
                IsBackground = true,
                Name = "AudioLib.MidiPlayer",
            };
            _thread.Start();
        }
    }

    public void Pause()
    {
        _paused = true;
        _resumeSignal.Reset();
    }

    public void Resume()
    {
        _paused = false;
        _resumeSignal.Set();
    }

    public void Stop()
    {
        Thread? thread;
        lock (_stateLock)
        {
            thread = _thread;
            _thread = null;
            _stopRequested = true;
            _resumeSignal.Set();
        }

        if (thread != null && thread != Thread.CurrentThread)
        {
            thread.Join();
        }

        _paused = false;
        lock (_renderLock)
        {
            _synth.Seek(0);
        }
    }

    public void Dispose()
    {
        Stop();
        _resumeSignal.Dispose();
    }

    private void RenderLoop(IAudioOutput output, int chunkSamples)
    {
        short[] buffer = new short[chunkSamples];
        bool finished = false;

        try
        {
            while (!_stopRequested)
            {
                if (_paused)
                {
                    output.Flush();
                    _resumeSignal.Wait();
                    continue;
                }

                bool playing;
                lock (_renderLock)
                {
                    playing = _synth.Render(buffer, buffer.Length, _speed, _volume, _loop);
                }

                output.Write(buffer, buffer.Length);
                if (!playing)
                {
                    finished = true;
                    break;
                }
            }
        }
        catch (Exception e)
        {
            OnError?.Invoke(e);
        }
        finally
        {
            output.Dispose();
        }

        lock (_stateLock)
        {
            if (_thread == Thread.CurrentThread)
            {
                _thread = null;
            }
        }

        if (!finished)
        {
            return;
        }

        lock (_renderLock)
        {
            _synth.Seek(0);
        }

        Finished?.Invoke();
    }
}
