using NUnit.Framework;
using Rec = DatasetBaseline.FailureRecord;

namespace Compliance.Tests.Unit;

[TestFixture]
public class DatasetBaselineKeyingTests
{
    [Test]
    public void MethodKey_drops_parameter_names()
    {
        Assert.That(
            DatasetBaseline.NormaliseMethodKey(
                "UnitTest: Method: PlaneIntersections(Ellipse curve, Plane plane, Double tolerance)."),
            Is.EqualTo("PlaneIntersections(Ellipse,Plane,Double)"));
    }

    [Test]
    public void MethodKey_distinguishes_overloads_by_type()
    {
        var a = DatasetBaseline.NormaliseMethodKey(
            "UnitTest: Method: SectionPropertyAtParameter(Bar bar, Double position).");
        var b = DatasetBaseline.NormaliseMethodKey(
            "UnitTest: Method: SectionPropertyAtParameter(ISectionProperty sectionProperty, Double position).");
        Assert.That(a, Is.Not.EqualTo(b));
        Assert.That(a, Is.EqualTo("SectionPropertyAtParameter(Bar,Double)"));
    }

    [Test]
    public void MethodKey_keeps_the_test_name_when_one_is_set()
    {
        Assert.That(
            DatasetBaseline.NormaliseMethodKey("UnitTest: Method: Round(Double number) ,name: negatives."),
            Is.EqualTo("Round(Double)#negatives"));
    }

    [Test]
    public void MethodKey_survives_a_generic_parameter_containing_a_comma()
    {
        Assert.That(
            DatasetBaseline.NormaliseMethodKey(
                "UnitTest: Method: Lookup(Dictionary<String, Int32> map, String key)."),
            Is.EqualTo("Lookup(Dictionary<String, Int32>,String)"));
    }

    [Test]
    public void MethodKey_falls_back_to_the_raw_description_when_it_does_not_parse()
    {
        Assert.That(DatasetBaseline.NormaliseMethodKey("UnitTest: Method: not a signature"),
                    Is.EqualTo("not a signature"));
    }

    [Test]
    public void MethodKey_handles_a_parameterless_method()
    {
        Assert.That(DatasetBaseline.NormaliseMethodKey("UnitTest: Method: Now()."),
                    Is.EqualTo("Now()"));
    }

    [Test]
    public void Fingerprint_is_independent_of_difference_order()
    {
        var a = DatasetBaseline.Fingerprint(new[] { "[0].X|1.5", "[0].Y|2.5" });
        var b = DatasetBaseline.Fingerprint(new[] { "[0].Y|2.5", "[0].X|1.5" });
        Assert.That(a, Is.EqualTo(b));
    }

    [Test]
    public void Fingerprint_changes_when_a_difference_appears()
    {
        var a = DatasetBaseline.Fingerprint(new[] { "[0].X|1.5" });
        var b = DatasetBaseline.Fingerprint(new[] { "[0].X|1.5", "[1].X|9.0" });
        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public void Fingerprint_preserves_multiplicity()
    {
        var a = DatasetBaseline.Fingerprint(new[] { "[0].X|1.5" });
        var b = DatasetBaseline.Fingerprint(new[] { "[0].X|1.5", "[0].X|1.5" });
        Assert.That(a, Is.Not.EqualTo(b));
    }

    [Test]
    public void DifferenceLine_uses_the_expected_value_not_the_run_value()
    {
        // Round-trip formatting: the stored 17-significant-digit value must survive verbatim,
        // otherwise two fixtures differing only in the last digit collide.
        var line = DatasetBaseline.DifferenceLine("[0].X", -1022.7676520933219);
        Assert.That(line, Is.EqualTo("[0].X|-1022.7676520933219"));
    }

    [Test]
    public void Canonical_renders_null_and_invariant_culture()
    {
        Assert.That(DatasetBaseline.Canonical(null), Is.EqualTo("<null>"));
        Assert.That(DatasetBaseline.Canonical(1.5), Is.EqualTo("1.5"));
        Assert.That(DatasetBaseline.Canonical(true), Is.EqualTo("true"));
    }
}

[TestFixture]
public class DatasetBaselineClassifyTests
{
    private static Rec Record(string fixture, string method, string fp = "aaaa0000",
                              int rows = 1, int diffs = 3) => new()
    {
        Fixture          = fixture,
        MethodKey        = method,
        Description      = $"UnitTest: Method: {method}.",
        Status           = "Error",
        FailingDataCount = rows,
        DifferenceCount  = diffs,
        Fingerprint      = fp,
    };

