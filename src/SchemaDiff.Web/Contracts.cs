namespace SchemaDiff.Web;

// Types exchanged with the browser. A password only ever travels from the client to the server
// and is never part of any response.

public sealed record ConnectionDto(
    string Server,
    string? Database = null,
    string Authentication = "Windows",
    string? UserName = null,
    string? Password = null,
    bool RememberPassword = false,
    bool Encrypt = false,
    bool TrustServerCertificate = true,
    string? Id = null);

public sealed record RecentConnectionDto(
    string Id,
    string Server,
    string? Database,
    string Authentication,
    string? UserName,
    bool Encrypt,
    bool TrustServerCertificate,
    bool HasStoredPassword,
    string Label,
    DateTimeOffset LastUsed);

/// <summary>
/// Comparison options, aligned with the Schema Compare options of SSDT. Only options that the
/// comparison really applies are listed, a checkbox that does nothing would be misleading.
/// </summary>
public sealed record CompareOptionsDto(
    bool IgnoreWhitespace = true,
    bool IgnoreComments = true,
    bool IgnoreKeywordCasing = false,
    bool IgnoreSemicolons = false,
    bool IgnoreColumnOrder = false,
    bool IgnoreCollation = false,
    bool IgnoreIdentitySeed = false,
    bool IgnoreIdentityIncrement = false,
    bool IgnoreIndexPhysical = false,
    bool IgnoreFillFactor = true,
    bool IgnoreIndexPadding = false,
    bool IgnoreDataCompression = false,
    bool IgnoreAnsiNulls = true,
    bool IgnoreQuotedIdentifiers = true,
    bool IgnoreDmlTriggerState = false,
    bool IgnoreSystemNamedConstraints = true,
    bool CaseSensitiveNames = false,
    bool CaseSensitiveColumnNames = false,
    int MaxQueries = 16);

public sealed record CompareRequest(
    ConnectionDto Source,
    ConnectionDto Target,
    CompareOptionsDto? Options);

/// <summary>One object that differs. Action is Add, Change, Delete or Indeterminate, seen from the target.</summary>
public sealed record ObjectDiffDto(
    string Action,
    string ObjectType,
    string Schema,
    string Name,
    string[] ChangedParts);

public sealed record CompareResultDto(
    string RunId,
    string SourceLabel,
    string TargetLabel,
    int SourceObjects,
    int TargetObjects,
    int Equal,
    int AddCount,
    int ChangeCount,
    int DeleteCount,
    int IndeterminateCount,
    double DurationMs,
    string[] Warnings,
    ObjectDiffDto[] Changes,
    bool Truncated);
