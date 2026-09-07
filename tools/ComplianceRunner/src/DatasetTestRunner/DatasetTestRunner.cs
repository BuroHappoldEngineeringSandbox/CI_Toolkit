using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BH.Engine.Test;                   // Modify.Merge
using BH.Engine.UnitTest;               // CheckTest extension method
using BH.oM.Test;                       // TestStatus
using BH.oM.Test.Results;              // TestResult, ITestInformation
using BH.oM.UnitTest.Results;           // ComparisonDifference

class DatasetTestRunner
{
    static int Main(string[] args)
    {
        // CLI: DatasetTestRunner [--output console|github|json|sarif] [--sarif-file PATH]
        //                        [--baseline PATH] [--write-baseline PATH]
        //                        [--dump-failures PATH] [--stale-fails]
        //                        [--generated-stamp TEXT]
        //                        <file1.json> [file2.json ...]
        var parsed = ArgParser.ParseDataset(args);
        var (outputFormat, sarifFilePath, files) = (parsed.OutputFormat, parsed.SarifFilePath, parsed.Files);
        if (files == null || files.Count == 0)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  DatasetTestRunner [--output console|github|json|sarif] [--sarif-file PATH]");
            Console.WriteLine("                    [--baseline PATH] [--write-baseline PATH]");
            Console.WriteLine("                    [--dump-failures PATH] [--stale-fails]");
            Console.WriteLine("                    <file1.json> [file2.json ...]");
            return 1;
        }

        if (outputFormat == "sarif" && !string.IsNullOrEmpty(sarifFilePath))
            outputFormat = "sarif-file";

        bool verbose = outputFormat == "console";
        if (verbose) Console.WriteLine("Running BHoM DATASET UNIT TESTS...");

        // Load all BHoM assemblies including the caller's compiled DLLs.
        // Required because CheckTest() executes methods via reflection and needs
        // the caller's type system to be fully loaded in the AppDomain.
        BH.Engine.Base.Compute.LoadAllAssemblies();

        var mergedResult   = new TestResult() { Status = TestStatus.Pass, Information = new List<ITestInformation>() };
        var allAnnotations = new List<Annotation>();

        // Per-unit-test failure records, keyed by (repo-relative fixture, normalised method).
        // Collected on every run, whether or not a baseline is in play, so --dump-failures can
        // capture a run's set without changing its verdict.
        var failureRecords   = new List<DatasetBaseline.FailureRecord>();
        var executedFixtures = new List<string>();

        // Three of FileAccounting's four exits. This runner applies no relevance filter — the
        // action hands it the fixtures it already selected — so NotRelevant stays zero and the
        // denominator reads as examined / handed in.
        var accounting = new FileAccounting(files.Count);

        foreach (var file in files)
        {
            if (verbose) Console.WriteLine($"\n=== Running: {file} ===");

            // The two [SKIP] diagnostics go to stderr, not stdout. They were unconditional
            // Console.WriteLine, which corrupts the json and sarif formats: those put a single
            // payload on stdout and nothing may precede it, so one missing fixture made the
            // output unparseable from its first character. Pre-existing, and OutputEmitter.cs
            // already cites this exact diagnostic as the reason it guards its own coverage
            // line. Routing round it was enough while coverage was console-only; it is not now
            // that the counts are part of the json payload.
            //
            // stderr rather than deleting them: both streams reach the job log, so the
            // diagnostic is preserved everywhere it was visible before, and the counts now
            // carry the same fact structurally.
            if (!File.Exists(file))
            {
                accounting.CountNotOnDisk();
                Console.Error.WriteLine($"  [SKIP] File not found: {file}");
                continue;
            }

            var result = file.CheckTest();

            if (result == null)
            {
                accounting.CountNoResult();
                Console.Error.WriteLine($"  [SKIP] No result returned for: {file}");
                continue;
            }

            if (verbose) Console.WriteLine($"  Result Status: {result.Status}");

            accounting.CountExamined();
            mergedResult = mergedResult.Merge(result);

            // Record the fixture as executed BEFORE testing whether it failed. Staleness is
            // only meaningful for fixtures that ran: on a dataset-only change the action hands
            // over a subset, and without this every other baselined entry would report stale.
            var fixtureKey = PathHelper.NormaliseAnnotationPath(file);
            executedFixtures.Add(fixtureKey);
            failureRecords.AddRange(CollectFailures(fixtureKey, result));

            var information        = (result.Information ?? Enumerable.Empty<ITestInformation>())
                                     .Where(i => i.Status != TestStatus.Pass);
            var perFileAnnotations = information
                .Select(i => i.ToAnnotationEquivalent())
                .ToList();
            var infoList = information.ToList();

            for (int i = 0; i < perFileAnnotations.Count; i++)
            {
                var a = perFileAnnotations[i];
                if (string.IsNullOrEmpty(a.FilePath))
                    a.FilePath = file;
                if (a.LineStart <= 0)
                    a.LineStart = 1;

                if (verbose)
                {
                    Console.WriteLine($"  - [{a.Level}] {a.FilePath}:{a.LineStart} [{a.RuleName}]");
                    Console.WriteLine($"    {a.Message}");
                    if (i < infoList.Count)
                        AnnotationConvert.LogDetailedFinding(infoList[i]);
                }
                allAnnotations.Add(a);
            }
        }

