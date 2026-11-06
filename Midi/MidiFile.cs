using System.Text;

namespace AudioLib.Midi;

public static class MidiFile
{
    public const int PercussionChannel = 9;

    private const int DefaultMicrosecondsPerQuarter = 500000;
    private const byte MetaEvent = 0xFF;
    private const byte SysExStart = 0xF0;
    private const byte SysExEscape = 0xF7;
    private const byte TempoMeta = 0x51;

    private enum EventKind
    {
        Tempo,
        NoteOff,
        NoteOn,
        End,
    }

    private struct RawEvent
    {
        public long Tick;
        public int Order;
        public EventKind Kind;
        public byte Channel;
        public byte Key;
        public byte Velocity;
        public int Tempo;
    }

    public static MidiSong Load(string path, bool includePercussion = false)
    {
        using FileStream stream = File.OpenRead(path);
        return Load(stream, includePercussion);
    }

    public static MidiSong Load(Stream stream, bool includePercussion = false)
    {
        using MemoryStream memory = new();
        stream.CopyTo(memory);

        return Parse(memory.ToArray(), includePercussion);
    }

    public static MidiSong Parse(byte[] data, bool includePercussion = false)
    {
        ByteReader reader = new(data);
        if (reader.ReadChunkId() != "MThd")
        {
            throw new MidiFormatException("Not a MIDI file (missing MThd header).");
        }

        int headerLength = (int) reader.ReadUInt32();
        int headerEnd = reader.Position + headerLength;
        reader.ReadUInt16();
        int trackCount = reader.ReadUInt16();
        int division = reader.ReadUInt16();
        reader.Position = headerEnd;

        if (division == 0)
        {
            throw new MidiFormatException("Invalid time division.");
        }

        List<RawEvent> events = new();
        int tracksRead = 0;
        while (tracksRead < trackCount && reader.Remaining >= 8)
        {
            string chunkId = reader.ReadChunkId();
            int length = (int) reader.ReadUInt32();
            int chunkEnd = Math.Min(reader.Position + length, data.Length);

            if (chunkId == "MTrk")
            {
                ReadTrack(reader, chunkEnd, events);
                tracksRead++;
            }

            reader.Position = chunkEnd;
        }

        return BuildSong(events, division, includePercussion);
    }

    private static void ReadTrack(ByteReader reader, int end, List<RawEvent> events)
    {
        long tick = 0;
        byte runningStatus = 0;

        while (reader.Position < end)
        {
            tick += reader.ReadVariableLength();
            byte status = reader.ReadByte();

            if (status == MetaEvent)
            {
                byte type = reader.ReadByte();
                int length = (int) reader.ReadVariableLength();
                int dataEnd = reader.Position + length;

                if (type == TempoMeta && length >= 3)
                {
                    int tempo = reader.ReadByte() << 16 | reader.ReadByte() << 8 | reader.ReadByte();
                    events.Add(new() { Tick = tick, Order = events.Count, Kind = EventKind.Tempo, Tempo = tempo });
                }

                reader.Position = dataEnd;
                runningStatus = 0;
                continue;
            }

            if (status is SysExStart or SysExEscape)
            {
                int length = (int) reader.ReadVariableLength();
                reader.Position += length;
                runningStatus = 0;
                continue;
            }

            byte firstData;
            if (status < 0x80)
            {
                if (runningStatus == 0)
                {
                    throw new MidiFormatException("Data byte without a running status.");
                }

                firstData = status;
                status = runningStatus;
            }
            else
            {
                runningStatus = status;
                firstData = reader.ReadByte();
            }

            int command = status & 0xF0;
            byte channel = (byte) (status & 0x0F);

            if (command is 0xC0 or 0xD0)
            {
                continue;
            }

            byte secondData = reader.ReadByte();

            if (command == 0x90 && secondData > 0)
            {
                events.Add(new() { Tick = tick, Order = events.Count, Kind = EventKind.NoteOn, Channel = channel, Key = firstData, Velocity = secondData });
                continue;
            }

            if (command is 0x80 or 0x90)
            {
                events.Add(new() { Tick = tick, Order = events.Count, Kind = EventKind.NoteOff, Channel = channel, Key = firstData });
            }
        }

        events.Add(new() { Tick = tick, Order = events.Count, Kind = EventKind.End });
    }

