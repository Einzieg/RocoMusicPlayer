using System.Security.Cryptography;
using NAudio.Wave;
using RocoPlayer.Music;
using RocoPlayer.Playback;

internal static class HandpanChecks
{
    private static readonly int[] Pitches = [45, 52, 53, 55, 57, 59, 60, 62, 64];

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static float[] Render(HandpanPreview preview, int block = 640)
    {
        using var result = new MemoryStream();
        var buffer = new float[block];
        int read;
        while ((read = preview.Read(buffer, 0, buffer.Length)) > 0)
            result.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer.AsSpan(0, read)));
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(result.ToArray()).ToArray();
    }

    public static void Run()
    {
        const int rate = HandpanPreview.SampleRate;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int i = 0; i < Pitches.Length; i++)
        {
            string key = "BFGHJKTYU"[i].ToString();
            using var stream = typeof(AuditionPlayer).Assembly.GetManifestResourceStream($"RocoPlayer.Handpan.v2.{key}.wav")!;
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            hash.AppendData(bytes.ToArray());
            stream.Position = 0;
            using var wave = new WaveFileReader(stream);
            var expected = new float[rate / 2];
            Check(wave.ToSampleProvider().Read(expected, 0, expected.Length) == expected.Length, "实录长度");
            new HandpanCompressor(rate).Process(expected);
            var strike = new Score(key, [new(0, .02, Pitches[i])], .02);
            var actual = Render(new HandpanPreview(strike, 0, .25, 1), 274);
            Check(actual.SequenceEqual(expected), $"{key} 键音色、声道或短音余音不一致");
        }
        Check(Convert.ToHexString(hash.GetHashAndReset()) == "2740EE6E35D708203DB5F16F5B88B0FC74DB318664F6FFFF4E7C479DD9D3DECA",
            "九个 WAV 与 RocoMusic v2 采样不同");

        var held = new Score("尾音", [new(0, .01, 57)], .01);
        var tail = Render(new HandpanPreview(held, .6, .3, 1));
        var longNote = held with { Notes = [new(0, 3, 57)], Duration = 3 };
        Check(tail.Any(x => Math.Abs(x) > .005) && tail.SequenceEqual(Render(new HandpanPreview(longNote, .6, .3, 1))),
            "从敲击余音中途试听，不重新触发起音或依赖松键时间");
        Check(HandpanPreview.Duration(held, 1) == 6 && HandpanPreview.Duration(held, 2) == 12, "改速保留六秒自然衰减");
        Check(HandpanPreview.Duration(new Score("低音", [new(0, .1, 33)], .1), 1) == 8, "向下移调后最长八秒");
        var sparse = new Score("长曲", [new(0, .01, 57), new(32, .01, 60)], 32.01);
        var full = new HandpanPreview(sparse, 0, null, 1);
        var scratch = new float[2048];
        long total = 0;
        bool laterNote = false;
        for (int n; (n = full.Read(scratch, 0, scratch.Length)) > 0; total += n)
            if (total >= 32 * rate * 2 && scratch.Take(n).Any(x => Math.Abs(x) > .01)) laterNote = true;
        Check(full.EndPosition == 38 && total == 38 * rate * 2 && laterNote, "整曲播放超过 30 秒，包含末音余音");
        var chord = new Score("同刻重击", Enumerable.Repeat(new Note(0, .01, 60), 129).ToArray(), .01);
        Check(Render(new HandpanPreview(chord, 0, .2, 1)).SequenceEqual(Render(new HandpanPreview(
            chord with { Notes = chord.Notes.Take(128).ToArray() }, 0, .2, 1))), "同时发声上限");
        using var cancel = new CancellationTokenSource();
        var interrupted = new HandpanPreview(sparse, 0, null, 1, cancel.Token);
        cancel.Cancel();
        try { interrupted.Read(scratch, 0, scratch.Length); throw new Exception("试听取消未生效"); }
        catch (OperationCanceledException) { }

        // Matches the RocoMusic ScorePlayer fixture: seek into a B strike, transpose J up a
        // semitone, then overlap a T/U chord and another T. All source notes are 20 ms.
        var fixture = new Score("网页试听对照", [new(0,.02,45), new(.05,.02,58), new(.1,.02,60), new(.1,.02,64), new(.18,.02,60)], .20);
        var pcm = Render(new HandpanPreview(fixture, .05, 2, 1.25));
        Check(pcm.SequenceEqual(Render(new HandpanPreview(fixture, .05, 2, 1.25), 1024)), "分块边界不改变混音");
        // Stereo frames from RocoMusic's ScorePlayer rendered in a 24 kHz OfflineAudioContext.
        (int frame, float left, float right)[] webFrames = [
            (204, -.10166059f, -.07497746f), (545, .20960179f, .15479150f),
            (1304, -.27273074f, -.20245074f), (3151, .79669690f, .58897823f),
            (6857, -.06399424f, -.04739427f), (17004, -.25014603f, -.18618038f),
            (36108, .03750006f, .02703363f)
        ];
        foreach (var (frame, left, right) in webFrames)
            Check(Math.Abs(pcm[frame * 2] - left) < .001 && Math.Abs(pcm[frame * 2 + 1] - right) < .001, $"网页参考波形第 {frame} 帧");
        if (Environment.GetEnvironmentVariable("ROCO_TEST_WEB_AUDIO_PCM") is { Length: > 0 } path)
        {
            var reference = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(path)).ToArray();
            Check(pcm.Length == reference.Length, "网页与本地试听长度");
            double product = 0, sourcePower = 0, outputPower = 0, error = 0;
            for (int i = 0; i < pcm.Length; i++)
            {
                product += pcm[i] * reference[i];
                sourcePower += reference[i] * reference[i];
                outputPower += pcm[i] * pcm[i];
                error += Math.Pow(pcm[i] - reference[i], 2);
            }
            double correlation = product / Math.Sqrt(sourcePower * outputPower);
            double relativeError = Math.Sqrt(error / sourcePower);
            Console.WriteLine($"与网页双声道波形相关系数 {correlation:F6}，相对均方根误差 {relativeError:P3}");
            Check(correlation > .99999 && relativeError < .001, "网页与本地录音混音的一致性");
        }
        if (Environment.GetEnvironmentVariable("ROCO_TEST_AUDITION_WAV") is { Length: > 0 } output)
        {
            var melody = ScoreIO.ParseText("5 5 2' 2' 3' 3' 2':2", 120, "小星星", 3);
            WaveFileWriter.CreateWaveFile16(output, new HandpanPreview(melody, 0, null, 1));
        }
        Console.WriteLine("通过：九键原始录音、原谱移调、和弦与连击、跨段余音、倍速、整曲流式播放及取消。");
    }
}
