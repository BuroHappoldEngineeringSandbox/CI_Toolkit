using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BH.Engine.Test;                   // Modify.Merge
using BH.Engine.UnitTest;               // CheckTest extension method
using BH.oM.Test;                       // TestStatus
using BH.oM.Test.Results;              // TestResult, ITestInformation

class DatasetTestRunner
{
    static int Main(string[] args)
    {
        // CLI: DatasetTestRunner [--output console|github|json|sarif] [--sarif-file PATH]
        //                        <file1.json> [file2.json ...]
        var (outputFormat, sarifFilePath, files) = ArgParser.ParseDataset(args);
        if (files == null || files.Count == 0)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  DatasetTestRunner [--output console|github|json|sarif] [--sarif-file PATH]");
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

        // Three of FileAccounting's four exits. This runner applies no relevance filter — the
        // action hands it the fixtures it already selected — so NotRelevant stays zero and the
        // denominator reads as examined / handed in.
        var accounting = new FileAccounting(files.Count);

        foreach (var file in files)
        {
            if (verbose) Console.WriteLine($"\n=== Running: {file} ===");

            if (!File.Exists(file))
            {
                accounting.CountNotOnDisk();
                Console.WriteLine($"  [SKIP] File not found: {file}");
                continue;
            }

            var result = file.CheckTest();

            if (result == null)
            {
                accounting.CountNoResult();
                Console.WriteLine($"  [SKIP] No result returned for: {file}");
                continue;
            }

            if (verbose) Console.WriteLine($"  Result Status: {result.Status}");

            accounting.CountExamined();
            mergedResult = mergedResult.Merge(result);

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

        return mergedResult.Status == TestStatus.Error ? 1 : 0;
    }
}
