using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Known-failure baseline for the dataset unit tests.
///
/// The dataset fixtures under .ci/Datasets carry expected values serialised under .NET
/// Framework. DatasetTestRunner hosts the same comparison code on net10.0, so a set of
/// fixtures fails for environment reasons that no pull request caused. Today the runner's
/// only verdict is "any Error means exit 1", which makes that set indistinguishable from a
/// real regression: the check is permanently red, nobody reads it, and the set grew from 7
/// to 9 entries without anyone noticing.
///
/// This type turns the failure set into data. A baseline file records the failures that are
/// accepted, with a category and a reason each, and every run is classified against it:
///
///   Known    failing, present in the baseline, and its shape still matches.
///   New      failing and absent from the baseline. Fails the run.
///   Drifted  failing, present in the baseline, but the failure itself changed. Fails the
///            run: an entry that fails differently is not the entry that was accepted.
///   Stale    in the baseline, its fixture ran, and it did not fail. The baseline can shrink.
///
/// Deliberately free of BHoM types, matching FileAccounting and SarifBuilder, so it compiles
/// into the hermetic Compliance.Unit.Tests project and its keying and arithmetic can be
/// tested with no BHoM install. It classifies and renders; DatasetTestRunner walks the BHoM
/// TestResult tree and hands the records in.
/// </summary>
public static class DatasetBaseline
{
    public const string CategoryComparer = "comparer";
    public const string CategoryEngine   = "engine";
    public const string CategoryUnknown  = "unknown";

    /// <summary>Fingerprint must match for the entry to count as Known.</summary>
    public const string MatchExact  = "exact";
    /// <summary>Any failure under the key counts as Known, whatever its shape.</summary>
    public const string MatchMethod = "method";

    /// <summary>
    /// One failing unit test, as observed by a run. Keyed by (Fixture, MethodKey).
    ///
    /// Granularity is the unit test, not the TestData row, because the row's only identifier
    /// in the result tree is its ordinal ("TestData: index: 37"), which moves when a fixture
    /// is reordered or extended. Sub-method change is carried by Fingerprint instead, which
    /// is order-independent.
    /// </summary>
    public sealed record FailureRecord
    {
        /// <summary>Repo-relative, forward-slashed fixture path.</summary>
        public string Fixture { get; init; } = "";
        /// <summary>Normalised method key, e.g. "PlaneIntersections(Ellipse,Plane,Double)".</summary>
        public string MethodKey { get; init; } = "";
        /// <summary>The unit test's own Description verbatim, for humans.</summary>
        public string Description { get; init; } = "";
        /// <summary>"Error" or "Warning".</summary>
        public string Status { get; init; } = "";
        /// <summary>How many TestData rows in this unit test did not pass.</summary>
        public int FailingDataCount { get; init; }
        /// <summary>How many individual property differences were reported.</summary>
        public int DifferenceCount { get; init; }
        /// <summary>
        /// Order-independent digest of (property path, expected value) over every reported
        /// difference. Expected values come from the stored fixture, so this is stable across
        /// runtimes and machines; the value the run produced is deliberately excluded, so
        /// last-digit variation does not churn it. It changes when a difference appears or
        /// disappears, which is exactly the case that must not pass silently.
        /// </summary>
        public string Fingerprint { get; init; } = "";

        public string Key => $"{Fixture}::{MethodKey}";
    }

    public sealed record BaselineEntry
    {
        public string Fixture { get; init; } = "";
        public string MethodKey { get; init; } = "";
        public string Description { get; init; } = "";
        /// <summary>"comparer" (a-c: expected to pass once the comparer is fixed),
        /// "engine" (d: blocked on an engine change), or "unknown".</summary>
        public string Category { get; init; } = CategoryUnknown;
        /// <summary>Free text: why this is accepted.</summary>
        public string Reason { get; init; } = "";
        /// <summary>Tracking issue, e.g. "BHoM/BHoM_Engine#3514". Informational.</summary>
        public string Issue { get; init; } = "";
        /// <summary>"exact" or "method". See MatchExact / MatchMethod.</summary>
        public string Match { get; init; } = MatchExact;
        public int FailingDataCount { get; init; }
        public int DifferenceCount { get; init; }
        public string Fingerprint { get; init; } = "";

