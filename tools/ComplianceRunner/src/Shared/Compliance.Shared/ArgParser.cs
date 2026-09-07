/// <summary>Command-line argument parsing for compliance runners.</summary>
public static class ArgParser
{
    private static readonly HashSet<string> ValidOutputFormats =
        new(StringComparer.OrdinalIgnoreCase) { "console", "github", "json", "sarif" };

    private static readonly HashSet<string> ValidCheckTypes =
        new(StringComparer.OrdinalIgnoreCase) { "code", "copyright", "documentation", "project" };

    public sealed record ComplianceArgs(
        string        OutputFormat,
        string?       SarifFilePath,
        string?       CheckType,
        List<string>? Files,
        string        OrgUrl);

    public sealed record DatasetArgs(
        string        OutputFormat,
        string?       SarifFilePath,
        List<string>? Files,
        // Known-failure baseline. All three default to null, so a runner invoked exactly as
        // it is today behaves exactly as it does today: any Error is exit 1.
        string?       BaselinePath      = null,
        string?       WriteBaselinePath = null,
        string?       DumpFailuresPath  = null,
        // Stale entries are reported but do not fail the run by default. Failing on stale
        // would turn every repo red the moment a Test_Toolkit comparer fix lands and before
        // anyone has regenerated, which is the opposite of the intent.
        bool          StaleFails        = false,
        string        GeneratedStamp    = "");

    public static ComplianceArgs ParseCompliance(string[] args)
    {
        string  outputFormat  = "console";
        string? sarifFilePath = null;
        string  orgUrl        = "";
        var     rest          = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--output" && i + 1 < args.Length)
            {
                var fmt = args[++i].ToLowerInvariant();
                outputFormat = ValidOutputFormats.Contains(fmt) ? fmt : "console";
            }
            else if ((args[i] == "--sarif-file" || args[i] == "--sarif") && i + 1 < args.Length)
                sarifFilePath = args[++i];
            else if (args[i] == "--org-url" && i + 1 < args.Length)
                orgUrl = args[++i];
            else
                rest.Add(args[i]);
        }

        if (rest.Count < 2)
            return new ComplianceArgs(outputFormat, sarifFilePath, null, null, orgUrl);

        var checkType = rest[0].Trim().ToLowerInvariant();
        if (!ValidCheckTypes.Contains(checkType))
            return new ComplianceArgs(outputFormat, sarifFilePath, null, null, orgUrl);

        return new ComplianceArgs(outputFormat, sarifFilePath, checkType, rest.Skip(1).ToList(), orgUrl);
    }

    public static DatasetArgs ParseDataset(string[] args)
    {
        string  outputFormat  = "console";
        string? sarifFilePath = null;
        string? baseline      = null;
        string? writeBaseline = null;
        string? dumpFailures  = null;
        bool    staleFails    = false;
        string  stamp         = "";
        var     rest          = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--output" && i + 1 < args.Length)
            {
                var fmt = args[++i].ToLowerInvariant();
                outputFormat = ValidOutputFormats.Contains(fmt) ? fmt : "console";
            }
            else if ((args[i] == "--sarif-file" || args[i] == "--sarif") && i + 1 < args.Length)
                sarifFilePath = args[++i];
            else if (args[i] == "--baseline" && i + 1 < args.Length)
                baseline = args[++i];
            else if (args[i] == "--write-baseline" && i + 1 < args.Length)
                writeBaseline = args[++i];
            else if (args[i] == "--dump-failures" && i + 1 < args.Length)
                dumpFailures = args[++i];
            else if (args[i] == "--generated-stamp" && i + 1 < args.Length)
                stamp = args[++i];
            else if (args[i] == "--stale-fails")
                staleFails = true;
            else
                rest.Add(args[i]);
        }

        return new DatasetArgs(
            outputFormat, sarifFilePath, rest.Count == 0 ? null : rest,
            baseline, writeBaseline, dumpFailures, staleFails, stamp);
    }
}
