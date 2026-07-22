using MusicLibrary.Core.Models;
using MusicLibraryTools;

namespace MusicLibrary.Core.Services;

/// <summary>
/// Internal health-check extension point. A rule owns stable identity and documents when it is
/// applicable; profile XML independently controls whether findings are shown and whether repairs
/// may be proposed or applied.
/// </summary>
public interface IHealthRule
{
    string Id { get; }
    string Name { get; }
    string Applicability { get; }
    bool SupportsRepair { get; }
    LibraryHealthRulePolicy DefaultPolicy { get; }
}

public sealed record BuiltInHealthRule(
    string Id,
    string Name,
    string Applicability,
    bool SupportsRepair,
    LibraryHealthRulePolicy DefaultPolicy) : IHealthRule;

public static class BuiltInHealthRules
{
    public static IReadOnlyList<IHealthRule> All { get; } = Create();

    private static IReadOnlyList<IHealthRule> Create()
    {
        IReadOnlyDictionary<string, LibraryHealthRulePolicy> defaults =
            LibraryProfilePresets.Create(LibraryProfilePreset.LegacyMusicLibraryTools)
                .Health.Rules.ToDictionary(rule => rule.Id, StringComparer.OrdinalIgnoreCase);
        IHealthRule Rule(string id, string name, string applicability, bool repair) =>
            new BuiltInHealthRule(id, name, applicability, repair, defaults[id]);
        return
        [
            Rule(LibraryHealthRuleIds.LossyFile, "Lossy file",
                "Applies when the decoded audio codec is lossy.", false),
            Rule(LibraryHealthRuleIds.MissingAlbumArtist, "Missing album artist",
                "Applies when Album Artist is absent or blank.", true),
            Rule(LibraryHealthRuleIds.MissingTrackTotal, "Track numbering",
                "Applies when track number or total is missing, zero, or inconsistent.", true),
            Rule(LibraryHealthRuleIds.DiscMetadata, "Disc metadata",
                "Applies when disc number/total is present, missing, or inconsistent.", true),
            Rule(LibraryHealthRuleIds.Id3Version, "ID3 version",
                "Applies to ID3-tagged formats whose configured version policy is not met.", true),
            Rule(LibraryHealthRuleIds.NormalizeWhitespace, "Text normalization",
                "Applies to configured metadata and path whitespace anomalies.", true),
            Rule(LibraryHealthRuleIds.DiscAlbumTitle, "Disc title migration",
                "Applies only when the profile opts into disc-suffix inference or migration.", true),
        ];
    }
}

/// <summary>
/// Applies a profile's health-rule switches to analyzer reports and repair plans. Missing rule
/// entries inherit legacy behavior so partial/custom policies do not silently suppress existing
/// checks or repairs.
/// </summary>
public interface ILibraryHealthPolicyService
{
    LibraryHealthRulePolicy ResolveRule(LibraryHealthPolicy policy, string ruleId);

    AnalysisReport ApplyToReport(
        AnalysisReport report,
        LibraryHealthPolicy policy,
        string? defaultRuleId = null);

    AnalysisReport ApplyToReport(
        AnalysisReport report,
        LibraryConfiguration configuration,
        string? defaultRuleId = null);

    AnalysisRepairPlan FilterProposedRepairs(
        AnalysisRepairPlan plan,
        LibraryHealthPolicy policy);

    AnalysisRepairPlan FilterProposedRepairs(
        AnalysisRepairPlan plan,
        LibraryConfiguration configuration);

    AnalysisRepairPlan FilterApplicableRepairs(
        AnalysisRepairPlan plan,
        LibraryHealthPolicy policy);

    AnalysisRepairPlan FilterApplicableRepairs(
        AnalysisRepairPlan plan,
        LibraryConfiguration configuration);
}

public sealed class LibraryHealthPolicyService : ILibraryHealthPolicyService
{
    private readonly IReadOnlyDictionary<string, IHealthRule> _rules;

    public static LibraryHealthPolicyService Default { get; } = new();

