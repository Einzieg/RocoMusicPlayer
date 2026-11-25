using System.Security.Cryptography;
using System.Text;
using RocoPlayer.Music;

internal static class ExportFiles
{
    internal static readonly string[] Suffixes = [".original.mid", ".9keys.mid", ".report.json"];

    internal static void Save(string directory, string stem, Score source, Score fitted, string report, CancellationToken token)
    {
        var output = Directory.CreateDirectory(directory);
        var stage = output.CreateSubdirectory(".arrange-" + Guid.NewGuid().ToString("N")).FullName;
        string[] names = Suffixes.Select(suffix => stem + suffix).ToArray();
        bool preserveBackups = false;
        try
        {
            ScoreIO.ExportMidi(source, Path.Combine(stage, names[0]));
            ScoreIO.ExportMidi(fitted, Path.Combine(stage, names[1]));
            File.WriteAllText(Path.Combine(stage, names[2]), report, new UTF8Encoding(false));
            token.ThrowIfCancellationRequested();

            // Serialize publication of one stem; a locked output must not leave a mixed set of files.
            string fullStem = Path.Combine(output.FullName, stem).ToUpperInvariant();
            string mutexName = "RocoPlayer.Arrange." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullStem)));
            using var mutex = new Mutex(false, mutexName);
            try
            {
                if (!mutex.WaitOne(0)) throw new IOException("另一个进程正在写入同名编配结果，请重试。");
            }
            catch (AbandonedMutexException) { /* This process now owns the mutex. */ }
            try
            {
                var published = new Stack<string>();
                try
                {
                    foreach (string name in names)
                    {
                        string from = Path.Combine(stage, name), to = Path.Combine(output.FullName, name);
                        if (File.Exists(to)) File.Replace(from, to, from + ".bak");
                        else File.Move(from, to);
                        published.Push(name);
                    }
                }
                catch
                {
                    preserveBackups = true;
                    while (published.TryPop(out string? name))
                    {
                        string backup = Path.Combine(stage, name + ".bak"), to = Path.Combine(output.FullName, name);
                        if (File.Exists(backup)) File.Move(backup, to, overwrite: true);
                        else File.Delete(to);
                    }
                    preserveBackups = false;
                    throw;
                }
            }
            finally { mutex.ReleaseMutex(); }
        }
        finally
        {
            if (!preserveBackups)
            {
                foreach (string name in names)
                {
                    File.Delete(Path.Combine(stage, name));
                    File.Delete(Path.Combine(stage, name + ".bak"));
                }
                Directory.Delete(stage);
            }
        }
    }
}
