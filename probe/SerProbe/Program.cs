using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// Scratch probe for the #3631 serialisation experiment. Reflection only: nothing BHoM is referenced
// at compile time, and assemblies are loaded exactly as SerialiserRunner (CI_Toolkit 38bc7fc) does.
//   SerProbe legs    <assembliesDir> <out.json>
//   SerProbe capture <assembliesDir> <outDir>
//   SerProbe read93  <assembliesDir> <Methods.json> <out.json>

static class Program
{
    static readonly string[] Suffixes = ["oM.dll", "_Engine.dll", "_Adapter.dll", "_Test.dll"];
    static readonly JsonSerializerOptions JsonOut = new() { WriteIndented = true };

    // Copied verbatim from tools/VersioningRunner/src/VersioningRunner/Commands/RunCommand.cs at CI_Toolkit 38bc7fc.
    static readonly Regex MethodEventPattern = new(
        @"^Method\s+(?<method>\S+)\s+from\s+.*?""Name""\s*:\s*""(?<type>[^"",]+)",
        RegexOptions.Compiled);
    static readonly Regex MethodEventAssemblyPattern = new(
        @"^Method\s+\S+\s+from\s+.*?""Name""\s*:\s*""[^"",]+,\s*(?<assembly>[^"",]+)",
        RegexOptions.Compiled);