    private static DatasetBaseline.BaselineEntry Entry(
        string fixture, string method, string fp = "aaaa0000",
        string category = DatasetBaseline.CategoryComparer,
        string match = DatasetBaseline.MatchExact,
        int rows = 1, int diffs = 3) => new()
    {
        Fixture          = fixture,
        MethodKey        = method,
        Category         = category,
        Reason           = "runtime-serialised expectations",
        Match            = match,
        FailingDataCount = rows,
        DifferenceCount  = diffs,
        Fingerprint      = fp,
    };

    private static DatasetBaseline.BaselineFile File(params DatasetBaseline.BaselineEntry[] e)
        => new() { Entries = e.ToList() };

    [Test]
    public void A_baselined_failure_with_a_matching_fingerprint_is_known_and_does_not_fail()
    {
        var report = DatasetBaseline.Classify(
            new[] { Record("a.json", "M(Double)") },
            File(Entry("a.json", "M(Double)")),
            new[] { "a.json" });

        Assert.That(report.Known, Has.Count.EqualTo(1));
        Assert.That(report.HasUnaccounted, Is.False);
        Assert.That(report.Known[0].Category, Is.EqualTo(DatasetBaseline.CategoryComparer));
    }

    [Test]
    public void A_failure_absent_from_the_baseline_is_new_and_fails()
    {
        var report = DatasetBaseline.Classify(
            new[] { Record("a.json", "M(Double)"), Record("b.json", "N(Int32)") },
            File(Entry("a.json", "M(Double)")),
            new[] { "a.json", "b.json" });

        Assert.That(report.New, Has.Count.EqualTo(1));
        Assert.That(report.New[0].MethodKey, Is.EqualTo("N(Int32)"));
        Assert.That(report.HasUnaccounted, Is.True);
    }

    [Test]
    public void A_baselined_entry_that_stopped_failing_is_stale_and_does_not_fail_by_default()
    {
        var report = DatasetBaseline.Classify(
            Array.Empty<Rec>(),
            File(Entry("a.json", "M(Double)")),
            new[] { "a.json" });

        Assert.That(report.Stale, Has.Count.EqualTo(1));
        Assert.That(report.HasUnaccounted, Is.False);
    }

    [Test]
    public void Staleness_is_only_reported_for_fixtures_that_ran()
    {
        // The dataset-only change path hands the runner a subset. Everything else must not be
        // declared stale just because it was not executed.
        var report = DatasetBaseline.Classify(
            Array.Empty<Rec>(),
            File(Entry("a.json", "M(Double)"), Entry("b.json", "N(Int32)")),
            new[] { "a.json" });

        Assert.That(report.Stale, Has.Count.EqualTo(1));
        Assert.That(report.Stale[0].Fixture, Is.EqualTo("a.json"));
    }

    [Test]
    public void A_changed_failure_shape_is_drift_and_fails()
    {
        var report = DatasetBaseline.Classify(
            new[] { Record("a.json", "M(Double)", fp: "bbbb1111", rows: 2, diffs: 6) },
            File(Entry("a.json", "M(Double)", fp: "aaaa0000", rows: 1, diffs: 3)),
            new[] { "a.json" });

        Assert.That(report.Drifted, Has.Count.EqualTo(1));
        Assert.That(report.HasUnaccounted, Is.True);
        Assert.That(report.Drifted[0].DriftDetail, Does.Contain("6 difference(s) over 2 row(s)"));
    }

    [Test]
    public void Match_method_accepts_a_changed_shape()
    {
        // The near-tangency fixtures flip between one and two intersection points, so their
        // difference set varies without the defect changing.
        var report = DatasetBaseline.Classify(
            new[] { Record("a.json", "PlaneIntersections(Ellipse,Plane,Double)", fp: "bbbb1111") },
            File(Entry("a.json", "PlaneIntersections(Ellipse,Plane,Double)", fp: "aaaa0000",
                       category: DatasetBaseline.CategoryEngine,
                       match: DatasetBaseline.MatchMethod)),
            new[] { "a.json" });

        Assert.That(report.Drifted, Is.Empty);
        Assert.That(report.Known, Has.Count.EqualTo(1));
        Assert.That(report.Known[0].Category, Is.EqualTo(DatasetBaseline.CategoryEngine));
    }

    [Test]
    public void An_entry_with_no_fingerprint_degrades_to_method_matching()
    {
        var report = DatasetBaseline.Classify(
            new[] { Record("a.json", "M(Double)", fp: "bbbb1111") },
            File(Entry("a.json", "M(Double)", fp: "")),
            new[] { "a.json" });

        Assert.That(report.Known, Has.Count.EqualTo(1));
        Assert.That(report.Drifted, Is.Empty);
    }

    [Test]
    public void No_baseline_file_makes_every_failure_new()
    {
        var report = DatasetBaseline.Classify(new[] { Record("a.json", "M(Double)") }, null, new[] { "a.json" });
        Assert.That(report.New, Has.Count.EqualTo(1));
        Assert.That(report.HasUnaccounted, Is.True);
    }