        const string checkType = "dataset-tests";

        // Routed through the shared emitter rather than a local copy. The copy that stood here
        // had drifted from OutputEmitter in three ways, all of them silent:
        //   - it omitted title= and the location prefix on the message, which is the workaround
        //     OutputEmitter documents for GitHub stripping file= out of the rendered log line.
        //     Measured: the annotation anchored correctly and the log line carried no path at
        //     all, so a failing fixture named nothing a reader could act on.
        //   - it mapped every non-failure level to warning, so a notice was reported as a
        //     warning.
        //   - it flattened newlines to spaces instead of %0A, so a nested result hierarchy
        //     arrived as one long line.
        // Passing accounting also gives this runner the coverage denominator the other four
        // already report. Verdict is unchanged: the exit code below is untouched, and Write
        // reports rather than decides.
        OutputEmitter.Write(
            outputFormat,
            checkType,
            mergedResult.Status,
            allAnnotations,
            sarifFilePath,
            verbose,
            BH.Engine.Base.Query.DocumentationURL("DevOps/Code%20Compliance%20and%20CI/Compliance%20Checks/"),
            accounting);

        if (!string.IsNullOrEmpty(parsed.DumpFailuresPath))
        {
            File.WriteAllText(parsed.DumpFailuresPath,
                System.Text.Json.JsonSerializer.Serialize(
                    new { executedFixtures, failures = failureRecords },
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            if (verbose) Console.WriteLine($"Failure records written to {parsed.DumpFailuresPath}");
        }

        // Regeneration. Reads the existing baseline if one is present so the human-authored
        // fields survive, then writes the observed set. Deliberately does not gate: a
        // regeneration run reports what it wrote and exits 0 on the baseline's account.
        if (!string.IsNullOrEmpty(parsed.WriteBaselinePath))
        {
            DatasetBaseline.BaselineFile? previous = null;
            if (!string.IsNullOrEmpty(parsed.BaselinePath) && File.Exists(parsed.BaselinePath))
                previous = DatasetBaseline.Deserialise(File.ReadAllText(parsed.BaselinePath));

            var generated = DatasetBaseline.Generate(
                failureRecords, previous, parsed.GeneratedStamp,
                "Regenerate with the ci-dataset-tests action's regenerate_baseline input. "
              + "category and reason are carried forward by key and must be filled in by hand "
              + "for any new entry.");

            File.WriteAllText(parsed.WriteBaselinePath, DatasetBaseline.Serialise(generated));
            Console.WriteLine($"Baseline written to {parsed.WriteBaselinePath} "
                            + $"({generated.Entries.Count} entry(ies)).");
            return 0;
        }

        // No baseline in play: the verdict is exactly what it was before this option existed.
        if (string.IsNullOrEmpty(parsed.BaselinePath))
            return mergedResult.Status == TestStatus.Error ? 1 : 0;

        // A baseline path that was asked for and is not there is a configuration error, not an
        // empty baseline. Treating it as empty would report every known failure as new, which
        // reads as nine regressions and hides the one real problem.
        if (!File.Exists(parsed.BaselinePath))
        {
            Console.WriteLine($"::error title=DatasetTests::Baseline file not found: {parsed.BaselinePath}");
            return 1;
        }

        DatasetBaseline.BaselineFile? baseline;
        try
        {
            baseline = DatasetBaseline.Deserialise(File.ReadAllText(parsed.BaselinePath));
        }
        catch (System.Text.Json.JsonException e)
        {
            Console.WriteLine($"::error title=DatasetTests::Baseline file {parsed.BaselinePath} "
                            + $"could not be parsed: {e.Message}");
            return 1;
        }

        var report = DatasetBaseline.Classify(failureRecords, baseline, executedFixtures);
        Console.WriteLine(DatasetBaseline.SummaryLine(report));

        if (outputFormat == "github")
        {
            foreach (var n in report.New)
                Console.WriteLine($"::error file={n.Fixture},line=1,title=New dataset test failure::"
                                + $"{n.Fixture}: {n.MethodKey} is failing and is not in the dataset-test "
                                + "baseline. Either it is a regression from this change, or the baseline "
                                + "needs regenerating with a reason recorded.");
            foreach (var d in report.Drifted)
                Console.WriteLine($"::error file={d.Fixture},line=1,title=Dataset test failure changed::"
                                + $"{d.Fixture}: {d.MethodKey} is in the baseline but fails differently: "
                                + $"{d.DriftDetail}");
            foreach (var s in report.Stale)
                Console.WriteLine($"::{(parsed.StaleFails ? "error" : "warning")} file={s.Fixture},line=1,"
                                + $"title=Stale baseline entry::{s.Fixture}: {s.MethodKey} is in the "
                                + "dataset-test baseline but passed. Regenerate the baseline to remove it.");
        }

        var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(summaryPath))
            File.AppendAllText(summaryPath, DatasetBaseline.Markdown(report, parsed.StaleFails));

        if (report.HasUnaccounted) return 1;
        if (parsed.StaleFails && report.Stale.Count > 0) return 1;
        return 0;
    }

