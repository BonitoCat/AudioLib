using AudioLib.Midi;

namespace AudioLib.Synthesis;

internal sealed class SquareSynth
{
    private const int MaxVoices = 10;
    private const double AttackSeconds = 0.004;
    private const double ReleaseSeconds = 0.025;
    private const double VoiceGain = 0.16;

    private readonly int _sampleRate;
    private readonly double _sampleSeconds;
    private readonly List<Voice> _voices = new();
    private MidiSong? _song;
    private int _nextNote;
    private double _time;

    public double Time => _time;

    public SquareSynth(int sampleRate)
    {
        _sampleRate = sampleRate;
        _sampleSeconds = 1.0 / sampleRate;
    }

    public void Load(MidiSong song)
    {
        _song = song;
        Seek(0);
    }

    public void Seek(double seconds)
    {
        _voices.Clear();
        _time = 0;
        _nextNote = 0;

        if (_song == null)
        {
            return;
        }

        _time = Math.Max(0, Math.Min(seconds, _song.DurationSeconds));
        while (_nextNote < _song.Notes.Count && _song.Notes[_nextNote].Start < _time)
        {
            _nextNote++;
        }
    }

    public bool Render(short[] buffer, int count, double speed, float volume, bool loop)
    {
        MidiSong? song = _song;
        if (song == null)
        {
            Array.Clear(buffer, 0, count);
            return false;
        }

        double songStep = _sampleSeconds * speed;
        bool finished = false;

        for (int i = 0; i < count; i++)
        {
            if (!finished)
            {
                StartDueNotes(song);
            }

            buffer[i] = (short) (Math.Tanh(MixVoices() * volume) * short.MaxValue);

            if (finished)
            {
                continue;
            }

            _time += songStep;
            if (_time < song.DurationSeconds)
            {
                continue;
            }

            if (!loop)
            {
                finished = true;
                continue;
            }

            _time -= song.DurationSeconds;
            _nextNote = 0;
            foreach (Voice voice in _voices)
            {
                voice.End -= song.DurationSeconds;
            }
        }

        return !finished || _voices.Count > 0;
    }

    private void StartDueNotes(MidiSong song)
    {
        while (_nextNote < song.Notes.Count && song.Notes[_nextNote].Start <= _time)
        {
            MidiNote note = song.Notes[_nextNote];
            _nextNote++;

            if (_voices.Count >= MaxVoices)
            {
                _voices.RemoveAt(0);
            }

            _voices.Add(new()
            {
                Increment = note.Frequency / _sampleRate,
                Gain = VoiceGain * note.Velocity / 127.0,
                End = note.End,
            });
        }
    }

    private double MixVoices()
    {
        double mixed = 0;
        for (int i = _voices.Count - 1; i >= 0; i--)
        {
            Voice voice = _voices[i];
            if (voice.ReleaseAge < 0 && _time >= voice.End)
            {
                voice.ReleaseAge = 0;
            }

            if (voice.ReleaseAge >= ReleaseSeconds)
            {
                _voices.RemoveAt(i);
                continue;
            }

            double envelope = Math.Min(1.0, voice.Age / AttackSeconds);
            if (voice.ReleaseAge >= 0)
            {
                envelope *= 1.0 - voice.ReleaseAge / ReleaseSeconds;
                voice.ReleaseAge += _sampleSeconds;
            }

            mixed += voice.Gain * envelope * Square(voice.Phase, voice.Increment);

            voice.Phase += voice.Increment;
            if (voice.Phase >= 1.0)
            {
                voice.Phase -= 1.0;
            }

            voice.Age += _sampleSeconds;
        }

        return mixed;
    }

    private static double Square(double phase, double increment)
    {
        double value = phase < 0.5 ? 1.0 : -1.0;
        value += PolyBlep(phase, increment);
        value -= PolyBlep((phase + 0.5) % 1.0, increment);
        return value;
    }

    private static double PolyBlep(double t, double dt)
    {
        if (t < dt)
        {
            t /= dt;
            return t + t - t * t - 1.0;
        }

        if (t > 1.0 - dt)
        {
            t = (t - 1.0) / dt;
            return t * t + t + t + 1.0;
        }

        return 0.0;
    }

    private sealed class Voice
    {
        public double Increment;
        public double Phase;
        public double Gain;
        public double End;
        public double Age;
        public double ReleaseAge = -1;
    }
}
