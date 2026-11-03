using System.Text;
using PiSharp.Contracts.Schema;

// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/schema/test/conformance.test.ts, packages/schema/test/suite.ts
//
// The official JSON-Schema-Test-Suite, drafts 2020-12 and 7: every test, including optional and format tests, run as pi-schema's
// conformance test runs it. Each test checks the verdict and that the errors agree with the check. A test listed in KnownFailures
// must fail; when one starts passing, it leaves the list.
internal static partial class Program
{
    // pi-schema's KNOWN_FAILURES, unchanged.
    private static readonly HashSet<string> KnownFailures = new(StringComparer.Ordinal)
    {
        // Vocabularies switched off by a custom meta-schema are not supported.
        "draft2020-12/vocabulary > schema that uses custom metaschema with with no validation vocabulary > no validation: invalid number, but it still validates",
        "draft2020-12/optional/format-assertion > schema that uses custom metaschema with format-assertion: false > format-assertion: false: invalid string",
        "draft2020-12/optional/format-assertion > schema that uses custom metaschema with format-assertion: true > format-assertion: true: invalid string",
        // A $ref to a document of another draft is evaluated with the compile's draft.
        "draft2020-12/optional/cross-draft > refs to historic drafts are processed as historic drafts > first item not a string is valid",
        "draft7/optional/cross-draft > refs to future drafts are processed as future drafts > missing bar is invalid",
        // contentMediaType and contentEncoding are annotations.
        "draft7/optional/content > validation of string-encoded content based on media type > an invalid JSON document",
        "draft7/optional/content > validation of binary string-encoding > an invalid base64 string (% is not a valid character)",
        "draft7/optional/content > validation of binary-encoded media type documents > a validly-encoded invalid JSON document",
        "draft7/optional/content > validation of binary-encoded media type documents > an invalid base64 string that is valid JSON",
    };

    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");

    private static void Conformance(string directory, bool draft7)
    {
        var schemas = SuiteSchemas();
        var tests = 0;
        var problems = new List<string>();
        foreach (var path in JsonFiles(Path.Combine(Fixtures, "json-schema-test-suite", "tests", directory)))
        {
            var name = Path.GetRelativePath(Path.Combine(Fixtures, "json-schema-test-suite", "tests"), path).Replace('\\', '/')[..^".json".Length];
            // Format tests assert formats; everywhere else `format` is an annotation, as draft 2020-12 specifies.
            var options = new SchemaOptions { Draft7 = draft7, Schemas = schemas, Formats = name.Contains("/optional/format/", StringComparison.Ordinal) ? SchemaFormats.All : null };
            foreach (var group in (JsArray)Js.Parse(File.ReadAllText(path, Encoding.UTF8))!)
            {
                var g = (JsObject)group!;
                var description = (string)g.GetOwn("description")!;
                foreach (var item in (JsArray)g.GetOwn("tests")!)
                {
                    var test = (JsObject)item!;
                    var id = $"{name} > {description} > {(string)test.GetOwn("description")!}";
                    var data = test.GetOwn("data");
                    var valid = (bool)test.GetOwn("valid")!;
                    var known = KnownFailures.Contains(id);
                    tests++;
                    string? verdict;
                    try
                    {
                        var validator = SchemaValidator.Compile(g.GetOwn("schema"), options);
                        var check = validator.Check(data);
                        verdict = check == valid ? null : $"check is {check}";
                        var errors = validator.Errors(data);
                        // (errors agree with check): never a known failure.
                        if ((errors.Count == 0) != check) problems.Add($"{id} (errors agree with check): {errors.Count} errors, check {check}");
                    }
                    catch (Exception error)
                    {
                        verdict = error.GetType().Name + ": " + error.Message;
                        problems.Add($"{id} (errors agree with check): {verdict}");
                    }

                    if (known && verdict is null) problems.Add($"{id}: listed as a known failure but passes");
                    if (!known && verdict is not null) problems.Add($"{id}: {verdict}");
                }
            }
        }

        Check(tests > 1000, $"{directory}: only {tests} tests found under {Fixtures}");
        Check(problems.Count == 0, $"{directory}: {problems.Count} of {tests} tests fail:\n" + string.Join("\n", problems.Take(60)));
    }

    // suite.ts suiteSchemas(): the remote documents and meta-schemas the tests reference, by URI.
    private static List<KeyValuePair<string, object?>> SuiteSchemas()
    {
        var schemas = new List<KeyValuePair<string, object?>>();
        var remotes = Path.Combine(Fixtures, "json-schema-test-suite", "remotes");
        foreach (var path in JsonFiles(remotes))
            schemas.Add(new($"http://localhost:1234/{Path.GetRelativePath(remotes, path).Replace('\\', '/')}", ReadJson(path)));
        var meta = Path.Combine(Fixtures, "meta-schemas");
        schemas.Add(new("http://json-schema.org/draft-07/schema", ReadJson(Path.Combine(meta, "draft-07-schema.json"))));
        schemas.Add(new("https://json-schema.org/draft/2020-12/schema", ReadJson(Path.Combine(meta, "draft2020-12-schema.json"))));
        foreach (var vocabulary in new[] { "core", "applicator", "unevaluated", "validation", "meta-data", "format-annotation", "content" })
            schemas.Add(new($"https://json-schema.org/draft/2020-12/meta/{vocabulary}", ReadJson(Path.Combine(meta, $"draft2020-12-meta-{vocabulary}.json"))));
        return schemas;
    }

    private static object? ReadJson(string path) => Js.Parse(File.ReadAllText(path, Encoding.UTF8));

    // readdirSync(dir).sort(): UTF-16 code unit order, directories walked in place.
    private static List<string> JsonFiles(string directory)
    {
        var output = new List<string>();
        foreach (var entry in Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal))
        {
            var path = Path.Combine(directory, entry!);
            if (Directory.Exists(path)) output.AddRange(JsonFiles(path));
            else if (entry!.EndsWith(".json", StringComparison.Ordinal)) output.Add(path);
        }

        return output;
    }
}
