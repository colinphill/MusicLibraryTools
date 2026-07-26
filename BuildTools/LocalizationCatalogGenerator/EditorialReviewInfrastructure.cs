using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using MusicLibraryTools.Localization;

internal enum CatalogTranslationRoute
{
    EditorialOverride,
    NativeAutonym,
    BuiltInSpecialCase,
    ExactResource,
    Glossary,
}

internal enum EditorialReviewStatus
{
    Pending,
    InvariantApproved,
    GlossaryReviewed,
    EditorialReviewed,
}

internal sealed record CatalogReviewSource(
    string Key,
    string Neutral,
    IReadOnlyDictionary<string, string> Translations,
    CatalogTranslationRoute Route);

internal sealed record ReviewedCatalogEvidence(
    string Neutral,
    IReadOnlyDictionary<string, string> Translations,
    string Disposition);

internal sealed record EditorialReviewRecord(
    string Key,
    EditorialReviewStatus Status,
    CatalogTranslationRoute Route,
    string Digest,
    string Batch,
    string Reviewer,
    string Date,
    string Disposition);

internal sealed record EditorialReviewManifest(
    IReadOnlyDictionary<string, EditorialReviewRecord> Records);

internal sealed record EditorialReviewSeed(
    IReadOnlyDictionary<string, ReviewedCatalogEvidence> Catalogs,
    string Batch,
    string Reviewer,
    string Date,
    string Identity);

internal static partial class EditorialReviewInfrastructure
{
    public const string DefaultManifestFileName =
        "EditorialReviewManifest.xml";

    public static IReadOnlyList<string> ShippingCultures { get; } =
    [
        "de-DE",
        "es-ES",
        "fr-FR",
        "it-IT",
        "pt-BR",
        "ja-JP",
        "ko-KR",
        "zh-CN",
        "zh-TW",
    ];

    public static EditorialReviewManifest LoadAndValidate(
        string path,
        IReadOnlyList<CatalogReviewSource> sources,
        IReadOnlyDictionary<string, string> invariantApprovedValues,
        bool requireComplete)
    {
        if (!File.Exists(path))
            throw new InvalidDataException(
                $"Editorial review manifest '{path}' is missing. " +
                "Run --refresh-editorial-review-manifest.");

        IReadOnlyDictionary<string, EditorialReviewRecord> records =
            ParseManifest(path);

        ValidateManifest(
            new EditorialReviewManifest(records),
            sources,
            invariantApprovedValues,
            requireComplete);

        ValidateRootSummary(path, records);
        return new EditorialReviewManifest(records);
    }