    private static MidiSong BuildSong(List<RawEvent> events, int division, bool includePercussion)
    {
        events.Sort((a, b) =>
        {
            int byTick = a.Tick.CompareTo(b.Tick);
            if (byTick != 0)
            {
                return byTick;
            }

            int byKind = a.Kind.CompareTo(b.Kind);
            return byKind != 0 ? byKind : a.Order.CompareTo(b.Order);
        });

        bool smpte = (division & 0x8000) != 0;
        double smpteSecondsPerTick = 0;
        if (smpte)
        {
            int framesPerSecond = -(sbyte) (division >> 8);
            int ticksPerFrame = division & 0xFF;
            smpteSecondsPerTick = 1.0 / Math.Max(1, framesPerSecond * ticksPerFrame);
        }

        List<MidiNote> notes = new();
        Dictionary<int, Queue<(double Start, byte Velocity)>> openNotes = new();
        double seconds = 0;
        long lastTick = 0;
        int tempo = DefaultMicrosecondsPerQuarter;

        foreach (RawEvent e in events)
        {
            double secondsPerTick = smpte ? smpteSecondsPerTick : tempo / 1_000_000.0 / division;
            seconds += (e.Tick - lastTick) * secondsPerTick;
            lastTick = e.Tick;

            if (e.Kind == EventKind.Tempo)
            {
                tempo = Math.Max(1, e.Tempo);
                continue;
            }

            if (e.Kind == EventKind.End)
            {
                continue;
            }

            if (e.Channel == PercussionChannel && !includePercussion)
            {
                continue;
            }

            int slot = e.Channel * 128 + e.Key;
            if (e.Kind == EventKind.NoteOn)
            {
                if (!openNotes.TryGetValue(slot, out Queue<(double Start, byte Velocity)>? queue))
                {
                    queue = new();
                    openNotes[slot] = queue;
                }

                queue.Enqueue((seconds, e.Velocity));
                continue;
            }

            if (!openNotes.TryGetValue(slot, out Queue<(double Start, byte Velocity)>? open) || open.Count == 0)
            {
                continue;
            }

            (double start, byte velocity) = open.Dequeue();
            notes.Add(new(start, seconds - start, e.Key, velocity, e.Channel));
        }

        foreach (KeyValuePair<int, Queue<(double Start, byte Velocity)>> pair in openNotes)
        {
            foreach ((double start, byte velocity) in pair.Value)
            {
                notes.Add(new(start, seconds - start, (byte) (pair.Key % 128), velocity, (byte) (pair.Key / 128)));
            }
        }

        if (notes.Count == 0)
        {
            throw new MidiFormatException("The MIDI file contains no playable notes.");
        }

        return new(notes, seconds);
    }

    private sealed class ByteReader
    {
        private readonly byte[] _data;

        public int Position { get; set; }
        public int Remaining => _data.Length - Position;

        public ByteReader(byte[] data)
        {
            _data = data;
        }

        public byte ReadByte()
        {
            if (Position >= _data.Length)
            {
                throw new MidiFormatException("Unexpected end of MIDI data.");
            }

            return _data[Position++];
        }

        public int ReadUInt16()
        {
            return ReadByte() << 8 | ReadByte();
        }

        public uint ReadUInt32()
        {
            return (uint) ReadByte() << 24 | (uint) ReadByte() << 16 | (uint) ReadByte() << 8 | ReadByte();
        }

        public string ReadChunkId()
        {
            if (Remaining < 4)
            {
                throw new MidiFormatException("Unexpected end of MIDI data.");
            }

            string id = Encoding.ASCII.GetString(_data, Position, 4);
            Position += 4;
            return id;
        }

        public long ReadVariableLength()
        {
            long value = 0;
            for (int i = 0; i < 4; i++)
            {
                byte b = ReadByte();
                value = value << 7 | (long) (b & 0x7F);
                if ((b & 0x80) == 0)
                {
                    return value;
                }
            }

            throw new MidiFormatException("Variable-length value is too long.");
        }
    }
}