    static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: SerProbe legs|capture|read93 <assembliesDir> ...");
            return 64;
        }
        LoadAssemblies(args[1]);
        return args[0] switch
        {
            "legs" => Legs(args[1], args[2]),
            "capture" => Capture(args[2]),
            "read93" when args.Length >= 4 => Read93(args[2], args[3]),
            _ => 64
        };
    }

    // ---- loading, as SerialiserRunner.RunCommand.LoadAssemblies ----

    static void LoadAssemblies(string folder)
    {
        int loaded = 0, failed = 0;
        foreach (string file in Directory.GetFiles(folder))
        {
            string name = Path.GetFileName(file);
            if (!Suffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                continue;
            try { Assembly.LoadFrom(file); loaded++; }
            catch (Exception ex) { failed++; Console.Error.WriteLine($"Warning: could not load {file}: {ex.Message}"); }
        }
        Console.WriteLine($"Assemblies: {loaded} loaded, {failed} failed, from {folder}");
    }

    static Type T(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => { try { return a.GetType(fullName); } catch { return null; } })
            .FirstOrDefault(t => t != null)
        ?? throw new InvalidOperationException($"Type not found: {fullName}");

    static object? Call(string type, string method, Type[] sig, params object?[] args) =>
        (T(type).GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, sig, null)
            ?? throw new InvalidOperationException($"Method not found: {type}.{method}"))
        .Invoke(null, args);

    static void ClearEvents() => Call("BH.Engine.Base.Compute", "ClearCurrentEvents", Type.EmptyTypes);

    static List<string> Events()
    {
        var result = new List<string>();
        if (Call("BH.Engine.Base.Query", "CurrentEvents", Type.EmptyTypes) is IEnumerable events)
            foreach (object e in events)
                result.Add(e.GetType().GetProperty("Message")?.GetValue(e)?.ToString() ?? "");
        return result;
    }

    static string? ToJson(object o) => Call("BH.Engine.Serialiser.Convert", "ToJson", [typeof(object)], o) as string;
    static object? FromJson(string s) => Call("BH.Engine.Serialiser.Convert", "FromJson", [typeof(string)], s);

    static string ToText(Type t) =>
        (string)Call("BH.Engine.Base.Convert", "ToText",
            [typeof(Type), typeof(bool), typeof(bool), typeof(string), typeof(string), typeof(string)],
            t, true, false, "<", ", ", ">")!;

    static ParameterInfo[] ParametersWithConstraints(MethodBase m) =>
        (ParameterInfo[])Call("BH.Engine.Reflection.Query", "ParametersWithConstraints", [typeof(MethodBase)], m)!;

    // ---- legs: each Verify method separately, every non-passing item kept with its leg ----

    static int Legs(string folder, string outPath)
    {
        var verifyMethods = new List<MethodInfo>();
        foreach (string file in Directory.GetFiles(folder).Where(f => f.EndsWith("_Test.dll", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Assembly asm = Assembly.LoadFrom(file);
                Type?[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                foreach (Type? type in types.Where(t => t?.Name == "Verify"
                    && t.Namespace?.StartsWith("BH.Test.Serialiser", StringComparison.Ordinal) == true))
                    verifyMethods.AddRange(type!.GetMethods(BindingFlags.Static | BindingFlags.Public)
                        .Where(m => m.ReturnType.FullName == "BH.oM.Test.Results.TestResult" && m.GetParameters().Length == 0));
            }
            catch (Exception ex) { Console.Error.WriteLine($"Warning: could not inspect {file}: {ex.Message}"); }
        }

        var legs = new JsonArray();
        foreach (MethodInfo method in verifyMethods.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var leg = new JsonObject { ["Leg"] = method.Name };
            object? result;
            try { result = method.Invoke(null, null); }
            catch (Exception ex)
            {
                leg["Status"] = "Threw";
                leg["Error"] = (ex.InnerException ?? ex).ToString();
                legs.Add(leg);
                continue;
            }
            if (result == null) { leg["Status"] = "Null"; legs.Add(leg); continue; }

            Type rt = result.GetType();
            leg["Status"] = rt.GetProperty("Status")?.GetValue(result)?.ToString();
            leg["Summary"] = rt.GetProperty("Description")?.GetValue(result)?.ToString();
            var items = new JsonArray();
            if (rt.GetProperty("Information")?.GetValue(result) is IEnumerable info)
            {
                foreach (object item in info)
                {
                    Type it = item.GetType();
                    var nested = new JsonArray();
                    if (it.GetProperty("Information")?.GetValue(item) is IEnumerable inner)
                        foreach (object n in inner)
                            nested.Add(n.GetType().GetProperty("Message")?.GetValue(n)?.ToString());
                    items.Add(new JsonObject
                    {
                        ["Description"] = it.GetProperty("Description")?.GetValue(item)?.ToString(),
                        ["Status"] = it.GetProperty("Status")?.GetValue(item)?.ToString(),
                        ["Message"] = it.GetProperty("Message")?.GetValue(item)?.ToString(),
                        ["Events"] = nested
                    });
                }
            }
            leg["NonPassCount"] = items.Count;
            leg["Items"] = items;
            legs.Add(leg);
            Console.WriteLine($"{method.Name}: {leg["Status"]} | {leg["Summary"]} | non-pass {items.Count}");
        }
        File.WriteAllText(outPath, legs.ToJsonString(JsonOut));
        return 0;
    }

    // ---- capture: write chosen methods, check the shape, force a deserialise failure, run the regexes ----

    static int Capture(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var all = ((IEnumerable)Call("BH.Engine.Base.Query", "BHoMMethodList", Type.EmptyTypes)!)
            .Cast<MethodInfo>()
            .OrderBy(m => m.DeclaringType!.FullName, StringComparer.Ordinal)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .ThenBy(m => string.Join(",", m.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name)), StringComparer.Ordinal)
            .ToList();

        var picks = new List<(string Why, MethodBase Method)>();
        void Pick(string why, Func<MethodInfo, bool> pred)
        {
            MethodInfo? m = all.FirstOrDefault(x => pred(x) && !picks.Any(p => p.Method == x));
            if (m != null) picks.Add((why, m));
            else Console.Error.WriteLine($"No method found for: {why}");
        }

        bool Param(MethodInfo m, Func<Type, bool> pred) => m.GetParameters().Any(p => pred(p.ParameterType));

        Pick("non-generic engine method (Geometry Query.Length)", m => m.DeclaringType!.FullName == "BH.Engine.Geometry.Query" && m.Name == "Length" && !m.IsGenericMethodDefinition);
        Pick("generic method definition #1", m => m.IsGenericMethodDefinition);
        Pick("generic method definition with constrained parameter", m => m.IsGenericMethodDefinition && m.GetGenericArguments().Any(g => g.GetGenericParameterConstraints().Length > 0));
        Pick("List<> parameter", m => !m.IsGenericMethodDefinition && Param(m, t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)));
        Pick("array parameter", m => !m.IsGenericMethodDefinition && Param(m, t => t.IsArray));
        Pick("Dictionary<,> parameter", m => !m.IsGenericMethodDefinition && Param(m, t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>)));
        Pick("BH.Engine.Adapter namespace method", m => m.DeclaringType!.Namespace?.StartsWith("BH.Engine.Adapter", StringComparison.Ordinal) == true);
        Pick("File_Toolkit method", m => m.DeclaringType!.Assembly.GetName().Name is "File_Engine" or "File_Adapter");

        Type? fileAdapter = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return (IEnumerable<Type?>)a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types; } })
            .FirstOrDefault(t => t != null && t.Name == "FileAdapter" && t.Namespace?.StartsWith("BH.Adapter", StringComparison.Ordinal) == true);
        if (fileAdapter != null)
            foreach (ConstructorInfo c in fileAdapter.GetConstructors().OrderBy(c => c.GetParameters().Length))
                picks.Add(($"Adapter constructor ({fileAdapter.FullName}, {c.GetParameters().Length} params)", c));
        else
            Console.Error.WriteLine("FileAdapter type not found");

        var records = new JsonArray();
        var messages = new List<string>();
        int i = 0;
        foreach ((string why, MethodBase method) in picks)
        {
            i++;
            var rec = new JsonObject
            {
                ["Index"] = i,
                ["Why"] = why,
                ["DeclaringType"] = method.DeclaringType!.FullName,
                ["Assembly"] = method.DeclaringType.Assembly.GetName().Name,
                ["Name"] = method.Name
            };

            ClearEvents();
            string? json = null;
            try { json = ToJson(method); }
            catch (Exception ex) { rec["ToJsonError"] = (ex.InnerException ?? ex).Message; }
            rec["Json"] = json;
            rec["ToJsonEvents"] = new JsonArray(Events().Select(e => (JsonNode?)e).ToArray());

            if (json != null)
            {
                rec["Shape"] = Shape(json, method);

                ClearEvents();
                object? back = null;
                try { back = FromJson(json); }
                catch (Exception ex) { rec["RoundTripError"] = (ex.InnerException ?? ex).Message; }
                rec["RoundTripResolved"] = back is MethodBase mb && mb == method;
                rec["RoundTripResolvedTo"] = back is MethodBase mb2 ? $"{mb2.DeclaringType?.FullName}.{mb2.Name}" : back?.GetType().FullName;

                // Forced failure: same record, method name that cannot exist.
                JsonObject doc = JsonNode.Parse(json)!.AsObject();
                doc["MethodName"] = doc["MethodName"]!.GetValue<string>() + "__sertestMissing";
                ClearEvents();
                try { FromJson(doc.ToJsonString()); }
                catch (Exception ex) { rec["ForcedFailureThrew"] = (ex.InnerException ?? ex).Message; }
                List<string> events = Events();
                string? methodEvent = events.FirstOrDefault(e => e.StartsWith("Method ", StringComparison.Ordinal));
                rec["ForcedFailureEvents"] = new JsonArray(events.Select(e => (JsonNode?)e).ToArray());
                rec["ForcedFailureMessage"] = methodEvent;
                if (methodEvent != null)
                {
                    messages.Add(methodEvent);
                    Match tm = MethodEventPattern.Match(methodEvent);
                    Match am = MethodEventAssemblyPattern.Match(methodEvent);
                    rec["Regex"] = new JsonObject
                    {
                        ["TypeMatched"] = tm.Success,
                        ["Method"] = tm.Success ? tm.Groups["method"].Value.Trim() : null,
                        ["Type"] = tm.Success ? tm.Groups["type"].Value.Trim() : null,
                        ["AssemblyMatched"] = am.Success,
                        ["Assembly"] = am.Success ? am.Groups["assembly"].Value.Trim() : null
                    };
                }
            }
            records.Add(rec);
            Console.WriteLine($"[{i}] {why}: {method.DeclaringType.FullName}.{method.Name} -> {(json == null ? "NO JSON" : "ok")}");
        }

        File.WriteAllText(Path.Combine(outDir, "capture.json"), records.ToJsonString(JsonOut));
        File.WriteAllLines(Path.Combine(outDir, "forced-failure-messages.txt"), messages);
        return 0;
    }

    static JsonObject Shape(string json, MethodBase method)
    {
        var s = new JsonObject();
        JsonObject doc;
        try { doc = JsonNode.Parse(json)!.AsObject(); }
        catch (Exception ex) { s["ParseError"] = ex.Message; return s; }

        s["TopLevelKeys"] = new JsonArray(doc.Select(kv => (JsonNode?)kv.Key).ToArray());
        s["HasBhomVersion"] = doc.ContainsKey("_bhomVersion");
        s["BhomVersion"] = doc["_bhomVersion"]?.ToString();

        string? typeName = doc["TypeName"]?.GetValue<string>();
        try
        {
            JsonObject inner = JsonNode.Parse(typeName!)!.AsObject();
            string? name = inner["Name"]?.GetValue<string>();
            s["TypeNameIsNestedDocument"] = true;
            s["TypeNameKeys"] = new JsonArray(inner.Select(kv => (JsonNode?)kv.Key).ToArray());
            s["TypeNameName"] = name;
            s["TypeNameHasAssembly"] = name?.Contains(',') == true;
            s["TypeNameHasOwnBhomVersion"] = inner.ContainsKey("_bhomVersion");
        }
        catch (Exception ex)
        {
            s["TypeNameIsNestedDocument"] = false;
            s["TypeNameParseError"] = ex.Message;
        }

        JsonArray? written = doc["Parameters"] as JsonArray;
        List<string> expected = ParametersWithConstraints(method).Select(p => ToText(p.ParameterType)).ToList();
        s["ParametersWritten"] = written?.DeepClone();
        s["ParametersExpectedToText"] = new JsonArray(expected.Select(e => (JsonNode?)e).ToArray());
        s["ParametersAreJsonDocuments"] = written?.Any(p => p?.GetValue<string>().TrimStart().StartsWith('{') == true) == true;
        s["ParametersEqualToText"] = written != null
            && written.Count == expected.Count
            && written.Select(p => p!.GetValue<string>()).SequenceEqual(expected);
        return s;
    }

    // ---- read93: every legacy 9.3 method record through FromJson (information only) ----

    static int Read93(string datasetPath, string outPath)
    {
        var rows = new JsonArray();
        int total = 0, resolved = 0, parseFailed = 0;
        foreach (string line in File.ReadLines(datasetPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            total++;
            string? methodName = null, typeName = null;
            try
            {
                JsonObject doc = JsonNode.Parse(line)!.AsObject();
                methodName = doc["MethodName"]?.GetValue<string>();
                typeName = JsonNode.Parse(doc["TypeName"]!.GetValue<string>())?["Name"]?.GetValue<string>();
            }
            catch { parseFailed++; }

            ClearEvents();
            object? back = null;
            string? error = null;
            try { back = FromJson(line); }
            catch (Exception ex) { error = (ex.InnerException ?? ex).Message; }
            bool ok = back is MethodBase;
            if (ok) resolved++;
            rows.Add(new JsonObject
            {
                ["Line"] = total,
                ["Type"] = typeName,
                ["Method"] = methodName,
                ["Resolved"] = ok,
                ["ResolvedTo"] = back is MethodBase mb ? $"{mb.DeclaringType?.FullName}.{mb.Name}({mb.GetParameters().Length})" : null,
                ["Threw"] = error,
                ["FirstEvent"] = Events().FirstOrDefault()
            });
        }
        var summary = new JsonObject { ["Total"] = total, ["Resolved"] = resolved, ["ParseFailed"] = parseFailed, ["Rows"] = rows };
        File.WriteAllText(outPath, summary.ToJsonString(JsonOut));
        Console.WriteLine($"9.3 Methods.json: {resolved} of {total} resolved ({parseFailed} lines unparsable by System.Text.Json)");
        return 0;
    }
}