    public static void ValidateManifest(
        EditorialReviewManifest manifest,
        IReadOnlyList<CatalogReviewSource> sources,
        IReadOnlyDictionary<string, string> invariantApprovedValues,
        bool requireComplete)
    {
        IReadOnlyDictionary<string, EditorialReviewRecord> records =
            manifest.Records;
        var sourceByKey = sources.ToDictionary(
            source => source.Key,
            StringComparer.Ordinal);
        string[] missingKeys =
        [
            .. sourceByKey.Keys
                .Where(key => !records.ContainsKey(key))
                .OrderBy(key => key, StringComparer.Ordinal),
        ];
        string[] unknownKeys =
        [
            .. records.Keys
                .Where(key => !sourceByKey.ContainsKey(key))
                .OrderBy(key => key, StringComparer.Ordinal),
        ];
        if (missingKeys.Length > 0)
            throw new InvalidDataException(
                "Editorial review manifest is missing resources: " +
                string.Join(", ", missingKeys.Take(20)) +
                (missingKeys.Length > 20
                    ? $" (and {missingKeys.Length - 20:N0} more)"
                    : ""));
        if (unknownKeys.Length > 0)
            throw new InvalidDataException(
                "Editorial review manifest references unknown resources: " +
                string.Join(", ", unknownKeys.Take(20)) +
                (unknownKeys.Length > 20
                    ? $" (and {unknownKeys.Length - 20:N0} more)"
                    : ""));

        foreach (CatalogReviewSource source in sources)
        {
            EditorialReviewRecord record = records[source.Key];
            string expectedDigest = ComputeDigest(source);
            if (!string.Equals(
                    record.Digest,
                    expectedDigest,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Editorial review manifest digest is stale for '{source.Key}'. " +
                    "Run --refresh-editorial-review-manifest; stale approvals " +
                    "will be downgraded to Pending.");
            if (record.Route != source.Route)
                throw new InvalidDataException(
                    $"Editorial review manifest route is stale for '{source.Key}': " +
                    $"expected {source.Route}, found {record.Route}.");
            if (record.Status == EditorialReviewStatus.InvariantApproved &&
                (!invariantApprovedValues.TryGetValue(
                     source.Key,
                     out string? approvedValueDigest) ||
                 !IsCatalogInvariant(source) ||
                 !string.Equals(
                     approvedValueDigest,
                     ComputeInvariantValueDigest(source),
                     StringComparison.Ordinal)))
                throw new InvalidDataException(
                    $"Editorial review manifest marks resource " +
                    $"'{source.Key}' as InvariantApproved without both " +
                    "explicit allowlist approval and catalog equality.");
        }
        ValidateInvariantAllowlist(
            invariantApprovedValues,
            sources);
        ValidateCanonicalRecords(
            records,
            sourceByKey,
            invariantApprovedValues);

        if (requireComplete)
        {
            int pending = records.Values.Count(record =>
                record.Status == EditorialReviewStatus.Pending);
            if (pending > 0)
                throw new InvalidDataException(
                    "Strict editorial review failed: " +
                    $"{pending:N0} resources remain Pending.");
        }

    }

    public static EditorialReviewManifest Refresh(
        string path,
        IReadOnlyList<CatalogReviewSource> sources,
        IReadOnlyDictionary<
            string,
            ReviewedCatalogEvidence> reviewedCatalogs,
        IReadOnlyDictionary<string, string> invariantApprovedValues,
        string batch,
        string reviewer,
        string date)
    {
        ValidateReviewMetadata(batch, reviewer, date);

        IReadOnlyDictionary<string, EditorialReviewRecord> existing =
            File.Exists(path)
                ? ParseManifest(path)
                : new Dictionary<string, EditorialReviewRecord>(
                    StringComparer.Ordinal);
        if (File.Exists(path))
            ValidateRootSummary(path, existing);
        var records = new Dictionary<string, EditorialReviewRecord>(
            StringComparer.Ordinal);
        foreach (CatalogReviewSource source in
                 sources.OrderBy(source => source.Key, StringComparer.Ordinal))
        {
            string digest = ComputeDigest(source);
            bool existingIsCurrent =
                existing.TryGetValue(
                    source.Key,
                    out EditorialReviewRecord? existingRecord) &&
                string.Equals(
                    existingRecord.Digest,
                    digest,
                    StringComparison.Ordinal) &&
                existingRecord.Route == source.Route;

            EditorialReviewRecord record;
            if (reviewedCatalogs.TryGetValue(
                    source.Key,
                    out ReviewedCatalogEvidence?
                        reviewedEvidence) &&
                source.Route ==
                    CatalogTranslationRoute.EditorialOverride &&
                string.Equals(
                    source.Neutral,
                    reviewedEvidence.Neutral,
                    StringComparison.Ordinal) &&
                HaveSameTranslations(
                    source.Translations,
                    reviewedEvidence.Translations))
            {
                record = new EditorialReviewRecord(
                    source.Key,
                    EditorialReviewStatus.EditorialReviewed,
                    source.Route,
                    digest,
                    batch,
                    reviewer,
                    date,
                    reviewedEvidence.Disposition);
            }
            else if (existingIsCurrent &&
                     existingRecord!.Status is
                         EditorialReviewStatus.EditorialReviewed or
                         EditorialReviewStatus.GlossaryReviewed)
            {
                record = existingRecord;
            }
            else if (invariantApprovedValues.TryGetValue(
                         source.Key,
                         out string? approvedValueDigest) &&
                     IsCatalogInvariant(source) &&
                     string.Equals(
                         approvedValueDigest,
                         ComputeInvariantValueDigest(source),
                         StringComparison.Ordinal))
            {
                record = new EditorialReviewRecord(
                    source.Key,
                    EditorialReviewStatus.InvariantApproved,
                    source.Route,
                    digest,
                    "catalog-invariant-v1",
                    "LocalizationCatalogGenerator",
                    date,
                    $"invariant:v1:{approvedValueDigest}");
            }
            else if (existingIsCurrent &&
                     existingRecord!.Status ==
                         EditorialReviewStatus.Pending)
            {
                record = existingRecord;
            }
            else
            {
                record = new EditorialReviewRecord(
                    source.Key,
                    EditorialReviewStatus.Pending,
                    source.Route,
                    digest,
                    "editorial-backlog-v1",
                    "Unassigned",
                    date,
                    "pending:v1");
            }
            records.Add(source.Key, record);
        }

        var manifest = new EditorialReviewManifest(records);
        ValidateInvariantAllowlist(
            invariantApprovedValues,
            sources);
        ValidateCanonicalRecords(
            records,
            sources.ToDictionary(
                source => source.Key,
                StringComparer.Ordinal),
            invariantApprovedValues);
        return manifest;
    }

    public static IReadOnlyDictionary<
        string,
        ReviewedCatalogEvidence>
        FindReviewedOverrideChanges(
            string repositoryRoot,
            string baselineReference,
            string reviewedReference)
    {
        if (string.IsNullOrWhiteSpace(baselineReference) ||
            string.IsNullOrWhiteSpace(reviewedReference))
            throw new ArgumentException(
                "Both --review-baseline-ref and --reviewed-ref are required.");

        IReadOnlyDictionary<
            string,
            IReadOnlyDictionary<string, string>> baseline =
                LoadOverridesAtGitReference(
                    repositoryRoot,
                    baselineReference);
        IReadOnlyDictionary<
            string,
            IReadOnlyDictionary<string, string>> reviewed =
                LoadOverridesAtGitReference(
                    repositoryRoot,
                    reviewedReference);
        IReadOnlyDictionary<string, string> reviewedNeutral =
            LoadNeutralAtGitReference(
                repositoryRoot,
                reviewedReference);
        return reviewed
            .Where(item =>
                !baseline.TryGetValue(
                    item.Key,
                    out IReadOnlyDictionary<string, string>?
                        baselineTranslations) ||
                !HaveSameTranslations(
                    baselineTranslations,
                    item.Value))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                item => item.Key,
                    item => new ReviewedCatalogEvidence(
                    reviewedNeutral.TryGetValue(
                        item.Key,
                        out string? neutral)
                        ? neutral
                        : throw new InvalidDataException(
                            $"Reviewed override '{item.Key}' does not " +
                            $"exist in the neutral catalog at " +
                            $"'{reviewedReference}'."),
                    item.Value,
                    $"git-diff:v1:{baselineReference}:{reviewedReference}"),
                StringComparer.Ordinal);
    }

