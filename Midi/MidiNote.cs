namespace AudioLib.Midi;

public sealed class MidiNote
{
    public double Start { get; }
    public double Duration { get; }
    public byte Key { get; }
    public byte Velocity { get; }
    public byte Channel { get; }

    public double End => Start + Duration;
    public double Frequency => 440.0 * Math.Pow(2.0, (Key - 69) / 12.0);

    public MidiNote(double start, double duration, byte key, byte velocity, byte channel)
    {
        Start = start;
        Duration = duration;
        Key = key;
        Velocity = velocity;
        Channel = channel;
    }
}
