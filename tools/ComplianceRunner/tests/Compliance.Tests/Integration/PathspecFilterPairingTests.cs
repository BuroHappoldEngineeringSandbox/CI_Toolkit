using NUnit.Framework;
using System.Diagnostics;

/// <summary>
/// Pairs the two halves of file selection against each other, which nothing else does.
///
/// Selection happens twice, in two languages, in two repositories' worth of convention:
///   1. compute-changed-files runs `git diff --name-only --diff-filter=ACMRT HEAD^1 HEAD --
///      &lt;pathspec&gt;` with the pathspec taken from the calling template, e.g.
///      `patterns: '*AssemblyInfo.cs *.csproj'` for a project-compliance job.
///   2. ComplianceRunner then asks FileFilter.IsRelevantFile of every file it was handed.
///
/// Each half has tests. `.github/scripts/tests/test-changed-file-patterns.sh` asserts the
/// pathspec behaviour against real git, and FileFilterTests asserts the predicate. Neither
/// asserts that the two agree, and findings-register item 34b is that they do not: the
/// pathspec token `*AssemblyInfo.cs` selects any file whose name ENDS with that string, while
/// FileFilter.cs:24 requires the name to EQUAL it. A file in between is selected, counted into
/// the skip decision, handed to the runner, and then silently discarded.
///
/// These tests are written to PASS against today's behaviour. They record the disagreement so
/// it is visible in the suite. Whether the fix narrows the pathspec or widens the filter is
/// open: BHoMBot used EndsWith("AssemblyInfo.cs") (ProjectCompliance.cs:33), so widening the
/// filter restores the older semantics, and FileFilterTests.cs:20 currently asserts the
/// narrower one deliberately. Marked INVERTS-ON-34b where a decision would change them.
/// </summary>
[TestFixture]
[Category("Integration")]
public class PathspecFilterPairingTests
{
    // The token shipped by every project-compliance job in templates/{BHoM,BHE}/ci-*.yml.
    private const string ProjectPathspec = "*AssemblyInfo.cs";

    // Ends with "AssemblyInfo.cs" but is not "AssemblyInfo.cs". The pathspec suite's fixture
    // has "Engine/AssemblyInfoHelper.cs", which does NOT end with the token and so does not
    // probe this gap; nothing in either suite currently uses a name of this shape.
    private const string StraddlingFile = "Properties/NotAssemblyInfo.cs";

    [Test]
    [Description("ITEM 34b: the pathspec selects a file the filter then discards.")]
    public void PathspecAndFilter_DisagreeOnAStraddlingName_ITEM34B()
    {
        bool selectedByGit = GitDiffSelects(ProjectPathspec, StraddlingFile);
        bool acceptedByFilter = FileFilter.IsRelevantFile(StraddlingFile, "project");

        Assert.Multiple(() =>
        {
            // Half 1: git selects it, so compute-changed-files counts it and writes it into
            // changed_files.txt, and the check does not self-skip.
            Assert.That(selectedByGit, Is.True,
                "git pathspec '*AssemblyInfo.cs' matches any path ending in that string. "
              + "'*' also matches '/', which is why the leading directory is no obstacle.");

            // Half 2: the runner then drops it. INVERTS-ON-34b if the filter is widened to
            // BHoMBot's EndsWith semantics.
            Assert.That(acceptedByFilter, Is.False,
                "FileFilter.cs:24 requires Path.GetFileName(file).Equals(\"AssemblyInfo.cs\"), "
              + "so the file is discarded with no message.");

            // The pairing, stated as the thing that is actually wrong.
            Assert.That(selectedByGit && !acceptedByFilter, Is.True,
                "ITEM 34b (register). Both halves are individually tested and individually "
              + "defensible; they disagree. A pull request changing only a file of this shape "
              + "produces a green project-compliance check that examined nothing, which is "
              + "item 3 reached by a route no single-layer test can see.");
        });
    }

    [Test]
    [Description("Control: the exact name agrees across both halves, so the gap is specific.")]
    public void PathspecAndFilter_AgreeOnTheExactName()
    {
        const string exact = "Properties/AssemblyInfo.cs";

        Assert.Multiple(() =>
        {
            Assert.That(GitDiffSelects(ProjectPathspec, exact), Is.True);
            Assert.That(FileFilter.IsRelevantFile(exact, "project"), Is.True);
        });
    }

    // ── Fixture ───────────────────────────────────────────────────────────────────────
    //
    // Mirrors compute-changed-files' invocation exactly: same --diff-filter, same HEAD^1..HEAD
    // range, pathspec passed as a trailing -- argument. A throwaway repo rather than the real
    // one so the fixture is explicit and the test cannot be perturbed by the working tree.

    private static bool GitDiffSelects(string pathspec, string relativePath)
    {
        string repo = Path.Combine(Path.GetTempPath(), "pairing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);
        try
        {
            Git(repo, "init", "-q");
            Git(repo, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q",
                     "--allow-empty", "-m", "base");

            string full = Path.Combine(repo, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "// fixture\n");

            Git(repo, "add", "-A");
            Git(repo, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "change");

            string selected = Git(repo, "diff", "--name-only", "--diff-filter=ACMRT",
                                        "HEAD^1", "HEAD", "--", pathspec);

            return selected
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Contains(relativePath);
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* temp dir, best effort */ }
        }
    }

    private static string Git(string workingDir, params string[] args)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory       = workingDir,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        foreach (var a in args) proc.StartInfo.ArgumentList.Add(a);

        proc.Start();
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed with {proc.ExitCode}: {stderr}");

        return stdout;
    }
}