    public LibraryHealthPolicyService(IEnumerable<IHealthRule>? rules = null)
    {
        IHealthRule[] configured = (rules ?? BuiltInHealthRules.All).ToArray();
        string[] duplicates = configured.GroupBy(rule => rule.Id,
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
            throw new ArgumentException(
                "Health rule IDs must be unique: " + string.Join(", ", duplicates),
                nameof(rules));
        _rules = configured.ToDictionary(rule => rule.Id,
            StringComparer.OrdinalIgnoreCase);
    }

    public LibraryHealthRulePolicy ResolveRule(LibraryHealthPolicy policy, string ruleId)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        return policy.Find(ruleId) ?? _rules.GetValueOrDefault(ruleId)?.DefaultPolicy ??
            new LibraryHealthRulePolicy(
                ruleId,
                Enabled: true,
                Severity: LibraryHealthSeverity.Warning,
                ProposeRepair: true,
                ApplyRepair: true);
    }

    public AnalysisReport ApplyToReport(
        AnalysisReport report,
        LibraryHealthPolicy policy,
        string? defaultRuleId = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(policy);

        var findings = new List<AnalysisFinding>(report.Findings.Count);
        foreach (AnalysisFinding finding in report.Findings)
        {
            string? ruleId = finding.RuleId ?? defaultRuleId;
            if (string.IsNullOrWhiteSpace(ruleId))
            {
                findings.Add(finding);
                continue;
            }

            LibraryHealthRulePolicy rule = ResolveRule(policy, ruleId);
            if (rule.Enabled)
                findings.Add(finding with { RuleId = rule.Id, Severity = rule.Severity });
        }
        return report with { Findings = findings };
    }

    public AnalysisReport ApplyToReport(
        AnalysisReport report,
        LibraryConfiguration configuration,
        string? defaultRuleId = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(configuration);

        var findings = new List<AnalysisFinding>(report.Findings.Count);
        foreach (AnalysisFinding finding in report.Findings)
        {
            string? ruleId = finding.RuleId ?? defaultRuleId;
            if (string.IsNullOrWhiteSpace(ruleId))
            {
                findings.Add(finding);
                continue;
            }

            LibraryHealthRulePolicy rule = ResolveRule(
                ResolvePolicy(configuration, finding.Path), ruleId);
            if (rule.Enabled)
                findings.Add(finding with { RuleId = rule.Id, Severity = rule.Severity });
        }
        return report with { Findings = findings };
    }

    public AnalysisRepairPlan FilterProposedRepairs(
        AnalysisRepairPlan plan,
        LibraryHealthPolicy policy) =>
        FilterRepairs(plan, policy, rule => rule.ProposeRepair);

    public AnalysisRepairPlan FilterProposedRepairs(
        AnalysisRepairPlan plan,
        LibraryConfiguration configuration) =>
        FilterRepairs(plan, configuration, rule => rule.ProposeRepair);

    public AnalysisRepairPlan FilterApplicableRepairs(
        AnalysisRepairPlan plan,
        LibraryHealthPolicy policy) =>
        FilterRepairs(plan, policy, rule => rule.ApplyRepair);

    public AnalysisRepairPlan FilterApplicableRepairs(
        AnalysisRepairPlan plan,
        LibraryConfiguration configuration) =>
        FilterRepairs(plan, configuration, rule => rule.ApplyRepair);

    private AnalysisRepairPlan FilterRepairs(
        AnalysisRepairPlan plan,
        LibraryHealthPolicy policy,
        Func<LibraryHealthRulePolicy, bool> allowed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(policy);

        AnalysisTagRepair[] items = plan.Items.Where(repair =>
        {
            if (string.IsNullOrWhiteSpace(repair.RuleId))
                return true;
            LibraryHealthRulePolicy rule = ResolveRule(policy, repair.RuleId);
            return rule.Enabled && allowed(rule);
        }).ToArray();
        return plan with { Items = items };
    }

    private AnalysisRepairPlan FilterRepairs(
        AnalysisRepairPlan plan,
        LibraryConfiguration configuration,
        Func<LibraryHealthRulePolicy, bool> allowed)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(configuration);

        AnalysisTagRepair[] items = plan.Items.Where(repair =>
        {
            if (string.IsNullOrWhiteSpace(repair.RuleId))
                return true;
            LibraryHealthRulePolicy rule = ResolveRule(
                ResolvePolicy(configuration, repair.Path), repair.RuleId);
            return rule.Enabled && allowed(rule);
        }).ToArray();
        return plan with { Items = items };
    }

    private static LibraryHealthPolicy ResolvePolicy(
        LibraryConfiguration configuration,
        string path)
    {
        LibraryIndexLocation? root = LibraryRootPermissionPolicy.MostSpecific(
            path, configuration.IndexLocations);
        return root is null
            ? configuration.ActiveProfile.Health
            : configuration.GetEffectiveProfile(root).Health;
    }
}
