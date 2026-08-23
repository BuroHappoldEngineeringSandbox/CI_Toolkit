using NUnit.Framework;
using System.Text.Json;

/// <summary>
/// Characterisation tests for ComplianceRunner.Main's file accounting and exit code.
///
/// These pin down what the entry point currently does when it examines nothing, which is
/// findings-register item 3: the runner can report success having inspected no file at all.
/// They are written to PASS against today's behaviour on purpose. The register item is
/// Critical and blocked on decision Q3 (fail, warn, or fail only once a repo is gated), so
/// this file records the behaviour rather than asserting a preferred one. Each assertion that
/// is expected to invert once Q3 is answered is marked INVERTS-ON-Q3 with what it should
/// become.
///
/// Main cannot be unit-tested in-process: every branch of it compiles against BHoM types
/// (TestResult, TestStatus, ITestInformation, BH.Engine.Test.CodeCompliance.Compute,
/// BH.Engine.Base.Query), so even the usage path at :18-31, which touches no BHoM type at
/// runtime, cannot be reached without them. Process invocation via RunnerFixture is therefore
/// the only way to observe it without restructuring the runner, and it is the convention the
/// existing E2E tests already use.
/// </summary>
[TestFixture]
[Category("Integration")]
public class MainAccountingTests
{
    // ── The [SKIP] path: relevant extension, file absent (ComplianceRunner.cs:49-53) ──
    //
    // Distinct from the filter path already covered by ComplianceRunnerE2ETests. There, a
    // file is rejected by FileFilter and `continue`d silently. Here the file IS relevant, so
    // it passes the filter, and is then dropped because it is not on disk. That second drop
    // prints a line but changes nothing else: no annotation, no status change, no exit code.

    [Test]
    [Description("A relevant file that is absent from disk is announced as [SKIP] on stdout.")]
    public void MissingRelevantFile_AnnouncesSkipOnStdout()
    {
        var (_, stdout) = RunnerFixture.Run("ComplianceRunner", "code", "definitely-absent.cs");

        Assert.That(stdout, Does.Contain("[SKIP]"),
            "ComplianceRunner.cs:51 prints '  [SKIP] File not found: <file>' for a relevant "
          + "file that is not on disk. If this assertion fails the diagnostic has been removed "
          + "or reworded, and item 3's only current signal has gone with it.");
    }

    [Test]
    [Description("ITEM 3: every relevant file being absent still exits 0 with status Pass.")]
    public void AllRelevantFilesMissing_ExitsZeroWithPassStatus_ITEM3()
    {
        // Three files, all relevant to a code check, none on disk. Nothing is examined.
        var (exitCode, stdout) = RunnerFixture.Run("ComplianceRunner",
            "code", "--output", "github", "a.cs", "b.cs", "c.cs");

        Assert.Multiple(() =>
        {
            // INVERTS-ON-Q3: should become Is.EqualTo(1) if Q3 decides that examining
            // nothing is a failure, or stay 0 with a ::warning if Q3 decides it warns.
            Assert.That(exitCode, Is.EqualTo(0),
                "ITEM 3 (register, Critical). mergedResult.Status is initialised to Pass at "
              + "ComplianceRunner.cs:39 and only ever changes via Merge inside the per-file "
              + "loop. Every file skipping means the loop body never runs, so :162 returns 0. "
              + "A compliance check therefore reports success having inspected nothing.");

            // No annotation is emitted either, so nothing in the GitHub log distinguishes
            // this from a genuine clean pass except the [SKIP] lines.
            Assert.That(stdout, Does.Not.Contain("::error"),
                "No annotation is produced when nothing was examined.");
        });
    }

    [Test]
    [Description("ITEM 3: the count of files actually examined is not reported anywhere.")]
    public void ExaminedCount_IsNotReported_ITEM3()
    {
        // Contrast with VersioningRunner, which prints a Coverage line precisely so that a
        // pass over zero and a pass over thousands are distinguishable (RunCommand.cs:226-230).
        // ComplianceRunner has no equivalent, which is why item 3 is invisible in the log.
        var (_, stdout) = RunnerFixture.Run("ComplianceRunner",
            "code", "--output", "github", "a.cs", "b.cs", "c.cs");

        Assert.That(stdout, Does.Not.Contain("examined"),
            "ITEM 3. There is no coverage line. Adding one is the cheapest partial mitigation "
          + "and does not need Q3 answered, because reporting the number changes no verdict.");
    }

    // ── Machine-readable output and the [SKIP] diagnostic ─────────────────────────────

    [Test]
    [Description("The [SKIP] diagnostic is written to stdout even when the output format is json.")]
    public void MissingRelevantFile_JsonOutput_SkipLinePrecedesTheJson()
    {
        var (exitCode, stdout) = RunnerFixture.Run("ComplianceRunner",
            "code", "--output", "json", "definitely-absent.cs");

        Assert.That(exitCode, Is.EqualTo(0));

        // ComplianceRunner.cs:51 is an unconditional Console.WriteLine, not gated on the
        // `verbose` flag that the console format sets. So in json and sarif modes the
        // diagnostic lands on the same stream as the payload, ahead of it.
        Assert.That(stdout.TrimStart(), Does.StartWith("[SKIP]").Or.StartWith("  [SKIP]"),
            "If this fails, the skip diagnostic has been moved off stdout or behind the "
          + "verbose flag, which would resolve the stream-mixing issue.");

        // The consequence, asserted rather than described: the raw stdout is not valid JSON.
        // Assert.Catch rather than Assert.Throws because the concrete type is
        // JsonReaderException, a subclass, and Assert.Throws matches the exact type only.
        Assert.Catch<JsonException>(() => JsonDocument.Parse(stdout),
            "Raw stdout does not parse as JSON once a skip line is present. Callers using "
          + "--output json must strip leading diagnostics. The existing E2E tests parse "
          + "stdout directly and only pass because the filter path prints nothing.");
    }

    // ── Exit code mapping at :162 ─────────────────────────────────────────────────────

    [Test]
    [Description("A run with no findings maps to exit 0 (the Pass and Warning half of :162).")]
    public void NoFindings_MapsToExitZero()
    {
        var (exitCode, _) = RunnerFixture.Run("ComplianceRunner",
            "code", "--output", "json", "definitely-absent.cs");

        // :162 is `return mergedResult.Status == TestStatus.Error ? 1 : 0`, so Pass and
        // Warning both map to 0. That mirrors BHoMBot deliberately (ComplianceRunner.cs:161).
        // The Error half needs a real finding from the BHoM engine and so belongs with the
        // RequiresBHoM tests, not here.
        Assert.That(exitCode, Is.EqualTo(0));
    }
}