        public string Key => $"{Fixture}::{MethodKey}";
    }

    public sealed record BaselineFile
    {
        public int Version { get; init; } = 1;
        /// <summary>Caller-supplied stamp. Never generated here: this type must stay clock-free
        /// so its output is reproducible and diffable.</summary>
        public string Generated { get; init; } = "";
        public string Notes { get; init; } = "";
        public List<BaselineEntry> Entries { get; init; } = new();
    }

    public enum Verdict { Known, New, Drifted, Stale }

    public sealed record Classified
    {
        public Verdict Verdict { get; init; }
        public string Fixture { get; init; } = "";
        public string MethodKey { get; init; } = "";
        public string Description { get; init; } = "";
        public string Category { get; init; } = CategoryUnknown;
        public string Reason { get; init; } = "";
        public string Issue { get; init; } = "";
        /// <summary>Set on Drifted: what changed.</summary>
        public string DriftDetail { get; init; } = "";

        public string Key => $"{Fixture}::{MethodKey}";
    }

    public sealed record Report
    {
        public List<Classified> Known { get; init; } = new();
        public List<Classified> New { get; init; } = new();
        public List<Classified> Drifted { get; init; } = new();
        public List<Classified> Stale { get; init; } = new();

        /// <summary>Failures the baseline did not account for. These are what must be loud.</summary>
        public bool HasUnaccounted => New.Count > 0 || Drifted.Count > 0;
    }

    /* ---------------------------------------------------------------- classification */

    /// <summary>
    /// Classifies a run against a baseline.
    ///
    /// executedFixtures scopes staleness. A run that was handed only the fixtures a pull
    /// request touched must not report every other baselined entry as stale, so an entry is
    /// only eligible to be Stale when its fixture was actually executed. Pass null to treat
    /// every fixture as executed.
    /// </summary>
    public static Report Classify(
        IEnumerable<FailureRecord> records,
        BaselineFile? baseline,
        IEnumerable<string>? executedFixtures = null)
    {
        var recs = records?.ToList() ?? new List<FailureRecord>();
        var entries = baseline?.Entries ?? new List<BaselineEntry>();

        // Last entry wins on a duplicate key, so a hand-edited file with a repeated key
        // behaves predictably rather than throwing.
        var byKey = new Dictionary<string, BaselineEntry>(StringComparer.Ordinal);
        foreach (var e in entries) byKey[e.Key] = e;

        var executed = executedFixtures is null
            ? null
            : new HashSet<string>(executedFixtures, StringComparer.OrdinalIgnoreCase);

        var report = new Report();
        var matchedKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var r in recs.OrderBy(x => x.Fixture, StringComparer.Ordinal)
                              .ThenBy(x => x.MethodKey, StringComparer.Ordinal))
        {
            if (!byKey.TryGetValue(r.Key, out var entry))
            {
                report.New.Add(new Classified
                {
                    Verdict     = Verdict.New,
                    Fixture     = r.Fixture,
                    MethodKey   = r.MethodKey,
                    Description = r.Description,
                    Category    = CategoryUnknown,
                    Reason      = "Not present in the baseline.",
                });
                continue;
            }

            matchedKeys.Add(r.Key);

            var drift = Drift(entry, r);
            if (drift is null)
            {
                report.Known.Add(new Classified
                {
                    Verdict     = Verdict.Known,
                    Fixture     = r.Fixture,
                    MethodKey   = r.MethodKey,
                    Description = r.Description,
                    Category    = entry.Category,
                    Reason      = entry.Reason,
                    Issue       = entry.Issue,
                });
            }
            else
            {
                report.Drifted.Add(new Classified
                {
                    Verdict     = Verdict.Drifted,
                    Fixture     = r.Fixture,
                    MethodKey   = r.MethodKey,
                    Description = r.Description,
                    Category    = entry.Category,
                    Reason      = entry.Reason,
                    Issue       = entry.Issue,
                    DriftDetail = drift,
                });
            }
        }