    /// <summary>
    /// Walks one fixture's result tree into per-unit-test failure records.
    ///
    /// The tree CheckTest builds is three deep: the dataset result's Information holds one
    /// TestResult per UnitTest, whose Information holds one TestResult per TestData row, whose
    /// Information holds the ComparisonDifferences. Only the top level is currently turned into
    /// annotations, which is why the job log carries one truncated message per method and
    /// nothing keyable.
    /// </summary>
    static List<DatasetBaseline.FailureRecord> CollectFailures(string fixtureKey, TestResult datasetResult)
    {
        var records = new List<DatasetBaseline.FailureRecord>();

        foreach (var unitInfo in datasetResult.Information ?? new List<ITestInformation>())
        {
            if (unitInfo == null || unitInfo.Status == TestStatus.Pass) continue;

            var lines            = new List<string>();
            int failingDataCount = 0;

            // ITestInformation carries only Message, Status and UTCTime. Description (which
            // holds the method signature) and Information (the child rows) are declared on
            // the concrete TestResult, so the identity this baseline keys on is reachable
            // only through the cast. CheckTest always populates the tree with TestResults;
            // anything else falls back to Message, which at least keys on something.
            string description = (unitInfo as TestResult)?.Description ?? unitInfo.Message ?? "";

            if (unitInfo is TestResult unitResult)
            {
                foreach (var dataInfo in unitResult.Information ?? new List<ITestInformation>())
                {
                    if (dataInfo == null || dataInfo.Status == TestStatus.Pass) continue;
                    failingDataCount++;

                    var differences = (dataInfo as TestResult)?.Information ?? new List<ITestInformation>();
                    int before = lines.Count;

                    foreach (var diff in differences)
                        if (diff is ComparisonDifference cd)
                            lines.Add(DatasetBaseline.DifferenceLine(cd.Property, cd.ReferenceValue));

                    // A row can fail without producing any ComparisonDifference: CheckTest
                    // returns early with only a Message when the method threw or returned a
                    // different number of outputs. Those messages are fixed strings except the
                    // exception path, whose text is not stable — such an entry should carry
                    // match: method in the baseline rather than an exact fingerprint.
                    if (lines.Count == before)
                        lines.Add("!" + FirstLine(dataInfo.Message));
                }
            }

            records.Add(new DatasetBaseline.FailureRecord
            {
                Fixture          = fixtureKey,
                MethodKey        = DatasetBaseline.NormaliseMethodKey(description),
                Description      = description,
                Status           = unitInfo.Status.ToString(),
                FailingDataCount = failingDataCount,
                DifferenceCount  = lines.Count(l => !l.StartsWith("!", StringComparison.Ordinal)),
                Fingerprint      = DatasetBaseline.Fingerprint(lines),
            });
        }

        return records;
    }

    static string FirstLine(string? s)
    {
        var t = (s ?? "").Replace("\r", "");
        int nl = t.IndexOf('\n');
        return (nl < 0 ? t : t.Substring(0, nl)).Trim();
    }
}
