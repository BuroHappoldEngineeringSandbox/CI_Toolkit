using NUnit.Framework;
using System.Text.Json;

/// <summary>
/// End-to-end tests for DatasetTestRunner (dataset unit-test fixtures).
///
/// Scope note. The emitted annotation shape — title=, the location prefix, the notice mapping,
/// the %0A escaping — is already covered by OutputEmitterTests against the shared emitter. These
/// tests deliberately do not restate it. What they cover is what is specific to this runner:
/// that it routes through that emitter at all rather than through a local copy, and that its
/// file accounting is wired at each of the three loop exits it can take.
///
/// Almost everything here is [Category("RequiresBHoM")]. Unlike the two compliance runners, this
/// one calls LoadAllAssemblies() before it looks at its arguments, so there is no path past the
/// usage message that returns before BHoM is touched.
/// </summary>
[TestFixture]
[Category("Integration")]
public class DatasetTestRunnerE2ETests
{
    // ── Usage / bad args — no BHoM call made ──────────────────────────────────

    [Test]
    public void NoArgs_ExitsWithCode1()
    {
        var (exitCode, _) = RunnerFixture.Run("DatasetTestRunner");
        Assert.That(exitCode, Is.EqualTo(1));
    }

    // ── Coverage denominator ──────────────────────────────────────────────────

    [Test]
    [Category("RequiresBHoM")]
    [Description("A path that is not on disk takes the NotOnDisk exit and is counted, not silently dropped.")]
    public void MissingFile_JsonOutput_CountsTheFileAsNotOnDisk()
    {
        var (_, stdout) = RunnerFixture.Run("DatasetTestRunner",
            "--output", "json", "no/such/fixture.json");

        var coverage = JsonDocument.Parse(stdout).RootElement.GetProperty("coverage");
        Assert.Multiple(() =>
        {
            Assert.That(coverage.GetProperty("handedIn").GetInt32(),    Is.EqualTo(1));
            Assert.That(coverage.GetProperty("examined").GetInt32(),    Is.EqualTo(0));
            Assert.That(coverage.GetProperty("notOnDisk").GetInt32(),   Is.EqualTo(1));
            // No relevance filter in this runner, so this exit can never be taken.
            Assert.That(coverage.GetProperty("notRelevant").GetInt32(), Is.EqualTo(0));
        });
    }

    [Test]
    [Category("RequiresBHoM")]
    [Description("The denominator reaches machine-readable output structurally, not by parsing stdout.")]
    public void JsonOutput_ContainsCoverageKey()
    {
        var (_, stdout) = RunnerFixture.Run("DatasetTestRunner",
            "--output", "json", "no/such/fixture.json");

        Assert.That(JsonDocument.Parse(stdout).RootElement.TryGetProperty("coverage", out _),
            "json output carries no coverage key, so the runner is not passing FileAccounting to OutputEmitter");
    }

    [Test]
    [Category("RequiresBHoM")]
    [Description("github output carries the coverage line and, when nothing was examined, the warning that says so.")]
    public void MissingFile_GithubOutput_ReportsCoverageAndExaminedNothing()
    {
        var (_, stdout) = RunnerFixture.Run("DatasetTestRunner",
            "--output", "github", "no/such/fixture.json");

        Assert.Multiple(() =>
        {
            Assert.That(stdout, Does.Contain("Coverage: 0 of 1 file(s) examined; 1 not found on disk."));
            Assert.That(stdout, Does.Contain("::warning title=Compliance coverage::"));
        });
    }

    // ── Verdict is unchanged by any of the above ──────────────────────────────

    [Test]
    [Category("RequiresBHoM")]
    [Description("Reporting a denominator must not move the verdict. Examining nothing still exits 0 and reports Pass, exactly as before this runner reported coverage at all.")]
    public void MissingFile_ExaminedNothing_StillPassesAndExitsZero()
    {
        var (exitCode, stdout) = RunnerFixture.Run("DatasetTestRunner",
            "--output", "json", "no/such/fixture.json");

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(0));
            Assert.That(JsonDocument.Parse(stdout).RootElement.GetProperty("status").GetString(),
                Is.EqualTo("Pass"));
        });
    }
}