        foreach (var e in byKey.Values.OrderBy(x => x.Fixture, StringComparer.Ordinal)
                                      .ThenBy(x => x.MethodKey, StringComparer.Ordinal))
        {
            if (matchedKeys.Contains(e.Key)) continue;
            if (executed is not null && !executed.Contains(e.Fixture)) continue;

            report.Stale.Add(new Classified
            {
                Verdict     = Verdict.Stale,
                Fixture     = e.Fixture,
                MethodKey   = e.MethodKey,
                Description = e.Description,
                Category    = e.Category,
                Reason      = e.Reason,
                Issue       = e.Issue,
            });
        }

        return report;
    }

    /// <summary>Null when the record still matches the entry, otherwise what changed.</summary>
    private static string? Drift(BaselineEntry entry, FailureRecord record)
    {
        // match: method accepts any failure under the key. Used for entries whose failure
        // shape is itself unstable — the near-tangency fixtures flip between returning one
        // point and two, so their difference set legitimately varies run to run and an exact
        // fingerprint would report drift on an unchanged defect.
        if (string.Equals(entry.Match, MatchMethod, StringComparison.OrdinalIgnoreCase))
            return null;

        // An entry written without a fingerprint (hand-added, or generated by an older
        // version) degrades to match: method rather than reporting drift against "".
        if (string.IsNullOrEmpty(entry.Fingerprint))
            return null;

        if (!string.Equals(entry.Fingerprint, record.Fingerprint, StringComparison.Ordinal))
            return $"expected differences changed (baseline {Short(entry.Fingerprint)} "
                 + $"{entry.DifferenceCount} difference(s) over {entry.FailingDataCount} row(s); "
                 + $"now {Short(record.Fingerprint)} {record.DifferenceCount} difference(s) over "
                 + $"{record.FailingDataCount} row(s))";

        return null;
    }

    private static string Short(string fp) => fp.Length <= 8 ? fp : fp.Substring(0, 8);

    /* ---------------------------------------------------------------- generation */

    /// <summary>
    /// Builds a baseline from an observed failure set, carrying category, reason, issue and
    /// match mode forward from a previous baseline where the key still exists. That is what
    /// makes the file regenerable rather than hand-maintained: the machine-derived fields are
    /// always rewritten, the human-authored ones are never lost, and an entry that stopped
    /// failing simply does not appear in the output.
    /// </summary>
    public static BaselineFile Generate(
        IEnumerable<FailureRecord> records,
        BaselineFile? previous,
        string generatedStamp = "",
        string notes = "")
    {
        var prev = new Dictionary<string, BaselineEntry>(StringComparer.Ordinal);
        foreach (var e in previous?.Entries ?? new List<BaselineEntry>()) prev[e.Key] = e;

        var entries = (records ?? Enumerable.Empty<FailureRecord>())
            .OrderBy(r => r.Fixture, StringComparer.Ordinal)
            .ThenBy(r => r.MethodKey, StringComparer.Ordinal)
            .Select(r =>
            {
                prev.TryGetValue(r.Key, out var old);
                return new BaselineEntry
                {
                    Fixture          = r.Fixture,
                    MethodKey        = r.MethodKey,
                    Description      = r.Description,
                    Category         = old?.Category ?? CategoryUnknown,
                    Reason           = old?.Reason   ?? "",
                    Issue            = old?.Issue    ?? "",
                    Match            = old?.Match    ?? MatchExact,
                    FailingDataCount = r.FailingDataCount,
                    DifferenceCount  = r.DifferenceCount,
                    Fingerprint      = r.Fingerprint,
                };
            })
            .ToList();

        return new BaselineFile
        {
            Version   = 1,
            Generated = generatedStamp,
            Notes     = notes,
            Entries   = entries,
        };
    }

    /* ---------------------------------------------------------------- keying */

    /// <summary>
    /// Turns a unit test's Description into a stable key.
    ///
    /// CheckTest builds the description as
    ///   "UnitTest: Method: {method.ToText()}" + (name is set ? " ,name: {name}." : ".")
    /// and ToText() includes parameter NAMES, e.g.
    ///   "PlaneIntersections(Ellipse curve, Plane plane, Double tolerance)".
    /// Parameter names are not part of the method's identity, so a rename that changes nothing
    /// semantically would otherwise invalidate the entry. This reduces the signature to its
    /// parameter types, splitting only at top-level commas so a generic argument list survives.
    ///
    /// Falls back to the trimmed description when the signature does not parse, so an
    /// unexpected shape produces a usable key rather than an exception.
    /// </summary>
    public static string NormaliseMethodKey(string? description)
    {
        var s = (description ?? "").Trim();
        const string prefix = "UnitTest: Method: ";
        if (s.StartsWith(prefix, StringComparison.Ordinal)) s = s.Substring(prefix.Length);

        // " ,name: foo." is appended when the UnitTest carries a Name. Keep it: it is what
        // distinguishes two named tests of the same method.
        string name = "";
        int nameAt = s.IndexOf(" ,name: ", StringComparison.Ordinal);
        if (nameAt >= 0)
        {
            name = s.Substring(nameAt + " ,name: ".Length).TrimEnd('.').Trim();
            s = s.Substring(0, nameAt);
        }
        s = s.Trim().TrimEnd('.').Trim();

        string signature = ReduceToParameterTypes(s) ?? s;
        return string.IsNullOrEmpty(name) ? signature : $"{signature}#{name}";
    }

    private static string? ReduceToParameterTypes(string signature)
    {
        int open = signature.IndexOf('(');
        if (open < 0 || !signature.EndsWith(")", StringComparison.Ordinal)) return null;

        string methodName = signature.Substring(0, open).Trim();
        string inner = signature.Substring(open + 1, signature.Length - open - 2);
        if (methodName.Length == 0) return null;
        if (inner.Trim().Length == 0) return methodName + "()";

        var parts = SplitTopLevel(inner);
        var types = new List<string>(parts.Count);
        foreach (var p in parts)
        {
            var t = p.Trim();
            if (t.Length == 0) return null;
            // Drop the trailing parameter name: the last whitespace-separated token, but only
            // when there is more than one token at depth 0. "Dictionary<String, Int32> d" has
            // two; a bare "Double" has one and must survive untouched.
            int cut = LastTopLevelSpace(t);
            if (cut > 0) t = t.Substring(0, cut).Trim();
            if (t.Length == 0) return null;
            types.Add(t);
        }
        return $"{methodName}({string.Join(",", types)})";
    }

    private static List<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '<' || c == '[' || c == '(') depth++;
            else if (c == '>' || c == ']' || c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(s.Substring(start, i - start));
                start = i + 1;
            }
        }
        parts.Add(s.Substring(start));
        return parts;
    }

    private static int LastTopLevelSpace(string s)
    {
        int depth = 0, at = -1;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '<' || c == '[' || c == '(') depth++;
            else if (c == '>' || c == ']' || c == ')') depth--;
            else if (char.IsWhiteSpace(c) && depth == 0) at = i;
        }
        return at;
    }

    /* ---------------------------------------------------------------- fingerprint */

    /// <summary>
    /// Order-independent digest over the difference lines of one unit test. Sorted before
    /// hashing so fixture reordering cannot change it; multiplicity is preserved so a second
    /// row failing on the same property does change it.
    /// </summary>
    public static string Fingerprint(IEnumerable<string> differenceLines)
    {
        var lines = (differenceLines ?? Enumerable.Empty<string>())
            .Select(l => l ?? "")
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToList();

        if (lines.Count == 0) return "empty";

        var payload = Encoding.UTF8.GetBytes(string.Join("\n", lines));
        var hash = SHA256.HashData(payload);
        var sb = new StringBuilder(32);
        for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>
    /// One difference, rendered for the fingerprint. Only the property path and the EXPECTED
    /// value: the expected value is read from the fixture and never varies, whereas the value
    /// the run produced varies by a few ULP between runtimes and would make the fingerprint
    /// unusable for the very failures this baseline exists to hold.
    /// </summary>
    public static string DifferenceLine(string? property, object? referenceValue)
        => $"{property ?? ""}|{Canonical(referenceValue)}";

    /// <summary>Round-trippable, culture-invariant rendering of a fixture's expected value.</summary>
    public static string Canonical(object? value) => value switch
    {
        null              => "<null>",
        double d          => d.ToString("R", CultureInfo.InvariantCulture),
        float f           => f.ToString("R", CultureInfo.InvariantCulture),
        decimal m         => m.ToString(CultureInfo.InvariantCulture),
        bool b            => b ? "true" : "false",
        string s          => s,
        IFormattable form => form.ToString(null, CultureInfo.InvariantCulture),
        _                 => value.ToString() ?? "",
    };

    /* ---------------------------------------------------------------- io + rendering */

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Serialise(BaselineFile file) => JsonSerializer.Serialize(file, s_json);

    /// <summary>
    /// Parses a baseline file. Returns null for empty or whitespace input. Throws
    /// JsonException on malformed input: a baseline that cannot be read must not silently
    /// degrade into "no baseline", which would turn every known failure into a new one and
    /// bury the parse error under nine errors that are not the problem.
    /// </summary>
    public static BaselineFile? Deserialise(string? json)
        => string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<BaselineFile>(json, s_json);

    /// <summary>Human-readable one-line verdict.</summary>
    public static string SummaryLine(Report report)
        => $"Dataset baseline: {report.Known.Count} known, {report.New.Count} new, "
         + $"{report.Drifted.Count} drifted, {report.Stale.Count} stale.";

    /// <summary>GitHub job-summary markdown. Rendering only; decides nothing.</summary>
    public static string Markdown(Report report, bool staleFails)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### Dataset unit tests: known-failure baseline");
        sb.AppendLine();
        sb.AppendLine($"- Known (accepted): **{report.Known.Count}**");
        sb.AppendLine($"- New (unaccounted): **{report.New.Count}**");
        sb.AppendLine($"- Drifted (shape changed): **{report.Drifted.Count}**");
        sb.AppendLine($"- Stale (baselined, no longer failing): **{report.Stale.Count}**"
                    + (staleFails ? "" : " _(reported, does not fail the run)_"));
        sb.AppendLine();

        void Table(string heading, List<Classified> rows, bool withDetail)
        {
            if (rows.Count == 0) return;
            sb.AppendLine($"#### {heading}");
            sb.AppendLine();
            sb.AppendLine(withDetail
                ? "| Fixture | Method | Category | What changed |"
                : "| Fixture | Method | Category | Reason |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var r in rows)
                sb.AppendLine($"| `{r.Fixture}` | `{r.MethodKey}` | {r.Category} | "
                            + $"{(withDetail ? r.DriftDetail : Describe(r))} |");
            sb.AppendLine();
        }

        Table("New failures", report.New, false);
        Table("Drifted failures", report.Drifted, true);
        Table("Stale entries — remove these by regenerating", report.Stale, false);
        Table("Known failures", report.Known, false);

        return sb.ToString();
    }

    private static string Describe(Classified c)
    {
        var reason = string.IsNullOrWhiteSpace(c.Reason) ? "—" : c.Reason;
        return string.IsNullOrWhiteSpace(c.Issue) ? reason : $"{reason} ({c.Issue})";
    }
}
