namespace EmailStat.Models;

/// <summary>
/// A group of emails sharing the same sender address or sender domain.
/// </summary>
public sealed class EmailGroup
{
    /// <summary>Unique key: domain name when <see cref="IsDomain"/> is true, otherwise the full email address.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Human-readable display name (may include the display name from the From header).</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Total number of emails in this group.</summary>
    public long EmailCount { get; init; }

    /// <summary>True when this group represents a domain; false when it represents an exact sender address.</summary>
    public bool IsDomain { get; init; }

    /// <summary>
    /// When <see cref="IsDomain"/> is true, the per-address sub-groups that belong to this domain.
    /// </summary>
    public IReadOnlyList<EmailGroup> SubGroups { get; init; } = [];
}
