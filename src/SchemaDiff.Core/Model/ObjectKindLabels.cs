namespace SchemaDiff.Core.Model;

/// <summary>
/// Display name of an object kind and the conversion from that name back to the kind.
///
/// Both directions live in the same file on purpose. When the label and the parser are kept
/// in different places they drift apart without anyone noticing, and a kind that cannot be
/// resolved never makes it into the script even if the user selected it.
/// <c>ObjectKindLabelTests</c> checks the round trip for every kind.
///
/// Names follow the wording used by SSDT.
/// </summary>
public static class ObjectKindLabels
{
    public static string Display(ObjectKind kind) => kind switch
    {
        ObjectKind.Schema => "Schema",
        ObjectKind.Table => "Table",
        ObjectKind.View => "View",
        ObjectKind.Procedure => "Procedure",
        ObjectKind.ScalarFunction => "Scalar Function",
        ObjectKind.InlineTableFunction => "Inline Function",
        ObjectKind.TableFunction => "Table Function",
        ObjectKind.Trigger => "Trigger",
        ObjectKind.Synonym => "Synonym",
        ObjectKind.Sequence => "Sequence",
        ObjectKind.Role => "Role",
        ObjectKind.User => "User",
        ObjectKind.UserDefinedType => "User-Defined Type",
        ObjectKind.TableType => "Table Type",
        ObjectKind.PartitionFunction => "Partition Function",
        ObjectKind.PartitionScheme => "Partition Scheme",
        ObjectKind.DdlTrigger => "DDL Trigger",
        ObjectKind.FullTextCatalog => "Full-Text Catalog",
        ObjectKind.FullTextStoplist => "Full-Text Stoplist",
        ObjectKind.XmlSchemaCollection => "XML Schema Collection",
        ObjectKind.PlanGuide => "Plan Guide",
        ObjectKind.LegacyRuleDefault => "Rule or Default",
        ObjectKind.Database => "Database",
        _ => kind.ToString(),
    };

    /// <summary>
    /// Converts a display name back to its kind. Labels that do not match the enum name, such as
    /// the shortened "Inline Function", are mapped explicitly. The rest are resolved after
    /// removing spaces and hyphens.
    /// </summary>
    public static ObjectKind Resolve(string label) => label switch
    {
        "Scalar Function" => ObjectKind.ScalarFunction,
        "Inline Function" => ObjectKind.InlineTableFunction,
        "Table Function" => ObjectKind.TableFunction,
        "Rule or Default" => ObjectKind.LegacyRuleDefault,
        _ => Parse(label),
    };

    private static ObjectKind Parse(string label)
    {
        var normalized = label.Replace(" ", string.Empty).Replace("-", string.Empty);
        return Enum.TryParse<ObjectKind>(normalized, ignoreCase: true, out var kind)
            ? kind
            : ObjectKind.Unknown;
    }
}
