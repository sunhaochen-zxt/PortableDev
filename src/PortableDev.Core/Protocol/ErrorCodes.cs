namespace PortableDev.Core.Protocol;

/// <summary>Stable error codes (spec 6.2). Never localize these.</summary>
public static class ErrorCodes
{
    public const string UnknownCommand = "ERR_UNKNOWN_COMMAND";
    public const string BadArgs = "ERR_BAD_ARGS";
    public const string ExtMissing = "ERR_EXT_MISSING";
    public const string Unsupported = "ERR_UNSUPPORTED";
    public const string ExtTimeout = "ERR_EXT_TIMEOUT";
    public const string Cancelled = "ERR_CANCELLED";
    public const string PromptTimeout = "ERR_PROMPT_TIMEOUT";
    public const string Protocol = "ERR_PROTOCOL";
    public const string Busy = "ERR_BUSY";
    public const string Assert = "ERR_ASSERT";
    public const string PackageInvalid = "ERR_PKG_INVALID";
    public const string PackageNotFound = "ERR_PKG_NOT_FOUND";
    public const string PackageAmbiguous = "ERR_PKG_AMBIGUOUS";
    public const string PackageYanked = "ERR_PKG_YANKED";
    public const string PackagePlatform = "ERR_PKG_PLATFORM";
    public const string SourceUnreachable = "ERR_SOURCE_UNREACHABLE";
    public const string OfflineMiss = "ERR_OFFLINE_MISS";
    public const string DependencyUnsatisfied = "ERR_DEP_UNSATISFIED";
    public const string Elevation = "ERR_ELEVATION";
    public const string IndexInvalid = "ERR_INDEX_INVALID";
    public const string Io = "ERR_IO";
    public const string Net = "ERR_NET";
    public const string Hash = "ERR_HASH";
    public const string Timeout = "ERR_TIMEOUT";
    public const string LogProviderMissing = "ERR_LOG_PROVIDER_MISSING";
    public const string AuditExpired = "ERR_AUDIT_EXPIRED";
    public const string LogProviderConflict = "ERR_LOG_PROVIDER_CONFLICT";
    public const string Env = "ERR_ENV";
    public const string Internal = "ERR_INTERNAL";
    public const string ProcNotFound = "ERR_PROC_NOT_FOUND";
    public const string ProcProtected = "ERR_PROC_PROTECTED";
}

/// <summary>Exit-code mapping (commands.md section 3 / 6.3).</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int Internal = 1;
    public const int Args = 2;
    public const int Data = 3;
    public const int Env = 4;
    public const int Permission = 8;
    public const int Busy = 10;
    public const int Rejected = 11;
    public const int Assert = 12;
    public const int Network = 15;
    public const int NotFound = 20;
    public const int Ambiguous = 21;
    public const int Yanked = 22;
    public const int OfflineMiss = 23;
    public const int Dependency = 24;
    public const int Timeout = 25;
    public const int Cancelled = 130;

    /// <summary>Maps an `ERR_*` code to the documented exit code.</summary>
    public static int ForError(string? code) => code switch
    {
        null or "" => Ok,
        ErrorCodes.BadArgs or ErrorCodes.UnknownCommand => Args,
        ErrorCodes.Io or ErrorCodes.Internal => Internal,
        ErrorCodes.IndexInvalid or ErrorCodes.PackageInvalid or ErrorCodes.Hash => Data,
        ErrorCodes.Env or ErrorCodes.Protocol => Env,
        ErrorCodes.Elevation => Permission,
        ErrorCodes.Busy => Busy,
        ErrorCodes.Unsupported or ErrorCodes.PackagePlatform => Rejected,
        ErrorCodes.Assert => Assert,
        ErrorCodes.SourceUnreachable or ErrorCodes.Net => Network,
        ErrorCodes.PackageNotFound or ErrorCodes.ExtMissing => NotFound,
        ErrorCodes.PackageAmbiguous => Ambiguous,
        ErrorCodes.PackageYanked => Yanked,
        ErrorCodes.OfflineMiss => OfflineMiss,
        ErrorCodes.DependencyUnsatisfied => Dependency,
        ErrorCodes.Timeout or ErrorCodes.ExtTimeout or ErrorCodes.PromptTimeout => Timeout,
        ErrorCodes.Cancelled => Cancelled,
        ErrorCodes.LogProviderMissing => NotFound,
        ErrorCodes.AuditExpired => NotFound,
        ErrorCodes.LogProviderConflict => Busy,
        _ => Internal,
    };
}
