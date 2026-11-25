using System.Text.Json.Nodes;
using PortableDev.Core.Config;
using PortableDev.Core.Logging;
using PortableDev.Core.Util;

namespace PortableDev.Kernel.Recovery;

public sealed record RecoveryReport(int Adopted, int Dropped, int IntentErrors, int StagingDeleted, int RecordsDeleted)
{
    public static RecoveryReport Empty { get; } = new(0, 0, 0, 0, 0);
}

/// <summary>
/// Startup recovery: settle dangling intents first, then sweep staging directories that no intent claims
/// (architecture 11.2 / kernel.md 5.2). Only `done` counts as committed.
/// </summary>
public sealed class RecoveryService
{
    private readonly KernelPaths _paths;
    private readonly SettingsStore _settings;
    private readonly KernelLogger _logger;

    public RecoveryService(KernelPaths paths, SettingsStore settings, KernelLogger logger)
    {
        _paths = paths;
        _settings = settings;
        _logger = logger;
    }

    public RecoveryReport Run()
    {
        var adopted = 0;
        var dropped = 0;
        var errors = 0;
        var recordsDeleted = 0;
        var intents = new List<(string Path, JsonObject Document)>();
        var claimedTasks = new HashSet<string>(StringComparer.Ordinal);

        Directory.CreateDirectory(_paths.Intents);
        foreach (var file in Directory.EnumerateFiles(_paths.Intents, "*.json").OrderBy(file => file, StringComparer.Ordinal))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject document)
                {
                    errors++;
                    continue;
                }

                intents.Add((file, document));
                var task = JsonUtil.String(document, "task");
                if (task is not null)
                {
                    claimedTasks.Add(task);
                }
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                errors++;
                _logger.Warn("recovery.intent", "unreadable intent file", new JsonObject { ["file"] = file, ["error"] = ex.Message });
            }
        }

        var defaultPolicy = _settings.GetString("recovery.intentDefault") ?? "adopt";
        foreach (var (file, document) in intents)
        {
            try
            {
                var (a, d, r) = SettleIntent(file, document, defaultPolicy);
                adopted += a;
                dropped += d;
                recordsDeleted += r;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // One broken intent must never stop the kernel from booting; leave it for the next run.
                errors++;
                _logger.Warn("recovery.intent", "cannot settle intent; leaving it for the next boot", new JsonObject
                {
                    ["file"] = file,
                    ["error"] = ex.Message,
                });
            }
        }

        (int Adopted, int Dropped, int RecordsDeleted) SettleIntent(string file, JsonObject document, string policy)
        {
            var phase = JsonUtil.String(document, "phase") ?? JsonUtil.String(document, "阶段") ?? "begin";
            var task = JsonUtil.String(document, "task") ?? Path.GetFileNameWithoutExtension(file);
            var target = JsonUtil.String(document, "target");
            var packageId = JsonUtil.String(document, "pkg") ?? JsonUtil.String(document, "id") ?? task;
            var recordPath = Path.Combine(_paths.Records, packageId + ".json");

            if (string.Equals(phase, "done", StringComparison.Ordinal))
            {
                SafeDelete(file);
                return (0, 0, 0);
            }

            var targetExists = target is not null && Directory.Exists(target);
            var recordExists = File.Exists(recordPath);

            // New-format intents carry the record they were about to write: finishing it is safe and lossless.
            if (document["afterRecord"] is JsonObject afterRecord)
            {
                if (targetExists)
                {
                    AtomicFile.WriteAllText(recordPath, afterRecord.ToJsonString());
                }

                SafeDelete(file);
                return (0, 0, 0);
            }

            if (targetExists && recordExists)
            {
                SafeDelete(file);
                return (0, 0, 0);
            }

            if (targetExists && !recordExists)
            {
                if (string.Equals(policy, "drop", StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteDirectory(target!);
                    SafeDelete(file);
                    return (0, 1, 0);
                }

                WriteRecoveredRecord(recordPath, document, packageId, target!);
                SafeDelete(file);
                return (1, 0, 0);
            }

            // The intent only proves the *new* version never landed; the existing record is still valid.
            SafeDelete(file);
            return (0, 0, 0);
        }

        var stagingDeleted = SweepStaging(claimedTasks);
        _logger.Info("recovery.done", "startup recovery finished", new JsonObject
        {
            ["adopted"] = adopted,
            ["dropped"] = dropped,
            ["intentErrors"] = errors,
            ["recordsDeleted"] = recordsDeleted,
            ["stagingDeleted"] = stagingDeleted,
        });

        return new RecoveryReport(adopted, dropped, errors, stagingDeleted, recordsDeleted);
    }

    private int SweepStaging(HashSet<string> claimedTasks)
    {
        var deleted = 0;
        if (!Directory.Exists(_paths.Staging))
        {
            return deleted;
        }

        foreach (var directory in Directory.EnumerateDirectories(_paths.Staging))
        {
            var name = Path.GetFileName(directory);
            if (claimedTasks.Contains(name))
            {
                continue;
            }

            if (TryDeleteDirectory(directory))
            {
                deleted++;
            }
        }

        return deleted;
    }

    private void WriteRecoveredRecord(string path, JsonObject intent, string packageId, string target)
    {
        var record = new JsonObject
        {
            ["schema"] = 1,
            ["id"] = packageId,
            ["version"] = JsonUtil.String(intent, "version") ?? "0.0.0",
            ["origin"] = "recovery",
            ["storePath"] = target,
            ["hashes"] = new JsonObject { ["tree"] = null, ["archive"] = null },
            ["wiring"] = new JsonObject
            {
                ["path"] = new JsonArray(),
                ["env"] = new JsonObject(),
                ["registry"] = new JsonArray(),
                ["launch"] = new JsonArray(),
            },
            ["needsReapply"] = true,
            ["recoveredAt"] = TimeUtil.NowIso(),
            ["intent"] = intent.DeepClone(),
        };

        AtomicFile.WriteAllText(path, record.ToJsonString());
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                return true;
            }
        }
        catch (IOException)
        {
            // Locked by a running task; leave it for the next sweep.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }

        return false;
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Next start will try again.
        }
    }
}
