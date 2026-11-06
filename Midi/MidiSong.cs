namespace AudioLib.Midi;

public sealed class MidiSong
{
    public IReadOnlyList<MidiNote> Notes { get; }
    public double DurationSeconds { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds(DurationSeconds);

    public MidiSong(IEnumerable<MidiNote> notes, double durationSeconds)
    {
        Notes = notes.OrderBy(note => note.Start).ToList();
        DurationSeconds = Math.Max(durationSeconds, Notes.Count == 0 ? 0 : Notes.Max(note => note.End));
    }
}
