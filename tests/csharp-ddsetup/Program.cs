using RocoPlayer.DDSetup;
using RocoPlayer.Playback;
using System.Text.Json;

var temp = Directory.CreateTempSubdirectory("RocoPlayer-DDSetupChecks-");
try
{
    var missing = new DriverState(new(false, 0, 0, 0), []);
    var healthy = new DriverState(new(true, 4, 0, 0), [new Device("mock", 0, 8, 0)]);
    var broken = healthy with { Devices = [new Device("mock", 0, 8, 31)] };
    var serviceOnly = healthy with { Devices = [] };
    int number = 0;
    void Scenario(string name, DriverState initial, int expected, int? vendorExit, bool shouldLaunch = false,
        SetupMode mode = SetupMode.Install, bool pending = false, DriverState? after = null, bool verifyForReal = false)
    {
        string folder = Directory.CreateDirectory(Path.Combine(temp.FullName, (++number).ToString())).FullName;
        int starts = 0, inspections = 0;
        string marker = Path.Combine(folder, "dd-install-pending.json");
        if (pending) File.WriteAllText(marker, "{}");
        var setup = new DriverSetup(() => ++inspections == 1 ? initial : after ?? initial,
            (_, _) =>
            {
                starts++;
                return new FakeProcess(vendorExit);
            }, verifyForReal ? DriverSetup.VerifyFiles : _ => new(), _ => { });
        int exit = setup.Run(temp.FullName, folder, mode);
        if (exit != expected || starts != (shouldLaunch ? 1 : 0)) throw new Exception(name);
        bool timeoutMarker = shouldLaunch && vendorExit == null;
        if (File.Exists(marker) != (pending || timeoutMarker)) throw new Exception(name + " pending marker");
        var lines = File.ReadAllLines(Directory.GetFiles(folder, "*.jsonl").Single());
        using var last = JsonDocument.Parse(lines[^1]);
        if (last.RootElement.GetProperty("code").GetInt32() != expected) throw new Exception(name + " log");
    }

    Scenario("healthy", healthy, 0, null);
    Scenario("missing inspect", missing, 2, null, mode: SetupMode.Inspect);
    Scenario("preflight missing driver", missing, 3, null, mode: SetupMode.CheckInstall, verifyForReal: true);
    Scenario("preflight installed driver", healthy, 0, null, mode: SetupMode.CheckInstall, verifyForReal: true);
    Scenario("preflight existing service", serviceOnly, 2, null, mode: SetupMode.CheckInstall);
    Scenario("preflight existing device only", missing with { Devices = healthy.Devices }, 2, null, mode: SetupMode.CheckInstall);
    Scenario("preflight pending installation", missing, 2, null, mode: SetupMode.CheckInstall, pending: true);
    Scenario("broken service", broken, 2, null);
    Scenario("service without device", serviceOnly, 2, null);
    Scenario("pending", missing, 2, null, pending: true);
    Scenario("missing package", missing, 2, null, verifyForReal: true);
    Scenario("vendor timeout", missing, 3010, null, true);
    Scenario("vendor error", missing, 2, 9, true);
    Scenario("reboot", missing, 3010, 3010, true);
    Scenario("installed", missing, 0, 0, true, after: healthy);
    Scenario("device never ready", missing, 2, 0, true);

    foreach (string name in DdPackage.Runtimes.Concat(DdPackage.Hashes.Keys.Where(k => k.StartsWith("drv/")).Select(k => k[4..])))
        File.WriteAllText(Path.Combine(temp.FullName, name), "mock");
    Scenario("hash mismatch", missing, 2, null, verifyForReal: true);
    Console.WriteLine($"DDSetup mock checks passed ({number} scenarios).");
}
finally
{
    if (temp.Parent?.FullName.TrimEnd(Path.DirectorySeparatorChar) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
        && temp.Name.StartsWith("RocoPlayer-DDSetupChecks-", StringComparison.Ordinal))
        temp.Delete(true);
}

sealed class FakeProcess(int? exitCode) : IVendorProcess
{
    public int Pid => 1;
    public int? WaitForExit(TimeSpan timeout) =>
        timeout == TimeSpan.FromSeconds(120) ? exitCode : throw new InvalidOperationException("wrong timeout");
    public void Dispose() { }
}
