using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Analysis;

public enum DeploymentRisk
{
    /// <summary>An additive change that does not touch existing data.</summary>
    Safe = 0,

    /// <summary>Applied in place with ALTER COLUMN. Data is preserved.</summary>
    InPlace = 1,

    /// <summary>The deployment is blocked if the table has rows (SSDT reports "possible data loss").</summary>
    BlockedIfNotEmpty = 2,

    /// <summary>Data loss is certain because a column is dropped or a type is narrowed.</summary>
    DataLoss = 3,
}

/// <param name="Conditional">
/// The block is not certain and depends on the data. Adding a UNIQUE, CHECK or FK constraint,
/// or making a column NOT NULL, only fails when existing rows break the rule. Adding a NOT NULL
/// column without a DEFAULT, dropping a column or narrowing a type is always certain.
/// </param>
public sealed record RiskFinding(string Column, DeploymentRisk Risk, string Description, bool Conditional = false);

public sealed record TableRisk(
    ObjectKey Table,
    DeploymentRisk Risk,
    long? TargetRowCount,
    IReadOnlyList<RiskFinding> Findings)
{
    /// <summary>
    /// Changes that will actually stop the deployment: the change is risky and the target has data.
    /// An unknown row count (null) also counts as risky. When in doubt we round towards the risky
    /// side, because a false "risky" is much cheaper than a false "safe".
    /// </summary>
    public bool WillBlock => Risk >= DeploymentRisk.BlockedIfNotEmpty && TargetRowCount is null or > 0;

    /// <summary>
    /// True when the block only comes from conditional findings. The deployment may still pass
    /// if the existing data satisfies the rule, so the UI can show "check" instead of "blocked".
    /// False as soon as there is a certain block or data loss.
    /// </summary>
    public bool ConditionalOnly => WillBlock &&
        Findings.Where(f => f.Risk >= DeploymentRisk.BlockedIfNotEmpty).All(f => f.Conditional);
}
