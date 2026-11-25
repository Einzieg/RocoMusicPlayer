using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RocoPlayer.Playback;

namespace RocoPlayer.DDSetup;

internal interface IVendorProcess : IDisposable
{
    int Pid { get; }
    int? WaitForExit(TimeSpan timeout);
}

internal enum SetupMode { Install, Inspect, CheckInstall }

internal sealed class DriverSetup(Func<DriverState> inspect, Func<string, string, IVendorProcess> launch,
    Func<string, Dictionary<string, string>> verify, Action<TimeSpan> sleep)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static Dictionary<string, string> VerifyFiles(string driver)
    {
        var result = new Dictionary<string, string>();
        foreach (string name in DdPackage.Runtimes.Concat(DdPackage.Hashes.Keys
                     .Where(path => path.StartsWith("drv/", StringComparison.Ordinal)).Select(path => path[4..])))
        {
            using var file = File.OpenRead(Path.Combine(driver, name));
            string hash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            if (DdPackage.Hashes.TryGetValue("drv/" + name, out var expected) && hash != expected)
                throw new InvalidDataException($"厂商文件校验不匹配：{name}");
            result[name] = hash;
        }
        return result;
    }

    public int Run(string driver, string logs, SetupMode mode)
    {
        Directory.CreateDirectory(logs);
        var session = $"{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}";
        string events = Path.Combine(logs, $"dd-{session}.jsonl");
        string vendorLog = Path.Combine(logs, $"dd-{session}-vendor.log");
        string pending = Path.Combine(logs, "dd-install-pending.json");
        void Record(string action, object fields)
        {
            var entry = JsonSerializer.SerializeToNode(fields, Json)!.AsObject();
            entry["time"] = DateTimeOffset.UtcNow;
            entry["event"] = action;
            File.AppendAllText(events, entry.ToJsonString() + Environment.NewLine, new UTF8Encoding(false));
        }
        void Snapshot(string action, DriverState value, int attempt) => Record(action, new
        {
            attempt, value.Service.Registered, value.Service.State, value.Service.Win32Exit,
            value.Service.ServiceExit, value.Devices
        });
        int Finish(int code, string reason)
        {
            Record("result", new { code, reason });
            Console.WriteLine($"DD setup: {reason}. Log: {events}");
            return code;
        }

        try
        {
            Record("start", new { driver_directory = driver, vendor_log = vendorLog });
            using var exclusive = mode != SetupMode.Install ? null : new FileStream(Path.Combine(logs, "dd-install.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var before = inspect();
            Snapshot("before", before, 0);
            if (mode == SetupMode.Inspect) return Finish(before.Healthy ? 0 : 2, "read_only_inspection");
            if (before.Healthy) return Finish(0, "service_and_pnp_ready_not_input_tested");
            if (before.Service.Registered || before.Devices.Length != 0)
                return Finish(2, "existing_driver_unhealthy_reinstallation_skipped");
            if (File.Exists(pending)) return Finish(2, "previous_install_timed_out_manual_review_required");
            if (mode == SetupMode.CheckInstall) return Finish(3, "driver_missing");
            Record("files", new { sha256 = verify(driver) });

            // A crash or timeout after launch must leave enough state to prevent a second install.
            File.WriteAllText(pending, JsonSerializer.Serialize(new { log = events, state = "launching" }));
            IVendorProcess process;
            try { process = launch(driver, vendorLog); }
            catch { File.Delete(pending); throw; }
            int? exit;
            using (process)
            {
                File.WriteAllText(pending, JsonSerializer.Serialize(new { pid = process.Pid, log = events }));
                Record("vendor_started", new { pid = process.Pid });
                exit = process.WaitForExit(TimeSpan.FromSeconds(120));
                if (exit == null) return Finish(3010, "vendor_timeout_do_not_rerun_restart_and_review");
            }
            File.Delete(pending);
            Record("vendor_exit", new { code = exit });
            if (exit != 0)
            {
                try { Snapshot("after", inspect(), 0); }
                catch (Exception ex) { Record("inspection_failed", new { message = ex.Message }); }
                return exit == 3010 ? Finish(3010, "vendor_requested_restart") : Finish(2, $"vendor_failed: {exit}");
            }

            for (int attempt = 0; attempt <= 10; attempt++)
            {
                if (attempt > 0) sleep(TimeSpan.FromSeconds(1));
                var after = inspect();
                Snapshot("after", after, attempt);
                if (after.Healthy) return Finish(0, "service_and_pnp_ready_not_input_tested");
            }
            return Finish(2, "service_or_pnp_not_ready_after_install");
        }
        catch (FileNotFoundException ex)
        {
            return Finish(2, $"missing_file: {ex.FileName}");
        }
        catch (Exception ex)
        {
            Record("exception", new { type = ex.GetType().Name, message = ex.Message });
            return Finish(2, "setup_or_state_query_failed");
        }
    }
}
