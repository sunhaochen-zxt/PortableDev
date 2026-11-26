using System.Text;
using System.Text.Json.Nodes;
using PortableDev.Core.Config;
using PortableDev.Core.Extensions;
using PortableDev.Core.Identity;
using PortableDev.Core.Logging;
using PortableDev.Core.Protocol;
using PortableDev.Core.Util;
using PortableDev.Core.Versioning;

var tests = new List<(string Name, Func<Task> Body)>
{
    ("framing.roundtrip", FramingRoundTrip),
    ("framing.rejects-zero-length", FramingRejectsZero),
    ("framing.rejects-oversize", FramingRejectsOversize),
    ("envelope.preserves-unknown-fields", EnvelopePreservesUnknownFields),
    ("envelope.requires-kind", EnvelopeRequiresKind),
    ("abi.range", AbiRangeWorks),
    ("settings.defaults-and-validation", SettingsDefaultsAndValidation),
    ("settings.corrupt-file-is-backed-up", SettingsCorruptBackup),
    ("logging.redaction-blocks-env", LoggingRedactionBlocksEnv),
    ("logging.rotates", LoggingRotates),
    ("commands.conflicts-and-suggest", CommandTableBehaviour),
    ("identity.stick-id-shape", StickIdShape),
    ("util.duration-parse", DurationParse),
    ("logging.priority-mapping", LogPriorityMapping),
    ("logging.record-parse-roundtrip", LogRecordRoundTrip),
    ("logging.extension-sanitize", LogExtensionSanitize),
    ("logging.record-truncation", LogRecordTruncation),
    ("settings.log-keys", LogSettingsKeys),
    ("protocol.envelope-version", EnvelopeVersionRejected),
    ("settings.concurrent-set", SettingsConcurrentSet),
    ("logging.provider-conflict", LogProviderConflict),
    ("logging.kernel-redacts-sensitive", KernelLoggerRedactsSensitive),
    ("logging.provider-drop-accounting", LogProviderCountsDrops),
    ("settings.unset-resets-default", SettingsUnsetResetsDefault),
};