    public static string SerializeReviewEvidence(
        IReadOnlyList<CatalogReviewSource> sources,
        IReadOnlyDictionary<string, ReviewedCatalogEvidence>
            reviewedCatalogs,
        string baselineCommit,
        string reviewedCommit,
        string batch,
        string reviewer,
        string date)
    {
        ValidateReviewMetadata(batch, reviewer, date);
        var sourceByKey = sources.ToDictionary(
            source => source.Key,
            StringComparer.Ordinal);
        var entries = new List<(string Key, string Digest)>();
        foreach ((string key, ReviewedCatalogEvidence evidence) in
                 reviewedCatalogs.OrderBy(
                     item => item.Key,
                     StringComparer.Ordinal))
        {
            if (!sourceByKey.TryGetValue(
                    key,
                    out CatalogReviewSource? source) ||
                source.Route !=
                    CatalogTranslationRoute.EditorialOverride ||
                !string.Equals(
                    source.Neutral,
                    evidence.Neutral,
                    StringComparison.Ordinal) ||
                !HaveSameTranslations(
                    source.Translations,
                    evidence.Translations))
                throw new InvalidDataException(
                    $"Reviewed evidence for '{key}' does not match the " +
                    "current editorial-override catalog values.");
            entries.Add((key, ComputeDigest(source)));
        }
        string identity = ComputeReviewEvidenceIdentity(
            baselineCommit,
            reviewedCommit,
            batch,
            reviewer,
            date,
            entries);
        var root = new XElement(
            "focused-editorial-review-evidence",
            new XAttribute("version", "1"),
            new XAttribute("baselineCommit", baselineCommit),
            new XAttribute("reviewedCommit", reviewedCommit),
            new XAttribute("resourceCount", entries.Count),
            new XAttribute("batch", batch),
            new XAttribute("reviewer", reviewer),
            new XAttribute("date", date),
            new XAttribute("identity", identity));
        foreach ((string key, string digest) in entries)
            root.Add(
                new XElement(
                    "entry",
                    new XAttribute("key", key),
                    new XAttribute(
                        "route",
                        CatalogTranslationRoute
                            .EditorialOverride),
                    new XAttribute("digest", digest)));
        return SerializeXml(
            new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                root));
    }

    public static EditorialReviewSeed LoadReviewEvidence(
        string path,
        IReadOnlyList<CatalogReviewSource> sources)
    {
        XDocument document = XDocument.Load(
            path,
            LoadOptions.PreserveWhitespace);
        if (document.Root?.Name.LocalName !=
                "focused-editorial-review-evidence" ||
            !string.Equals(
                (string?)document.Root.Attribute("version"),
                "1",
                StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Review evidence '{path}' must use a version 1 " +
                "focused-editorial-review-evidence root.");
        string baselineCommit =
            RequiredAttribute(document.Root, "baselineCommit");
        string reviewedCommit =
            RequiredAttribute(document.Root, "reviewedCommit");
        string batch =
            RequiredAttribute(document.Root, "batch");
        string reviewer =
            RequiredAttribute(document.Root, "reviewer");
        string date =
            RequiredAttribute(document.Root, "date");
        string identity =
            RequiredAttribute(document.Root, "identity");
        ValidateReviewMetadata(batch, reviewer, date);
        if (!IsSha256(identity))
            throw new InvalidDataException(
                "Focused review evidence identity is invalid.");

        var sourceByKey = sources.ToDictionary(
            source => source.Key,
            StringComparer.Ordinal);
        var entries = new List<(string Key, string Digest)>();
        var catalogs = new Dictionary<
            string,
            ReviewedCatalogEvidence>(
            StringComparer.Ordinal);
        foreach (XElement entry in
                 document.Root.Elements("entry"))
        {
            string key = RequiredAttribute(entry, "key");
            string digest = RequiredAttribute(entry, "digest");
            if (!string.Equals(
                    RequiredAttribute(entry, "route"),
                    CatalogTranslationRoute
                        .EditorialOverride.ToString(),
                    StringComparison.Ordinal) ||
                !IsSha256(digest))
                throw new InvalidDataException(
                    $"Focused review evidence entry '{key}' is invalid.");
            if (!sourceByKey.TryGetValue(
                    key,
                    out CatalogReviewSource? source) ||
                source.Route !=
                    CatalogTranslationRoute.EditorialOverride)
                throw new InvalidDataException(
                    $"Focused review evidence references unavailable " +
                    $"editorial override '{key}'.");
            if (!string.Equals(
                    digest,
                    ComputeDigest(source),
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Focused review evidence digest is stale for '{key}'.");
            if (!catalogs.TryAdd(
                    key,
                    new ReviewedCatalogEvidence(
                        source.Neutral,
                        source.Translations,
                        $"review-set:v1:{identity}")))
                throw new InvalidDataException(
                    $"Focused review evidence key '{key}' is duplicated.");
            entries.Add((key, digest));
        }
        string[] keyOrder =
        [
            .. entries.Select(entry => entry.Key),
        ];
        if (!keyOrder.SequenceEqual(
                keyOrder.OrderBy(
                    key => key,
                    StringComparer.Ordinal),
                StringComparer.Ordinal))
            throw new InvalidDataException(
                "Focused review evidence entries are not in canonical order.");
        RequireCount(
            document.Root,
            "resourceCount",
            entries.Count,
            "Focused review evidence");
        string expectedIdentity = ComputeReviewEvidenceIdentity(
            baselineCommit,
            reviewedCommit,
            batch,
            reviewer,
            date,
            entries);
        if (!string.Equals(
                identity,
                expectedIdentity,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "Focused review evidence identity does not match its " +
                "metadata and ordered key/digest set.");
        return new EditorialReviewSeed(
            catalogs,
            batch,
            reviewer,
            date,
            identity);
    }

    public static IReadOnlyDictionary<string, string>
        LoadInvariantAllowlist(
        string path)
    {
        if (!File.Exists(path))
            throw new InvalidDataException(
                $"Invariant approval allowlist '{path}' is missing.");
        var values = new Dictionary<string, string>(
            StringComparer.Ordinal);
        int lineNumber = 0;
        foreach (string rawLine in File.ReadLines(path))
        {
            lineNumber++;
            string line = rawLine.Trim();
            if (line.Length == 0 ||
                line.StartsWith('#'))
                continue;
            string[] columns = line.Split('\t');
            if (columns.Length != 2 ||
                string.IsNullOrWhiteSpace(columns[0]) ||
                !IsSha256(columns[1]))
                throw new InvalidDataException(
                    $"Invariant approval allowlist '{path}' has an invalid " +
                    $"key/digest row at line {lineNumber:N0}.");
            if (!values.TryAdd(
                    columns[0],
                    columns[1]))
                throw new InvalidDataException(
                    $"Invariant approval allowlist '{path}' duplicates " +
                    $"'{columns[0]}' at line {lineNumber:N0}.");
        }
        return values;
    }

    public static void WriteAudit(
        string path,
        EditorialReviewManifest manifest)
        => WriteUtf8Text(
            path,
            SerializeAudit(manifest));

    public static string SerializeAudit(
        EditorialReviewManifest manifest)
    {
        var builder = new StringBuilder();
        builder.Append(
            "Key\tStatus\tRoute\tDigest\tBatch\tReviewer\tDate\tDisposition\n");
        foreach (EditorialReviewRecord record in
                 manifest.Records.Values
                     .OrderBy(
                         record => record.Key,
                         StringComparer.Ordinal))
        {
            builder.Append(EscapeTsv(record.Key));
            builder.Append('\t');
            builder.Append(record.Status);
            builder.Append('\t');
            builder.Append(record.Route);
            builder.Append('\t');
            builder.Append(record.Digest);
            builder.Append('\t');
            builder.Append(EscapeTsv(record.Batch));
            builder.Append('\t');
            builder.Append(EscapeTsv(record.Reviewer));
            builder.Append('\t');
            builder.Append(record.Date);
            builder.Append('\t');
            builder.Append(EscapeTsv(record.Disposition));
            builder.Append('\n');
        }
        return builder.ToString();
    }

    public static void WriteReviewPacket(
        string path,
        string? domain,
        IReadOnlyList<CatalogReviewSource> sources,
        EditorialReviewManifest manifest)
        => WriteUtf8Text(
            path,
            SerializeReviewPacket(
                domain,
                sources,
                manifest));

    public static string SerializeReviewPacket(
        string? domain,
        IReadOnlyList<CatalogReviewSource> sources,
        EditorialReviewManifest manifest)
    {
        string normalizedDomain = domain?.Trim() ?? "";
        CatalogReviewSource[] selected =
        [
            .. sources
                .Where(source =>
                    manifest.Records[source.Key].Status ==
                        EditorialReviewStatus.Pending &&
                    (normalizedDomain.Length == 0 ||
                     string.Equals(
                         source.Key,
                         normalizedDomain,
                         StringComparison.Ordinal) ||
                     source.Key.StartsWith(
                         normalizedDomain + ".",
                         StringComparison.Ordinal)))
                .OrderBy(source => source.Key, StringComparer.Ordinal),
        ];
        if (selected.Length == 0)
            throw new InvalidDataException(
                normalizedDomain.Length == 0
                    ? "The review packet has no resources."
                    : $"Review domain '{normalizedDomain}' has no resources.");
        string packetIdentity = ComputePacketIdentity(
            normalizedDomain.Length == 0
                ? "*"
                : normalizedDomain,
            selected.Select(source => (
                source.Key,
                manifest.Records[source.Key].Digest,
                source.Route)));

        var root = new XElement(
            "editorial-review-packet",
            new XAttribute("version", "1"),
            new XAttribute(
                "domain",
                normalizedDomain.Length == 0
                    ? "*"
                    : normalizedDomain),
            new XAttribute("resourceCount", selected.Length),
            new XAttribute("identity", packetIdentity));
        foreach (CatalogReviewSource source in selected)
        {
            EditorialReviewRecord record =
                manifest.Records[source.Key];
            var entry = new XElement(
                "entry",
                new XAttribute("key", source.Key),
                new XAttribute("status", record.Status),
                new XAttribute("route", source.Route),
                new XAttribute("digest", record.Digest),
                new XAttribute(
                    "packetIdentity",
                    packetIdentity),
                new XAttribute(
                    "reviewDisposition",
                    ComputePacketEntryDisposition(
                        packetIdentity,
                        source.Key,
                        record.Digest,
                        source.Route)),
                new XAttribute("batch", record.Batch),
                new XAttribute("reviewer", record.Reviewer),
                new XAttribute("date", record.Date),
                new XElement("neutral", source.Neutral));
            var translations = new XElement("translations");
            foreach (string culture in ShippingCultures)
                translations.Add(
                    new XElement(
                        "translation",
                        new XAttribute("culture", culture),
                        source.Translations[culture]));
            entry.Add(translations);

            var placeholders = new XElement("placeholders");
            foreach (string placeholder in FindPlaceholders(source.Neutral))
                placeholders.Add(
                    new XElement("token", placeholder));
            entry.Add(placeholders);

            var protectedTokens = new XElement("protected-tokens");
            foreach (string token in FindProtectedTokens(source.Neutral))
                protectedTokens.Add(
                    new XElement("token", token));
            entry.Add(protectedTokens);
            root.Add(entry);
        }
        return SerializeXml(
            new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                root));
    }

    public static EditorialReviewManifest ApproveReviewPacket(
        string packetPath,
        IReadOnlyList<CatalogReviewSource> sources,
        EditorialReviewManifest manifest,
        string batch,
        string reviewer,
        string date)
    {
        ValidateReviewMetadata(batch, reviewer, date);
        XDocument document = XDocument.Load(
            packetPath,
            LoadOptions.PreserveWhitespace);
        if (document.Root?.Name.LocalName !=
                "editorial-review-packet" ||
            !string.Equals(
                (string?)document.Root.Attribute("version"),
                "1",
                StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Review packet '{packetPath}' must use a version 1 " +
                "editorial-review-packet root.");

        var sourceByKey = sources.ToDictionary(
            source => source.Key,
            StringComparer.Ordinal);
        string packetDomain =
            RequiredAttribute(document.Root, "domain");
        string packetIdentity =
            RequiredAttribute(document.Root, "identity");
        if (!IsSha256(packetIdentity))
            throw new InvalidDataException(
                "Review packet identity is not a lowercase SHA-256 digest.");
        XElement[] packetEntries =
        [
            .. document.Root.Elements("entry"),
        ];
        string[] packetKeyOrder =
        [
            .. packetEntries.Select(entry =>
                RequiredAttribute(entry, "key")),
        ];
        if (!packetKeyOrder.SequenceEqual(
                packetKeyOrder.OrderBy(
                    key => key,
                    StringComparer.Ordinal),
                StringComparer.Ordinal))
            throw new InvalidDataException(
                "Review packet entries are not in canonical key order.");
        var approvedKeys = new HashSet<string>(
            StringComparer.Ordinal);
        var reviewedDispositions = new Dictionary<string, string>(
            StringComparer.Ordinal);
        foreach (XElement entry in packetEntries)
        {
            string key = RequiredAttribute(entry, "key");
            if (!approvedKeys.Add(key))
                throw new InvalidDataException(
                    $"Review packet key '{key}' is duplicated.");
            if (!sourceByKey.TryGetValue(
                    key,
                    out CatalogReviewSource? source))
                throw new InvalidDataException(
                    $"Review packet references unknown resource '{key}'.");
            if (!string.Equals(
                    packetDomain,
                    "*",
                    StringComparison.Ordinal) &&
                !string.Equals(
                    key,
                    packetDomain,
                    StringComparison.Ordinal) &&
                !key.StartsWith(
                    packetDomain + ".",
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Review packet resource '{key}' is outside declared " +
                    $"domain '{packetDomain}'.");
            EditorialReviewRecord current = manifest.Records[key];
            if (current.Status != EditorialReviewStatus.Pending)
                throw new InvalidDataException(
                    $"Review packet resource '{key}' is no longer Pending.");
            if (!string.Equals(
                    RequiredAttribute(entry, "status"),
                    EditorialReviewStatus.Pending.ToString(),
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Review packet resource '{key}' was not exported as Pending.");
            if (!string.Equals(
                    RequiredAttribute(entry, "route"),
                    source.Route.ToString(),
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Review packet route is stale for '{key}'.");
            string digest = RequiredAttribute(entry, "digest");
            string expectedDigest = ComputeDigest(source);
            if (!string.Equals(
                    digest,
                    expectedDigest,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    current.Digest,
                    expectedDigest,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Review packet digest is stale for '{key}'.");
            if (!string.Equals(
                    RequiredAttribute(entry, "packetIdentity"),
                    packetIdentity,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Review packet identity is inconsistent for '{key}'.");
            string reviewedDisposition =
                RequiredAttribute(entry, "reviewDisposition");
            if (!string.Equals(
                    reviewedDisposition,
                    ComputePacketEntryDisposition(
                        packetIdentity,
                        key,
                        digest,
                        source.Route),
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Review disposition is invalid for '{key}'.");
            reviewedDispositions.Add(
                key,
                reviewedDisposition);
            string packetNeutral =
                entry.Element("neutral")?.Value ??
                throw new InvalidDataException(
                    $"Review packet resource '{key}' has no neutral value.");
            if (!string.Equals(
                    packetNeutral,
                    source.Neutral,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Review packet neutral value is stale for '{key}'.");

            XElement translationsElement =
                entry.Element("translations") ??
                throw new InvalidDataException(
                    $"Review packet resource '{key}' has no translations.");
            var packetTranslations = new Dictionary<string, string>(
                StringComparer.Ordinal);
            foreach (XElement translation in
                     translationsElement.Elements("translation"))
            {
                string culture =
                    RequiredAttribute(translation, "culture");
                if (!ShippingCultures.Contains(
                        culture,
                        StringComparer.Ordinal))
                    throw new InvalidDataException(
                        $"Review packet resource '{key}' uses unsupported " +
                        $"culture '{culture}'.");
                if (!packetTranslations.TryAdd(
                        culture,
                        translation.Value))
                    throw new InvalidDataException(
                        $"Review packet resource '{key}' repeats culture " +
                        $"'{culture}'.");
            }
            if (!HaveSameTranslations(
                    source.Translations,
                    packetTranslations))
                throw new InvalidDataException(
                    $"Review packet translations are stale for '{key}'.");
        }
        if (approvedKeys.Count == 0)
            throw new InvalidDataException(
                "Review packet contains no Pending resources.");
        string rawCount =
            RequiredAttribute(document.Root, "resourceCount");
        if (!int.TryParse(
                rawCount,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int declaredCount) ||
            declaredCount != approvedKeys.Count)
            throw new InvalidDataException(
                $"Review packet resourceCount is '{rawCount}', expected " +
                $"'{approvedKeys.Count}'.");
        string expectedPacketIdentity = ComputePacketIdentity(
            packetDomain,
            packetKeyOrder.Select(key => (
                key,
                manifest.Records[key].Digest,
                sourceByKey[key].Route)));
        if (!string.Equals(
                packetIdentity,
                expectedPacketIdentity,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "Review packet identity does not match its domain and " +
                "ordered key/digest set.");

        var updated = manifest.Records.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal);
        foreach (string key in approvedKeys)
        {
            EditorialReviewRecord current = updated[key];
            updated[key] = current with
            {
                Status =
                    current.Route ==
                    CatalogTranslationRoute.EditorialOverride
                        ? EditorialReviewStatus.EditorialReviewed
                        : EditorialReviewStatus.GlossaryReviewed,
                Batch = batch,
                Reviewer = reviewer,
                Date = date,
                Disposition =
                    $"packet:v1:{packetIdentity}:" +
                    reviewedDispositions[key],
            };
        }
        var result = new EditorialReviewManifest(updated);
        return result;
    }

    public static string ComputeDigest(
        CatalogReviewSource source)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(
                   stream,
                   new UTF8Encoding(false),
                   leaveOpen: true))
        {
            writer.Write("MusicLibraryManager.EditorialReviewDigest.v1");
            WriteDigestField(writer, source.Key);
            WriteDigestField(writer, "en-US");
            WriteDigestField(writer, source.Neutral);
            foreach (string culture in ShippingCultures)
            {
                WriteDigestField(writer, culture);
                WriteDigestField(
                    writer,
                    source.Translations[culture]);
            }
        }
        return Convert.ToHexString(
                SHA256.HashData(stream.ToArray()))
            .ToLowerInvariant();
    }

    private static string ComputePacketIdentity(
        string domain,
        IEnumerable<(
            string Key,
            string Digest,
            CatalogTranslationRoute Route)> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(
                   stream,
                   new UTF8Encoding(false),
                   leaveOpen: true))
        {
            writer.Write(
                "MusicLibraryManager.EditorialReviewPacket.v1");
            WriteDigestField(writer, domain);
            foreach ((string key, string digest,
                      CatalogTranslationRoute route) in entries)
            {
                WriteDigestField(writer, key);
                WriteDigestField(writer, digest);
                WriteDigestField(writer, route.ToString());
            }
        }
        return Convert.ToHexString(
                SHA256.HashData(stream.ToArray()))
            .ToLowerInvariant();
    }

    private static string ComputeReviewEvidenceIdentity(
        string baselineCommit,
        string reviewedCommit,
        string batch,
        string reviewer,
        string date,
        IEnumerable<(string Key, string Digest)> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(
                   stream,
                   new UTF8Encoding(false),
                   leaveOpen: true))
        {
            writer.Write(
                "MusicLibraryManager.FocusedEditorialReviewEvidence.v1");
            WriteDigestField(writer, baselineCommit);
            WriteDigestField(writer, reviewedCommit);
            WriteDigestField(writer, batch);
            WriteDigestField(writer, reviewer);
            WriteDigestField(writer, date);
            foreach ((string key, string digest) in entries)
            {
                WriteDigestField(writer, key);
                WriteDigestField(writer, digest);
            }
        }
        return Convert.ToHexString(
                SHA256.HashData(stream.ToArray()))
            .ToLowerInvariant();
    }

    private static string ComputePacketEntryDisposition(
        string packetIdentity,
        string key,
        string digest,
        CatalogTranslationRoute route)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(
                   stream,
                   new UTF8Encoding(false),
                   leaveOpen: true))
        {
            writer.Write(
                "MusicLibraryManager.EditorialReviewDisposition.v1");
            WriteDigestField(writer, packetIdentity);
            WriteDigestField(writer, key);
            WriteDigestField(writer, digest);
            WriteDigestField(writer, route.ToString());
            WriteDigestField(writer, "Approved");
        }
        return Convert.ToHexString(
                SHA256.HashData(stream.ToArray()))
            .ToLowerInvariant();
    }

    public static bool IsCatalogInvariant(
        CatalogReviewSource source) =>
        ShippingCultures.All(culture =>
            string.Equals(
                source.Neutral,
                source.Translations[culture],
                StringComparison.Ordinal));

    public static string ComputeInvariantValueDigest(
        CatalogReviewSource source)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(
                   stream,
                   new UTF8Encoding(false),
                   leaveOpen: true))
        {
            writer.Write(
                "MusicLibraryManager.InvariantApprovedValue.v1");
            WriteDigestField(writer, source.Key);
            WriteDigestField(writer, source.Neutral);
        }
        return Convert.ToHexString(
                SHA256.HashData(stream.ToArray()))
            .ToLowerInvariant();
    }

    private static void ValidateInvariantAllowlist(
        IReadOnlyDictionary<string, string> invariantApprovedValues,
        IReadOnlyList<CatalogReviewSource> sources)
    {
        var sourceByKey = sources.ToDictionary(
            source => source.Key,
            StringComparer.Ordinal);
        string[] unknown =
        [
            .. invariantApprovedValues.Keys
                .Where(key => !sourceByKey.ContainsKey(key))
                .OrderBy(key => key, StringComparer.Ordinal),
        ];
        if (unknown.Length > 0)
            throw new InvalidDataException(
                "Invariant approval allowlist references unknown resources: " +
                string.Join(", ", unknown));
        string[] changed =
        [
            .. invariantApprovedValues.Keys
                .Where(key =>
                    !IsCatalogInvariant(sourceByKey[key]) ||
                    !string.Equals(
                        invariantApprovedValues[key],
                        ComputeInvariantValueDigest(
                            sourceByKey[key]),
                        StringComparison.Ordinal))
                .OrderBy(key => key, StringComparer.Ordinal),
        ];
        if (changed.Length > 0)
            throw new InvalidDataException(
                "Invariant approval allowlist contains resources whose " +
                "catalog values or approved value digests changed: " +
                string.Join(", ", changed));
    }

    private static void ValidateCanonicalRecords(
        IReadOnlyDictionary<string, EditorialReviewRecord> records,
        IReadOnlyDictionary<string, CatalogReviewSource> sources,
        IReadOnlyDictionary<string, string> invariantApprovedValues)
    {
        foreach (EditorialReviewRecord record in records.Values)
        {
            bool canonical = record.Status switch
            {
                EditorialReviewStatus.Pending =>
                    string.Equals(
                        record.Batch,
                        "editorial-backlog-v1",
                        StringComparison.Ordinal) &&
                    string.Equals(
                        record.Reviewer,
                        "Unassigned",
                        StringComparison.Ordinal) &&
                    string.Equals(
                        record.Disposition,
                        "pending:v1",
                        StringComparison.Ordinal),
                EditorialReviewStatus.InvariantApproved =>
                    string.Equals(
                        record.Batch,
                        "catalog-invariant-v1",
                        StringComparison.Ordinal) &&
                    string.Equals(
                        record.Reviewer,
                        "LocalizationCatalogGenerator",
                        StringComparison.Ordinal) &&
                    invariantApprovedValues.TryGetValue(
                        record.Key,
                        out string? valueDigest) &&
                    string.Equals(
                        record.Disposition,
                        $"invariant:v1:{valueDigest}",
                        StringComparison.Ordinal) &&
                    IsCatalogInvariant(sources[record.Key]) &&
                    string.Equals(
                        valueDigest,
                        ComputeInvariantValueDigest(
                            sources[record.Key]),
                        StringComparison.Ordinal),
                EditorialReviewStatus.EditorialReviewed =>
                    record.Route ==
                        CatalogTranslationRoute.EditorialOverride &&
                    IsReviewedProvenance(record),
                EditorialReviewStatus.GlossaryReviewed =>
                    record.Route !=
                        CatalogTranslationRoute.EditorialOverride &&
                    IsReviewedProvenance(record),
                _ => false,
            };
            if (!canonical)
                throw new InvalidDataException(
                    $"Editorial review manifest entry '{record.Key}' has " +
                    $"a noncanonical {record.Status}/{record.Route} " +
                    "provenance combination.");
        }
    }

    private static bool IsReviewedProvenance(
        EditorialReviewRecord record) =>
        !string.Equals(
            record.Batch,
            "editorial-backlog-v1",
            StringComparison.Ordinal) &&
        !string.Equals(
            record.Batch,
            "catalog-invariant-v1",
            StringComparison.Ordinal) &&
        !string.Equals(
            record.Reviewer,
            "Unassigned",
            StringComparison.Ordinal) &&
        !string.Equals(
            record.Reviewer,
            "LocalizationCatalogGenerator",
            StringComparison.Ordinal) &&
        IsCanonicalReviewedDisposition(record.Disposition);

    private static bool IsCanonicalReviewedDisposition(
        string disposition)
    {
        if (disposition.StartsWith(
                "review-set:v1:",
                StringComparison.Ordinal))
            return IsSha256(
                disposition["review-set:v1:".Length..]);

        if (disposition.StartsWith(
                "packet:v1:",
                StringComparison.Ordinal))
        {
            string[] fields = disposition.Split(':');
            return fields.Length == 4 &&
                   IsSha256(fields[2]) &&
                   IsSha256(fields[3]);
        }

        if (disposition.StartsWith(
                "git-diff:v1:",
                StringComparison.Ordinal))
        {
            string[] fields = disposition.Split(':');
            return fields.Length == 4 &&
                   !string.IsNullOrWhiteSpace(fields[2]) &&
                   !string.IsNullOrWhiteSpace(fields[3]);
        }

        return false;
    }

    private static IReadOnlyDictionary<string, EditorialReviewRecord>
        ParseManifest(string path)
    {
        XDocument document = XDocument.Load(
            path,
            LoadOptions.PreserveWhitespace);
        if (document.Root?.Name.LocalName !=
            "editorial-review-manifest")
            throw new InvalidDataException(
                $"Editorial review manifest '{path}' must use an " +
                "editorial-review-manifest root.");
        if (!string.Equals(
                (string?)document.Root.Attribute("version"),
                "1",
                StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Editorial review manifest '{path}' uses an unsupported version.");

        var records = new Dictionary<string, EditorialReviewRecord>(
            StringComparer.Ordinal);
        foreach (XElement entry in document.Root.Elements("entry"))
        {
            string key = RequiredAttribute(entry, "key");
            if (!Enum.TryParse(
                    RequiredAttribute(entry, "status"),
                    ignoreCase: false,
                    out EditorialReviewStatus status))
                throw new InvalidDataException(
                    $"Editorial review manifest entry '{key}' has an " +
                    "unsupported status.");
            if (!Enum.TryParse(
                    RequiredAttribute(entry, "route"),
                    ignoreCase: false,
                    out CatalogTranslationRoute route))
                throw new InvalidDataException(
                    $"Editorial review manifest entry '{key}' has an " +
                    "unsupported route.");
            string digest = RequiredAttribute(entry, "digest");
            if (!IsSha256(digest))
                throw new InvalidDataException(
                    $"Editorial review manifest entry '{key}' has an " +
                    "invalid SHA-256 digest.");
            string batch = RequiredAttribute(entry, "batch");
            string reviewer = RequiredAttribute(entry, "reviewer");
            string date = RequiredAttribute(entry, "date");
            string disposition =
                RequiredAttribute(entry, "disposition");
            ValidateReviewMetadata(batch, reviewer, date);
            if (!records.TryAdd(
                    key,
                    new EditorialReviewRecord(
                        key,
                        status,
                        route,
                        digest,
                        batch,
                        reviewer,
                        date,
                        disposition)))
                throw new InvalidDataException(
                    $"Editorial review manifest key '{key}' is duplicated.");
        }
        return records;
    }

    public static string SerializeManifest(
        EditorialReviewManifest manifest)
    {
        IReadOnlyDictionary<EditorialReviewStatus, int> statusCounts =
            Enum.GetValues<EditorialReviewStatus>()
                .ToDictionary(
                    status => status,
                    status => manifest.Records.Values.Count(
                        record => record.Status == status));
        var root = new XElement(
            "editorial-review-manifest",
            new XAttribute("version", "1"),
            new XAttribute(
                "resourceCount",
                manifest.Records.Count),
            new XAttribute(
                "catalogDigest",
                ComputeCatalogDigest(manifest.Records.Values)),
            new XAttribute(
                "manifestDigest",
                ComputeManifestDigest(manifest.Records.Values)));
        foreach (EditorialReviewStatus status in
                 Enum.GetValues<EditorialReviewStatus>())
            root.Add(
                new XAttribute(
                    char.ToLowerInvariant(status.ToString()[0]) +
                    status.ToString()[1..],
                    statusCounts[status]));

        foreach (EditorialReviewRecord record in
                 manifest.Records.Values.OrderBy(
                     record => record.Key,
                     StringComparer.Ordinal))
        {
            root.Add(
                new XElement(
                    "entry",
                    new XAttribute("key", record.Key),
                    new XAttribute("status", record.Status),
                    new XAttribute("route", record.Route),
                    new XAttribute("digest", record.Digest),
                    new XAttribute("batch", record.Batch),
                    new XAttribute("reviewer", record.Reviewer),
                    new XAttribute("date", record.Date),
                    new XAttribute(
                        "disposition",
                        record.Disposition)));
        }
        return SerializeXml(
            new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                root));
    }

    private static void ValidateRootSummary(
        string path,
        IReadOnlyDictionary<string, EditorialReviewRecord> records)
    {
        XDocument document = XDocument.Load(path);
        XElement root = document.Root!;
        RequireSummary(
            root,
            "resourceCount",
            records.Count);
        foreach (EditorialReviewStatus status in
                 Enum.GetValues<EditorialReviewStatus>())
        {
            RequireSummary(
                root,
                char.ToLowerInvariant(status.ToString()[0]) +
                status.ToString()[1..],
                records.Values.Count(record =>
                    record.Status == status));
        }
        string expectedCatalogDigest =
            ComputeCatalogDigest(records.Values);
        string actualCatalogDigest =
            RequiredAttribute(root, "catalogDigest");
        if (!string.Equals(
                actualCatalogDigest,
                expectedCatalogDigest,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "Editorial review manifest catalogDigest does not match " +
                "its entries.");
        string expectedManifestDigest =
            ComputeManifestDigest(records.Values);
        string actualManifestDigest =
            RequiredAttribute(root, "manifestDigest");
        if (!string.Equals(
                actualManifestDigest,
                expectedManifestDigest,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "Editorial review manifest manifestDigest does not match " +
                "its statuses or provenance.");
    }

    private static void RequireSummary(
        XElement root,
        string name,
        int expected) =>
        RequireCount(
            root,
            name,
            expected,
            "Editorial review manifest");

    private static void RequireCount(
        XElement root,
        string name,
        int expected,
        string context)
    {
        string raw = RequiredAttribute(root, name);
        if (!int.TryParse(
                raw,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int actual) ||
            actual != expected)
            throw new InvalidDataException(
                $"{context} summary '{name}' is " +
                $"'{raw}', expected '{expected}'.");
    }

    private static string ComputeCatalogDigest(
        IEnumerable<EditorialReviewRecord> records)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(
                   stream,
                   new UTF8Encoding(false),
                   leaveOpen: true))
        {
            writer.Write(
                "MusicLibraryManager.EditorialReviewCatalogDigest.v1");
            foreach (EditorialReviewRecord record in
                     records.OrderBy(
                         record => record.Key,
                         StringComparer.Ordinal))
            {
                WriteDigestField(writer, record.Key);
                WriteDigestField(writer, record.Digest);
            }
        }
        return Convert.ToHexString(
                SHA256.HashData(stream.ToArray()))
            .ToLowerInvariant();
    }

    private static string ComputeManifestDigest(
        IEnumerable<EditorialReviewRecord> records)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(
                   stream,
                   new UTF8Encoding(false),
                   leaveOpen: true))
        {
            writer.Write(
                "MusicLibraryManager.EditorialReviewManifestDigest.v1");
            foreach (EditorialReviewRecord record in
                     records.OrderBy(
                         record => record.Key,
                         StringComparer.Ordinal))
            {
                WriteDigestField(writer, record.Key);
                WriteDigestField(writer, record.Digest);
                WriteDigestField(writer, record.Status.ToString());
                WriteDigestField(writer, record.Route.ToString());
                WriteDigestField(writer, record.Batch);
                WriteDigestField(writer, record.Reviewer);
                WriteDigestField(writer, record.Date);
                WriteDigestField(writer, record.Disposition);
            }
        }
        return Convert.ToHexString(
                SHA256.HashData(stream.ToArray()))
            .ToLowerInvariant();
    }

    private static void WriteDigestField(
        BinaryWriter writer,
        string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static bool HaveSameTranslations(
        IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second) =>
        ShippingCultures.All(culture =>
            first.TryGetValue(culture, out string? firstValue) &&
            second.TryGetValue(culture, out string? secondValue) &&
            string.Equals(
                firstValue,
                secondValue,
                StringComparison.Ordinal));

    private static IReadOnlyDictionary<
        string,
        IReadOnlyDictionary<string, string>>
        LoadOverridesAtGitReference(
            string repositoryRoot,
            string reference)
    {
        const string relativePath =
            "BuildTools/LocalizationCatalogGenerator/EditorialOverrides.xml";
        string output = ReadGitFile(
            repositoryRoot,
            reference,
            relativePath);
        using var reader = new StringReader(output);
        XDocument document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName !=
            "editorial-overrides")
            throw new InvalidDataException(
                $"Editorial overrides at '{reference}' use an invalid root.");
        var results = new Dictionary<
            string,
            IReadOnlyDictionary<string, string>>(
            StringComparer.Ordinal);
        foreach (XElement entry in document.Root.Elements("entry"))
        {
            string key = RequiredAttribute(entry, "key");
            var translations = new Dictionary<string, string>(
                StringComparer.Ordinal);
            foreach (XElement translation in
                     entry.Elements("translation"))
            {
                string culture =
                    RequiredAttribute(translation, "culture");
                if (!ShippingCultures.Contains(
                        culture,
                        StringComparer.Ordinal))
                    throw new InvalidDataException(
                        $"Editorial override '{key}' at '{reference}' " +
                        $"uses unsupported culture '{culture}'.");
                if (!translations.TryAdd(
                        culture,
                        translation.Value))
                    throw new InvalidDataException(
                        $"Editorial override '{key}' at '{reference}' " +
                        $"repeats culture '{culture}'.");
            }
            string[] missingCultures =
            [
                .. ShippingCultures.Where(culture =>
                    !translations.ContainsKey(culture)),
            ];
            if (missingCultures.Length > 0)
                throw new InvalidDataException(
                    $"Editorial override '{key}' at '{reference}' is " +
                    $"missing: {string.Join(", ", missingCultures)}.");
            if (!results.TryAdd(key, translations))
                throw new InvalidDataException(
                    $"Editorial override key '{key}' is duplicated at " +
                    $"'{reference}'.");
        }
        return results;
    }

    private static IReadOnlyDictionary<string, string>
        LoadNeutralAtGitReference(
            string repositoryRoot,
            string reference)
    {
        const string relativePath =
            "MusicLibraryManager.Presentation/Resources/Strings.resx";
        string output = ReadGitFile(
            repositoryRoot,
            reference,
            relativePath);
        using var reader = new StringReader(output);
        XDocument document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName != "root")
            throw new InvalidDataException(
                $"Neutral catalog at '{reference}' uses an invalid root.");
        var results = new Dictionary<string, string>(
            StringComparer.Ordinal);
        foreach (XElement entry in document.Root.Elements("data"))
        {
            string key = RequiredAttribute(entry, "name");
            string value = entry.Element("value")?.Value ??
                throw new InvalidDataException(
                    $"Neutral resource '{key}' at '{reference}' has no value.");
            if (!results.TryAdd(key, value))
                throw new InvalidDataException(
                    $"Neutral resource '{key}' is duplicated at " +
                    $"'{reference}'.");
        }
        return results;
    }

    private static string ReadGitFile(
        string repositoryRoot,
        string reference,
        string relativePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("show");
        startInfo.ArgumentList.Add($"{reference}:{relativePath}");
        using var process = new Process
        {
            StartInfo = startInfo,
        };
        if (!process.Start())
            throw new InvalidOperationException(
                "Could not start git to read review provenance.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"git show '{reference}:{relativePath}' timed out.");
        }
        if (process.ExitCode != 0)
            throw new InvalidDataException(
                $"Could not read '{relativePath}' at '{reference}': " +
                error.Trim());

        return output;
    }

    private static IReadOnlyList<string> FindPlaceholders(
        string value) =>
        PlaceholderPattern()
            .Matches(value)
            .Select(match => match.Value)
            .OrderBy(token => token, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> FindProtectedTokens(
        string value)
    {
        var tokens = LocalizationProtectedTerms.SourceDerivedTokens(value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string literal in
                 LocalizationProtectedTerms.LiteralTokens)
        {
            if (Regex.IsMatch(
                    value,
                    $@"(?<![A-Za-z0-9]){Regex.Escape(literal)}" +
                    @"(?![A-Za-z0-9])",
                    RegexOptions.CultureInvariant))
                tokens.Add(literal);
        }
        foreach (Match match in
                 LocalizationProtectedTerms.DynamicTokenPattern.Matches(
                     value))
        {
            if (!PlaceholderPattern().IsMatch(match.Value))
                tokens.Add(match.Value);
        }
        return tokens
            .OrderBy(token => token, StringComparer.Ordinal)
            .ToArray();
    }

    private static string RequiredAttribute(
        XElement element,
        string name)
    {
        string value =
            (string?)element.Attribute(name) ?? "";
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException(
                $"Element '{element.Name.LocalName}' is missing required " +
                $"attribute '{name}'.");
        return value;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 &&
        value.All(Uri.IsHexDigit) &&
        string.Equals(
            value,
            value.ToLowerInvariant(),
            StringComparison.Ordinal);

    private static void ValidateReviewMetadata(
        string batch,
        string reviewer,
        string date)
    {
        if (string.IsNullOrWhiteSpace(batch) ||
            string.IsNullOrWhiteSpace(reviewer))
            throw new InvalidDataException(
                "Editorial review batch and reviewer must be nonblank.");
        if (batch.IndexOfAny(['\t', '\r', '\n']) >= 0 ||
            reviewer.IndexOfAny(['\t', '\r', '\n']) >= 0)
            throw new InvalidDataException(
                "Editorial review metadata cannot contain tabs or newlines.");
        if (!DateOnly.TryParseExact(
                date,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
            throw new InvalidDataException(
                $"Editorial review date '{date}' must use yyyy-MM-dd.");
    }

    private static string EscapeTsv(string value) =>
        value.Replace('\t', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ');

    private static void WriteUtf8Text(
        string path,
        string contents)
        => AtomicOutputBatch.Commit(
            new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                [path] = contents,
            });

    private static string SerializeXml(
        XDocument document)
    {
        var builder = new StringBuilder();
        using var stringWriter = new InvariantStringWriter(
            builder);
        using var writer = XmlWriter.Create(
            stringWriter,
            new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                IndentChars = "  ",
                NewLineChars = "\n",
                NewLineHandling = NewLineHandling.Replace,
                OmitXmlDeclaration = false,
            });
        document.Save(writer);
        writer.Flush();
        return builder.ToString();
    }

    private sealed class InvariantStringWriter(
        StringBuilder builder) : StringWriter(
            builder,
            CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding { get; } =
            new UTF8Encoding(false);
    }

    [GeneratedRegex(
        @"(?<!\{)\{(?:\d+(?:,[^}:]+)?(?::[^}]*)?|[A-Za-z][A-Za-z0-9]*)\}(?!\})",
        RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}
