using RocoPlayer.Music;
using System.Text;
using System.Text.Json;

static void Check(bool value, string description)
{
    if (!value) throw new Exception(description);
}

var directory = Directory.CreateTempSubdirectory("roco-library-check-");
try
{
    var path = Path.Combine(directory.FullName, "tempo.mid");
    var settings = new PlayerSettings();
    var demo = ScoreIO.Load(Path.Combine(AppContext.BaseDirectory, "twinkle.txt"), settings);
    Check(demo.Notes.Count == 42 && demo.Notes.All(n => settings.KeyMap.ContainsKey(n.Pitch)), "Sample mapping");
    var text = ScoreIO.ParseText("6, [3 5]:1/2 - 0:2 | 1'");
    Check(text.Notes.Select(n => n.Pitch).SequenceEqual([57,64,67,72]) && text.Notes[1].Duration == .75 && text.Duration == 2.75, "Text chords");
    ScoreIO.ExportMidi(text, path);
    var imported = ScoreIO.LoadMidi(path);
    Check(imported.Notes.SequenceEqual(text.Notes) && imported.Duration == text.Duration, "MIDI roundtrip");

    // Type 1: tempo changes on the conductor track, running-status note off, and drums.
    File.WriteAllBytes(path, Convert.FromHexString(
        "4D546864000000060001000301E0" +
        "4D54726B0000001300FF510307A1208360FF51030F424000FF2F00" +
        "4D54726B0000001600FF03064D656C6F6479009040508740400000FF2F00" +
        "4D54726B0000000D00992450836089240000FF2F00"));
    imported = ScoreIO.LoadMidi(path);
    Check(imported.Notes.Count == 2 && imported.Notes.Contains(new Note(0,1.5,64,1)) && imported.Tracks![1] == "Melody" &&
        imported.PercussionTracks!.SequenceEqual([2]) && Arrangement.Create(imported, settings, Arrangement.ImportTexture, null, null, default).Score.Notes.Count == 1,
        "MIDI tempo map, drums retained in original and excluded from handpan");

    var library = new ScoreLibrary(directory.FullName);
    // RocoMusic Studio's default import of the same synthetic MIDI (named lead, split channels,
    // drums, delayed first tempo, two tempos, and two same-tick attacks on the same key).
    var studioPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "studio-import.mid");
    var studio = library.Add(ScoreIO.Load(studioPath, settings), studioPath);
    Note[] expected = [new(.75,.375,59), new(.75,.375,55), new(1.125,.375,62), new(1.5,.4,60)];
    Check(studio.Original.Notes.Count == 8 && studio.Score.Notes.Count == expected.Length &&
        studio.Score.Notes.Zip(expected).All(pair => pair.First.Pitch == pair.Second.Pitch &&
            Math.Abs(pair.First.Start - pair.Second.Start) < 1e-9 && Math.Abs(pair.First.Duration - pair.Second.Duration) < 1e-9),
        "RocoMusic Studio default import parity");
    library.Remove(studio.Id);
    var original = new Score("和弦", [new(.2,.3,72), new(.21,.3,76), new(.22,.3,79)], 2);
    var item = library.Add(original,"source.mid", metadata: new() { ["online_source"] = JsonSerializer.SerializeToElement(new { score_id = "1234" }) });
    Check(item.Score.Notes.Select(n => n.Pitch).Distinct().Count() == 3 && item.Score.Notes.Select(n => n.Start).SequenceEqual([.2,.21,.22]), "Chord fitting");
    var reloaded = new ScoreLibrary(directory.FullName);
    Check(reloaded.Items.Single().Original.Notes.SequenceEqual(original.Notes) &&
          reloaded.Items.Single().Extra["online_source"].GetProperty("score_id").GetString() == "1234", "Library roundtrip");
    Check(reloaded.CurrentId == item.Id && reloaded.Settings.Mapping.SequenceEqual(settings.Mapping), "Library selection/settings");
    var jsonPath = Path.Combine(directory.FullName, "library.json");
    using (var json = JsonDocument.Parse(File.ReadAllText(jsonPath, Encoding.UTF8)))
    {
        var encoded = json.RootElement.GetProperty("items")[0];
        Check(encoded.GetProperty("score").GetProperty("notes")[0].GetArrayLength() == 4 && encoded.GetProperty("original").GetProperty("tracks")[0][0].GetInt32() == 0, "Legacy row shapes");
    }
    reloaded.SetSettings(settings with { Mapping = settings.Mapping.Select(b => b with { Pitch = b.Pitch + 12 }).ToArray() });
    var saved = File.ReadAllBytes(jsonPath);
    try { reloaded.Arranged(item.Id, new CancellationToken(canceled: true)); throw new Exception("Cancelled remap succeeded"); }
    catch (OperationCanceledException) { Check(saved.SequenceEqual(File.ReadAllBytes(jsonPath)), "Cancelled remap preserved library"); }
    var remapped = reloaded.Arranged(item.Id);
    Check(remapped.Score.Notes.All(n => reloaded.Settings.KeyMap.ContainsKey(n.Pitch)) && remapped.Extra.ContainsKey("online_source"), "New mapping");
    bool conflict = false;
    try { library.SetMode("random"); } catch (IOException) { conflict = true; }
    Check(conflict && library.Mode == "order", "Stale writer rollback");
    var tune = new[] {73,82,77,77,75,73,70}.Select((p,i) => new Note(.2 * i, .15, p)).ToArray();
    var contour = Arrangement.Create(new Score("peak",tune,2), settings,"chords",null,null,default).Score.Notes;
    Check(contour[1].Pitch > contour[0].Pitch && contour[1].Pitch > contour[2].Pitch && contour[2].Pitch == contour[3].Pitch, "Monophonic contour");
    var voices = new Score("ensemble",
        [new(0,.9,64),new(1,.9,64),new(0,.5,57,1),new(.25,.5,57,1),new(1,.5,57,1)], 2);
    var config = JsonSerializer.SerializeToElement(new { version = 1, backing_track = 1, tempo_map = new[] { new[] {0,0,1_000_000} } });
    var bass = Arrangement.Create(voices,settings,"melody_bass",0,config,default).Score;
    Check(bass.Notes.Where(n => n.Track == 0).SequenceEqual(voices.Notes.Take(2)) &&
          bass.Notes.Where(n => n.Track == 1).SequenceEqual([new Note(0,.5,45,1),new Note(1,.5,45,1)]), "Locked melody and sparse bass");

    // A version 1 Python library can omit settings introduced after its creation.
    var oldDir = Directory.CreateDirectory(Path.Combine(directory.FullName, "legacy"));
    var legacyJson = """
        {"version":1,"mode":"random","current_id":"v1","items":[{"id":"v1","title":"old","mapping":[],
        "original":{"title":"old","duration":1,"tracks":[[0,"Lead"]],"notes":[[0,0.5,64,0]]},
        "score":{"title":"old","duration":1,"tracks":[[0,"Lead"]],"notes":[[0,0.5,64,0]]},
        "created_at":"2020-01-01","summary":"old","source":"legacy.mid","note_count":1,"original_count":1,
        "duration":1,"transcription":{"engine":"yin"}}],"settings":{"speed":0.5}}
        """;
    File.WriteAllText(Path.Combine(oldDir.FullName, "library.json"), legacyJson, Encoding.UTF8);
    var legacy = new ScoreLibrary(oldDir.FullName);
    var loaded = legacy.Arranged("v1");
    Check(legacy.Mode == "random" && legacy.Settings.Speed == .5 && legacy.Settings.Octave == 3 && loaded.Id == "v1" &&
          loaded.CreatedAt == "2020-01-01" && loaded.Extra["transcription"].GetProperty("engine").GetString() == "yin", "Python library schema");
    foreach (var summary in new[] { """{"folded":3}""", "null" })
    {
        var legacyPath = Path.Combine(oldDir.FullName, "library.json");
        var fixture = System.Text.Json.Nodes.JsonNode.Parse(legacyJson)!;
        fixture["items"]![0]!["summary"] = System.Text.Json.Nodes.JsonNode.Parse(summary);
        File.WriteAllText(legacyPath, fixture.ToJsonString(), Encoding.UTF8);
        var mixedLibrary = new ScoreLibrary(oldDir.FullName);
        var historical = mixedLibrary.Arranged("v1");
        using var mixedJson = JsonDocument.Parse(File.ReadAllText(legacyPath, Encoding.UTF8));
        Check(historical.Summary == "" && mixedJson.RootElement.GetProperty("items")[0].GetProperty("summary").GetRawText() == summary,
            "Historical structured/null summary preserved on remap");
    }
    reloaded.Remove(item.Id);
    Check(new ScoreLibrary(directory.FullName) is { Items.Count: 0, CurrentId: null }, "Remove last score");
    var first = reloaded.Add(text, "1.mid");
    var middle = reloaded.Add(text, "2.mid");
    var last = reloaded.Add(text, "3.mid");
    reloaded.Remove(middle.Id, [last.Id, middle.Id, first.Id]);
    Check(reloaded.CurrentId == last.Id, "Removing an unselected score keeps the current song");
    reloaded.Remove(last.Id, [first.Id, last.Id]);
    Check(new ScoreLibrary(directory.FullName).CurrentId == first.Id, "Deleting at the end saves the preceding visible song");
    var queue = new PlaylistNavigator();
    string[] tracks = ["a", "b", "c"];
    Check(queue.Next(tracks, "c", "order") == null && queue.Next(tracks, "c", "repeat_all") == "a" &&
        queue.Next(tracks, "b", "repeat_one") == "b" && queue.Next(tracks, "b", "repeat_one", automatic: false) == "c",
        "Sequential and repeat queue boundaries");
    string? shuffled = queue.Next(tracks, "b", "random");
    Check(shuffled is "a" or "c" && queue.Previous(tracks, shuffled, "random") == "b", "Shuffle and previous");
    File.WriteAllText(jsonPath, "{broken", Encoding.UTF8);
    try { _ = new ScoreLibrary(directory.FullName); throw new Exception("Broken library was accepted"); }
    catch (InvalidDataException) { Check(File.ReadAllText(jsonPath, Encoding.UTF8) == "{broken", "Corrupt library preserved"); }
    Console.WriteLine("Music checks passed");
}
finally { directory.Delete(true); }
