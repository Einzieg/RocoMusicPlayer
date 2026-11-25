using System.Buffers.Binary;
using System.Text;

namespace RocoPlayer.Music;

public static partial class ScoreIO
{
    private readonly record struct MidiEvent(long Tick, int Track, int Order, byte Status, int Value, byte Velocity);
    private const int MidiSizeLimit = 16 * 1024 * 1024;
    private static readonly UTF8Encoding TrackNameEncoding = new(false, true);

    public static Score LoadMidi(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > MidiSizeLimit) throw new InvalidDataException("MIDI 文件不能超过 16 MB");
        byte[] data = new byte[(int)file.Length];
        file.ReadExactly(data);
        var reader = new MidiReader(data);
        reader.Expect("MThd"u8);
        var header = reader.Chunk();
        int format = header.UInt16(), trackCount = header.UInt16(), ppq = header.UInt16();
        if (format > 1 || ppq == 0 || ppq >= 0x8000 || trackCount == 0 || format == 0 && trackCount != 1)
            throw new InvalidDataException("仅支持使用 PPQ 计时的 MIDI type 0/1");
        List<MidiEvent> timeline = [];
        Dictionary<int, string> names = [];
        HashSet<int> percussion = [];
        int attacks = 0, nextTrack = 0;
        for (int track = 0; track < trackCount; track++)
        {
            reader.Expect("MTrk"u8);
            var chunk = reader.Chunk();
            int firstTrack = nextTrack++;
            var instruments = new Dictionary<(int Channel, byte Program), int>();
            var programs = new byte[16];
            long tick = 0;
            byte running = 0;
            for (int order = 0; !chunk.Empty; order++)
            {
                tick += chunk.VariableLength();
                byte status = chunk.Peek();
                if (status >= 128) chunk.Byte(); else status = running;
                if (status == 0xff)
                {
                    byte type = chunk.Byte();
                    var content = chunk.Take(chunk.VariableLength());
                    if (type == 0x51)
                    {
                        if (content.Length != 3) throw new InvalidDataException("无效 MIDI 速度事件");
                        int tempo = (content[0] << 16) | (content[1] << 8) | content[2];
                        if (tempo == 0) throw new InvalidDataException("MIDI 速度不能为零");
                        timeline.Add(new(tick, firstTrack, order, status, tempo, 0));
                    }
                    else if (type == 3 && !names.ContainsKey(firstTrack))
                    {
                        try { names[firstTrack] = TrackNameEncoding.GetString(content).Trim(); }
                        catch (DecoderFallbackException) { names[firstTrack] = Encoding.Latin1.GetString(content).Trim(); }
                    }
                }
                else if (status is 0xf0 or 0xf7)
                {
                    chunk.Take(chunk.VariableLength());
                    running = 0;
                }
                else if (status is >= 0x80 and < 0xf0)
                {
                    running = status;
                    var first = chunk.DataByte();
                    byte second = status >> 4 is 12 or 13 ? (byte)0 : chunk.DataByte();
                    int channel = status & 15;
                    if (status >> 4 == 12) programs[channel] = first;
                    var instrument = (channel, programs[channel]);
                    if (!instruments.TryGetValue(instrument, out int part))
                        instruments[instrument] = part = instruments.Count == 0 ? firstTrack : nextTrack++;
                    if (status >> 4 is 8 or 9)
                    {
                        if (channel == 9) percussion.Add(part);
                        if ((status >> 4) == 9 && second > 0 && ++attacks > 100_000)
                            throw new InvalidDataException("乐谱最多支持 100000 个音符");
                        timeline.Add(new(tick, part, order, status, first, second));
                    }
                }
                else throw new InvalidDataException("未知 MIDI 消息或无效的连续状态");
            }
        }
        // A release at the same tick can close an attack even when the MIDI events are stored in reverse order.
        static int Priority(MidiEvent e) => e.Status == 0xff ? 0 : e.Status >> 4 == 9 && e.Velocity > 0 ? 1 : 2;
        timeline.Sort((a,b) => (a.Tick,Priority(a),a.Track,a.Order).CompareTo((b.Tick,Priority(b),b.Track,b.Order)));
        var active = new Dictionary<(int Track, int Channel, int Pitch), Queue<double>>();
        List<Note> notes = [];
        long tempoTick = 0;
        double tempoStart = 0, tempoBeat = 0, secondsPerBeat = .5;
        bool hasTempo = false;
        foreach (var e in timeline)
        {
            if (e.Status == 0xff)
            {
                // RocoMusic's MIDI reader anchors the first tempo at its own BPM, including a delayed first event.
                double nextSecondsPerBeat = 60 / (60000000d / e.Value);
                double beats = e.Tick / (double)ppq - tempoBeat;
                tempoStart += beats * (hasTempo ? secondsPerBeat : nextSecondsPerBeat);
                tempoBeat += beats;
                tempoTick = e.Tick;
                secondsPerBeat = nextSecondsPerBeat;
                hasTempo = true;
                continue;
            }
            double seconds = tempoStart + (e.Tick - tempoTick) / (double)ppq * secondsPerBeat;
            var key = (e.Track, e.Status & 15, e.Value);
            if (e.Status >> 4 == 9 && e.Velocity > 0)
            {
                if (!active.TryGetValue(key, out var queue)) active[key] = queue = new();
                queue.Enqueue(seconds);
            }
            else if (active.TryGetValue(key, out var held) && held.TryDequeue(out var start))
                notes.Add(new Note(start, Math.Max(.001, seconds - start), e.Value, e.Track));
        }
        if (notes.Count == 0) throw new InvalidDataException("MIDI 中没有完整的音符");
        var tracks = notes.Select(n => n.Track).Distinct().Order().ToDictionary(id => id,
            id => names.TryGetValue(id, out var name) && name.Length > 0 ? name : $"音轨 {id + 1}");
        // Preserve FIFO note-on order when two attacks share a pitch and start time.
        var ordered = notes.OrderBy(n => n.Start).ThenBy(n => n.Pitch).ThenBy(n => n.Track).ToArray();
        return new Score(Path.GetFileNameWithoutExtension(path), ordered, ordered.Max(n => n.Start + n.Duration), tracks)
        { PercussionTracks = percussion.Count == 0 ? null : percussion.Order().ToArray() };
    }

    private ref struct MidiReader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> remaining = bytes;
        public readonly bool Empty => remaining.IsEmpty;
        public readonly byte Peek() => remaining.IsEmpty ? throw new InvalidDataException("MIDI 文件意外结束") : remaining[0];
        public ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > remaining.Length) throw new InvalidDataException("MIDI 数据块长度无效");
            var data = remaining[..count];
            remaining = remaining[count..];
            return data;
        }
        public byte Byte() => Take(1)[0];
        public byte DataByte()
        {
            var data = Byte();
            return data < 128 ? data : throw new InvalidDataException("MIDI 数据字节无效");
        }
        public ushort UInt16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));
        public void Expect(ReadOnlySpan<byte> tag)
        {
            if (!Take(tag.Length).SequenceEqual(tag)) throw new InvalidDataException("MIDI 数据块标识无效");
        }
        public MidiReader Chunk() => new(Take(BinaryPrimitives.ReadInt32BigEndian(Take(4))));
        public int VariableLength()
        {
            int value = 0;
            for (int i = 0; i < 4; i++)
            {
                byte current = Byte();
                value = (value << 7) | (current & 0x7f);
                if (current < 128) return value;
            }
            throw new InvalidDataException("MIDI 时间或消息长度无效");
        }
    }

    public static void ExportMidi(Score score, string path)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is not (".mid" or ".midi"))
            throw new ArgumentException("导出文件名请使用 .mid 或 .midi");
        score.Validate();
        var events = new List<(long Tick, bool Down, byte Pitch)>(score.Notes.Count * 2);
        foreach (var n in score.Notes)
        {
            long start = checked((long)Math.Round(960 * n.Start));
            long end = Math.Max(start + 1, checked((long)Math.Round(960 * (n.Start + n.Duration))));
            events.Add((start, true, (byte)n.Pitch));
            events.Add((end, false, (byte)n.Pitch));
        }
        events.Sort();
        using var track = new MemoryStream();
        track.Write([0,0xff,0x51,3,7,0xa1,0x20]);
        long last = 0;
        foreach (var e in events)
        {
            WriteDelta(track, e.Tick - last);
            track.WriteByte(e.Down ? (byte)0x90 : (byte)0x80);
            track.WriteByte(e.Pitch);
            track.WriteByte(e.Down ? (byte)80 : (byte)0);
            last = e.Tick;
        }
        WriteDelta(track, Math.Max(0, checked((long)Math.Round(score.Duration * 960)) - last));
        track.Write([0xff,0x2f,0]);
        using var midi = new MemoryStream();
        midi.Write("MThd"u8);
        midi.Write([0,0,0,6,0,0,0,1,1,0xe0]);
        midi.Write("MTrk"u8);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, (int)track.Length);
        midi.Write(length);
        track.Position = 0;
        track.CopyTo(midi);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, midi.ToArray());
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    static void WriteDelta(Stream stream, long delta)
    {
        if (delta is < 0 or > 0x0fffffff) throw new InvalidDataException("间隔超出 MIDI 范围");
        Span<byte> bytes = stackalloc byte[4];
        int index = 3;
        bytes[index] = (byte)(delta & 127);
        while ((delta >>= 7) > 0) bytes[--index] = (byte)(128 | (delta & 127));
        stream.Write(bytes[index..]);
    }
}