    [Test]
    public void Reordering_a_fixture_does_not_change_the_key()
    {
        // Row ordinals are deliberately not part of the key. Two runs that disagree only about
        // which index a row sits at must classify identically.
        var before = Record("a.json", "M(Double)", fp: DatasetBaseline.Fingerprint(new[] { "[0].X|1.5", "[1].X|2.5" }));
        var after  = Record("a.json", "M(Double)", fp: DatasetBaseline.Fingerprint(new[] { "[1].X|2.5", "[0].X|1.5" }));
        Assert.That(before.Key, Is.EqualTo(after.Key));
        Assert.That(before.Fingerprint, Is.EqualTo(after.Fingerprint));
    }
}

[TestFixture]
public class DatasetBaselineGenerateTests
{
    private static Rec Record(string fixture, string method, string fp) => new()
    {
        Fixture = fixture, MethodKey = method, Fingerprint = fp,
        Description = $"UnitTest: Method: {method}.", Status = "Error",
        FailingDataCount = 1, DifferenceCount = 3,
    };

    [Test]
    public void Generate_carries_the_human_authored_fields_forward_by_key()
    {
        var previous = new DatasetBaseline.BaselineFile
        {
            Entries = new()
            {
                new DatasetBaseline.BaselineEntry
                {
                    Fixture = "a.json", MethodKey = "M(Double)",
                    Category = DatasetBaseline.CategoryEngine,
                    Reason = "near-tangency branch", Issue = "BHoM/BHoM_Engine#9999",
                    Match = DatasetBaseline.MatchMethod, Fingerprint = "old00000",
                },
            },
        };

        var generated = DatasetBaseline.Generate(
            new[] { Record("a.json", "M(Double)", "new11111") }, previous, "2026-01-01T00:00:00Z");

        Assert.That(generated.Entries, Has.Count.EqualTo(1));
        var e = generated.Entries[0];
        Assert.That(e.Category, Is.EqualTo(DatasetBaseline.CategoryEngine));
        Assert.That(e.Reason, Is.EqualTo("near-tangency branch"));
        Assert.That(e.Issue, Is.EqualTo("BHoM/BHoM_Engine#9999"));
        Assert.That(e.Match, Is.EqualTo(DatasetBaseline.MatchMethod));
        // Machine-derived fields are always rewritten.
        Assert.That(e.Fingerprint, Is.EqualTo("new11111"));
    }

    [Test]
    public void Generate_drops_entries_that_no_longer_fail()
    {
        var previous = new DatasetBaseline.BaselineFile
        {
            Entries = new()
            {
                new DatasetBaseline.BaselineEntry { Fixture = "a.json", MethodKey = "M(Double)" },
                new DatasetBaseline.BaselineEntry { Fixture = "b.json", MethodKey = "N(Int32)" },
            },
        };

        var generated = DatasetBaseline.Generate(new[] { Record("a.json", "M(Double)", "fp") }, previous);

        Assert.That(generated.Entries, Has.Count.EqualTo(1));
        Assert.That(generated.Entries[0].Fixture, Is.EqualTo("a.json"));
    }

    [Test]
    public void Generate_marks_a_brand_new_entry_unknown_so_it_must_be_categorised()
    {
        var generated = DatasetBaseline.Generate(new[] { Record("z.json", "Q(Double)", "fp") }, null);
        Assert.That(generated.Entries[0].Category, Is.EqualTo(DatasetBaseline.CategoryUnknown));
        Assert.That(generated.Entries[0].Reason, Is.Empty);
    }

    [Test]
    public void Generate_output_is_deterministic_and_round_trips()
    {
        var recs = new[] { Record("b.json", "N(Int32)", "f2"), Record("a.json", "M(Double)", "f1") };
        var one = DatasetBaseline.Serialise(DatasetBaseline.Generate(recs, null, "stamp"));
        var two = DatasetBaseline.Serialise(DatasetBaseline.Generate(recs, null, "stamp"));
        Assert.That(one, Is.EqualTo(two));

        var parsed = DatasetBaseline.Deserialise(one);
        Assert.That(parsed, Is.Not.Null);
        Assert.That(parsed!.Entries.Select(e => e.Fixture), Is.EqualTo(new[] { "a.json", "b.json" }));
    }

    [Test]
    public void Deserialise_returns_null_for_empty_input_and_throws_on_malformed()
    {
        Assert.That(DatasetBaseline.Deserialise(""), Is.Null);
        Assert.Throws<System.Text.Json.JsonException>(() => DatasetBaseline.Deserialise("{ not json"));
    }
}