var failures = 0;
foreach (var (name, body) in tests)
{
    try
    {
        await body().ConfigureAwait(false);
        Console.WriteLine($"  ok   {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"  FAIL {name}: {ex.Message}");
    }
}

Console.WriteLine($"\n{tests.Count - failures} passed, {failures} failed");
return failures == 0 ? 0 : 1;

static Task FramingRoundTrip()
{
    using var stream = new MemoryStream();
    var first = Encoding.UTF8.GetBytes("{\"a\":1}");
    var second = Encoding.UTF8.GetBytes("{\"b\":2}");
    FrameCodec.WriteFrameAsync(stream, first, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    FrameCodec.WriteFrameAsync(stream, second, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    stream.Position = 0;
    var readFirst = FrameCodec.ReadFrameAsync(stream, ProtocolConstants.MaxFrameBytes, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    var readSecond = FrameCodec.ReadFrameAsync(stream, ProtocolConstants.MaxFrameBytes, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    var eof = FrameCodec.ReadFrameAsync(stream, ProtocolConstants.MaxFrameBytes, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    Assert(readFirst is not null && readFirst.AsSpan().SequenceEqual(first), "first frame mismatch");
    Assert(readSecond is not null && readSecond.AsSpan().SequenceEqual(second), "second frame mismatch");
    Assert(eof is null, "expected clean EOF");
    return Task.CompletedTask;
}

static Task FramingRejectsZero()
{
    using var stream = new MemoryStream();
    stream.Write([0, 0, 0, 0]);
    stream.Position = 0;
    try
    {
        FrameCodec.ReadFrameAsync(stream, 1024, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        throw new InvalidOperationException("expected ProtocolException");
    }
    catch (ProtocolException)
    {
        return Task.CompletedTask;
    }
}

static Task FramingRejectsOversize()
{
    using var stream = new MemoryStream();
    stream.Write([0xFF, 0xFF, 0xFF, 0x7F]);
    stream.Position = 0;
    try
    {
        FrameCodec.ReadFrameAsync(stream, 1024, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        throw new InvalidOperationException("expected ProtocolException");
    }
    catch (ProtocolException)
    {
        return Task.CompletedTask;
    }
}

static Task EnvelopePreservesUnknownFields()
{
    var node = JsonNode.Parse("""
        {"v":1,"kind":"x-future","id":"t1","seq":3,"data":{"a":1},"meta":{},"x-extra":{"deep":true},"futureField":"keep"}
        """)!;
    var envelope = Envelope.Parse(node);
    Assert(envelope.Kind == "x-future", "kind");
    var round = Envelope.Parse(JsonNode.Parse(Encoding.UTF8.GetString(envelope.ToBytes())))!;
    Assert(round.Extras["x-extra"] is JsonObject, "x-extra lost");
    Assert(JsonUtil.String(round.Extras, "futureField") == "keep", "futureField lost");
    return Task.CompletedTask;
}

static Task EnvelopeRequiresKind()
{
    try
    {
        Envelope.Parse(JsonNode.Parse("""{"v":1,"data":{}}"""));
        throw new InvalidOperationException("expected ProtocolException");
    }
    catch (ProtocolException)
    {
        return Task.CompletedTask;
    }
}

static Task AbiRangeWorks()
{
    Assert(AbiRange.TryParse(">=1 <3", out var range) && range.Matches(1) && range.Matches(2) && !range.Matches(3), "range match");
    Assert(AbiRange.TryParse("==2", out var exact) && !exact.Matches(1) && exact.Matches(2), "exact match");
    Assert(AbiRange.TryParse(">=1", out var open) && open.Matches(9), "open range");
    Assert(!AbiRange.TryParse("~1", out _), "tilde must not parse for ABI");
    Assert(AbiRange.Unbounded.Matches(0), "unbounded");
    return Task.CompletedTask;
}

static Task SettingsDefaultsAndValidation()
{
    var dir = TempDir();
    var path = Path.Combine(dir, "settings.json");
    var store = SettingsStore.Load(path);
    Assert(store.GetInt("install.maxConcurrent", -1) == 2, "default maxConcurrent");
    Assert(store.GetDuration("gc.stagingMaxAge", TimeSpan.Zero) == TimeSpan.FromHours(24), "default staging age");
    var good = store.Set("install.maxConcurrent", "4");
    Assert(good.Ok, "set 4");
    var bad = store.Set("install.maxConcurrent", "99");
    Assert(!bad.Ok && bad.Error is not null, "99 must be rejected");
    var badEnum = store.Set("ui.theme", "purple");
    Assert(!badEnum.Ok, "purple must be rejected");
    var unknown = store.Set("x.custom", "keep");
    Assert(unknown.Ok && unknown.UnknownKey, "unknown key kept");
    var reloaded = SettingsStore.Load(path);
    Assert(reloaded.GetInt("install.maxConcurrent", -1) == 4, "persisted value");
    Assert(JsonUtil.String(reloaded.Root, "x") is not null, "unknown key persisted");
    return Task.CompletedTask;
}

static Task SettingsCorruptBackup()
{
    var dir = TempDir();
    var path = Path.Combine(dir, "settings.json");
    File.WriteAllText(path, "{ not json");
    var store = SettingsStore.Load(path);
    Assert(store.WasReset, "must report reset");
    Assert(store.BackupPath is not null && File.Exists(store.BackupPath), "backup file exists");
    Assert(store.GetInt("install.maxConcurrent", -1) == 2, "defaults restored");
    return Task.CompletedTask;
}

static Task LoggingRedactionBlocksEnv()
{
    var fingerprint = new EnvFingerprint(7);
    Assert(fingerprint.ToString().Contains("7 environment variables", StringComparison.Ordinal), "fingerprint text");
    Assert(!fingerprint.ToString().Contains("TOKEN", StringComparison.Ordinal), "no names");
    // A whole snapshot is dropped (recursively), never thrown: logging must not fail the caller.
    var snapshot = new JsonObject { ["env"] = new JsonObject { ["A"] = "B" } };
    Assert(LogRedaction.Validate(snapshot) == 1, "bulk snapshot dropped");
    Assert(!snapshot.ContainsKey("env"), "snapshot field removed");
    var nestedSnapshot = new JsonObject { ["details"] = new JsonObject { ["env"] = new JsonObject { ["A"] = "B" } } };
    Assert(LogRedaction.Validate(nestedSnapshot) == 1, "nested bulk snapshot dropped");
    Assert(!((JsonObject)nestedSnapshot["details"]!).ContainsKey("env"), "nested snapshot field removed");

    // Named values are allowed, including sensitive ones (they get marked, not dropped).
    LogRedaction.Validate(new JsonObject { ["environment.proxy"] = "http://x" });
    LogRedaction.Validate(new JsonObject { ["token"] = "abc" });
    Assert(LogRedaction.IsSensitive("GITHUB_TOKEN"), "sensitive name");
    Assert(!LogRedaction.IsSensitive("pkg"), "ordinary name");
    Assert(LogRedaction.IsBulkSnapshot("env"), "bulk name");
    Assert(!LogRedaction.IsBulkSnapshot("env.PATH"), "named value is not bulk");
    return Task.CompletedTask;
}

static Task LoggingRotates()
{
    var dir = TempDir();
    var options = new LogOptions(dir, "kernel") { Level = LogLevel.Debug, MaxSizeBytes = 200, KeepFiles = 2 };
    using (var logger = new JsonlLogger(options))
    {
        for (var i = 0; i < 8; i++)
        {
            logger.Info("test.event", $"line {i}");
        }
    }

    Assert(File.Exists(options.PrimaryPath), "primary log exists");
    Assert(File.Exists(options.RotatedPath(1)), "rotated log exists");
    return Task.CompletedTask;
}

static Task CommandTableBehaviour()
{
    var table = new CommandTable();
    var first = new CommandEntry { Name = "kernel status", ExtensionName = null, IsBuiltin = true, Parallel = true };
    Assert(table.TryRegister(first) is null, "register builtin");
    var conflict = table.TryRegister(new CommandEntry { Name = "status", ExtensionName = "x" });
    Assert(conflict is null, "alias-free name ok");
    var aliasConflict = table.TryRegister(new CommandEntry { Name = "kernel ps", Aliases = ["kernel status"], ExtensionName = "x" });
    Assert(aliasConflict == "kernel status", "alias conflict detected");
    Assert(table.Suggest("statuz") == "status", "suggestion");
    return Task.CompletedTask;
}

static Task StickIdShape()
{
    var id = StickIdentity.ComputeId(0x2D92C1B3, "PortableSys");
    Assert(id.StartsWith("pd-", StringComparison.Ordinal), "prefix");
    Assert(id.Length == 15, "12 hex chars");
    Assert(id == StickIdentity.ComputeId(0x2D92C1B3, "PortableSys"), "deterministic");
    Assert(id != StickIdentity.ComputeId(0x2D92C1B4, "PortableSys"), "serial changes id");
    Assert(id != StickIdentity.ComputeId(0x2D92C1B3, "Other"), "label changes id");
    return Task.CompletedTask;
}

static Task LogPriorityMapping()
{
    Assert(LogRecord.Priority(LogLevel.Error) == 3, "error=3");
    Assert(LogRecord.Priority(LogLevel.Warn) == 4, "warn=4");
    Assert(LogRecord.Priority(LogLevel.Info) == 6, "info=6");
    Assert(LogRecord.Priority(LogLevel.Debug) == 7, "debug=7");
    Assert(LogRecord.LevelFromPriority(3) == LogLevel.Error, "3->error");
    Assert(LogRecord.LevelFromPriority(7) == LogLevel.Debug, "7->debug");
    return Task.CompletedTask;
}

static Task LogRecordRoundTrip()
{
    var original = new LogRecord
    {
        Ts = "2026-11-24T16:00:00.000+08:00",
        Pri = 6,
        Level = "info",
        Msg = "hello",
        Evt = "test.event",
        Seq = 7,
        BootId = "b-3f9a2c71b004",
        Unit = "kernel",
        Pid = 42,
        StickId = "pd-12c2608283c2",
        MachineId = "m-a5f24fefbd80",
        Transport = "kernel",
        Fields = new JsonObject { ["extra"] = 1 },
    };

    var json = original.ToJson();
    Assert(LogRecord.TryParse(json, out var parsed), "parse");
    Assert(parsed.BootId == original.BootId && parsed.Seq == 7 && parsed.Unit == "kernel", "fields");
    Assert(JsonUtil.Int(json, "extra") == 1, "extra preserved");
    return Task.CompletedTask;
}

static Task LogExtensionSanitize()
{
    var raw = new JsonObject
    {
        ["_bootId"] = "forged",
        ["_unit"] = "forged",
        ["env"] = "TOKEN=x",
        ["token"] = "abc",
        ["msg"] = "ok",
        ["pri"] = 6,
    };

    var record = LogRecord.SanitizeExtension(raw, "echo", "b-true", "pd-x", "m-x", "i-1", 99, out var spoofed, out var redacted);
    Assert(spoofed == 2, $"spoofed={spoofed}");
    Assert(redacted == 1, $"redacted={redacted}");
    Assert(record.BootId == "b-true" && record.Unit == "echo", "trusted fields win");
    Assert(record.Fields is not null && !record.Fields.ContainsKey("env"), "bulk snapshot removed");
    Assert(record.Fields is not null && record.Fields.ContainsKey("token"), "named value kept");
    Assert(record.Fields?["token"]?.GetValue<string>() == LogRedaction.RedactedPlaceholder, "sensitive value masked");
    Assert(record.Sensitive, "sensitive marked");
    var serialized = LogUtil(record);
    Assert(serialized.Contains("_sensitive", StringComparison.Ordinal), "marker emitted");
    Assert(!serialized.Contains("abc", StringComparison.Ordinal), "plaintext secret must not hit disk");
    return Task.CompletedTask;
}

static Task KernelLoggerRedactsSensitive()
{
    var dir = TempDir();
    var options = new LogOptions(dir, "kernel") { Level = LogLevel.Debug };
    using (var logger = new KernelLogger(options, "boot-1", "pd-x", "m-x"))
    {
        logger.Info("auth.probe", "checked token", new JsonObject
        {
            ["github_token"] = "ghp_supersecret",
            ["env"] = new JsonObject { ["SECRET"] = "nope" },
        });
    }

    var content = File.ReadAllText(options.PrimaryPath);
    Assert(content.Contains("\"_sensitive\":true", StringComparison.Ordinal), "kernel record marked sensitive");
    Assert(content.Contains(LogRedaction.RedactedPlaceholder, StringComparison.Ordinal), "kernel secret masked");
    Assert(!content.Contains("ghp_supersecret", StringComparison.Ordinal), "plaintext kernel secret must not hit disk");
    return Task.CompletedTask;
}

static Task LogProviderCountsDrops()
{
    var dir = TempDir();
    var options = new LogOptions(dir, "kernel") { Level = LogLevel.Debug };
    using var logger = new KernelLogger(options, "boot-1", "pd-x", "m-x");
    logger.Provider.Attach("log-ext", priority: 10);
    for (var i = 0; i < 9000; i++)
    {
        logger.Provider.TryWrite(new LogRecord
        {
            Ts = TimeUtil.NowIso(),
            Pri = 6,
            Level = "info",
            Msg = "flood " + i,
            Evt = "flood",
            BootId = "boot-1",
            Unit = "kernel",
        });
    }

    Assert(logger.Provider.Dropped > 0, $"expected drops > 0, got {logger.Provider.Dropped}");
    return Task.CompletedTask;
}

static Task SettingsUnsetResetsDefault()
{
    var file = Path.Combine(TempDir(), "settings.json");
    var store = SettingsStore.Load(file);
    var set = store.Set("network.timeout", "90s");
    Assert(set.Ok, "set succeeded");
    Assert(store.GetJsonString("network.timeout") == "\"90s\"", "updated json");
    Assert(store.Unset("network.timeout", out var def) && def is not null, "unset succeeded");
    Assert(store.GetString("network.timeout") == "30s", "reset to default");
    return Task.CompletedTask;
}

static Task LogRecordTruncation()
{
    var big = new string('x', LogRecord.MaxBytes);
    var record = new LogRecord
    {
        Ts = "2026-11-24T16:00:00.000+08:00",
        Pri = 6,
        Level = "info",
        Msg = big,
        BootId = "b-1",
        Unit = "kernel",
    };
    var json = record.ToJson();
    Assert(JsonUtil.Bool(json, "_truncated") == true, "truncated flag");
    Assert(System.Text.Encoding.UTF8.GetByteCount(json.ToJsonString()) <= LogRecord.MaxBytes, "size cap");
    return Task.CompletedTask;
}

static Task LogSettingsKeys()
{
    var dir = TempDir();
    var path = Path.Combine(dir, "settings.json");
    var store = SettingsStore.Load(path);
    Assert(store.GetInt("log.retentionDays", -1) == 30, "retentionDays default");
    Assert(store.GetInt("log.batchBytes", -1) == 65536, "batchBytes default");
    Assert(store.GetInt("log.rateLimit", -1) == 5000, "rateLimit default");
    Assert(!store.Set("log.retentionDays", "0").Ok, "0 rejected");
    Assert(!store.Set("log.batchBytes", "1024").Ok, "too small rejected");
    return Task.CompletedTask;
}

static Task DurationParse()
{
    Assert(TimeUtil.ParseDuration("30s") == TimeSpan.FromSeconds(30), "30s");
    Assert(TimeUtil.ParseDuration("5m") == TimeSpan.FromMinutes(5), "5m");
    Assert(TimeUtil.ParseDuration("24h") == TimeSpan.FromHours(24), "24h");
    Assert(TimeUtil.ParseDuration("0") == TimeSpan.Zero, "0");
    Assert(TimeUtil.ParseDuration("nope") is null, "garbage");
    return Task.CompletedTask;
}

static string LogUtil(LogRecord record) => record.ToJson().ToJsonString();

static string TempDir()
{
    var dir = Path.Combine(Path.GetTempPath(), "pd-test-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(dir);
    return dir;
}

static Task EnvelopeVersionRejected()
{
    try
    {
        Envelope.Parse(Encoding.UTF8.GetBytes("{\"v\":2,\"kind\":\"evt\"}"));
        throw new InvalidOperationException("v=2 must be rejected");
    }
    catch (ProtocolException)
    {
    }

    var ok = Envelope.Parse(Encoding.UTF8.GetBytes("{\"kind\":\"evt\"}"));
    Assert(ok.V == 1, "missing v defaults to 1");
    return Task.CompletedTask;
}

static Task SettingsConcurrentSet()
{
    var dir = TempDir();
    var path = Path.Combine(dir, "settings.json");
    var store = SettingsStore.Load(path);
    var failures = 0;
    Parallel.For(0, 8, index =>
    {
        for (var i = 0; i < 50; i++)
        {
            var result = store.Set("install.maxConcurrent", index % 2 == 0 ? "2" : "3");
            if (!result.Ok)
            {
                Interlocked.Increment(ref failures);
            }
        }
    });

    Assert(failures == 0, failures + " concurrent sets failed");
    var final = SettingsStore.Load(path);
    Assert(final.GetInt("install.maxConcurrent", -1) is 2 or 3, "value stayed valid");
    return Task.CompletedTask;
}

static Task LogProviderConflict()
{
    var dir = TempDir();
    using (var logger = new KernelLogger(new LogOptions(dir, "kernel"), "b-test", "pd-test", "m-test"))
    {
        logger.Provider.Attach("log-a", 10);
        Assert(logger.Provider.ProviderName == "log-a", "first provider attaches");
        logger.Provider.Attach("log-b", 10);
        Assert(logger.Provider.Conflicted, "same priority must conflict");
        Assert(logger.Provider.ProviderName is null, "both providers refused");
        logger.Info("test.conflict", "fallback still works");
    }

    using (var lower = new KernelLogger(new LogOptions(Path.Combine(dir, "lower"), "kernel"), "b-test", "pd-test", "m-test"))
    {
        lower.Provider.Attach("log-a", 10);
        lower.Provider.Attach("log-b", 5);
        Assert(lower.Provider.ProviderName == "log-b", "lower priority wins");
    }

    return Task.CompletedTask;
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
