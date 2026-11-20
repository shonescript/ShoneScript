// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ShoneJS.Test262;

internal enum Mode { Sloppy, Strict, Raw }
internal sealed record Negative(string Phase, string Type);
internal sealed record TestCase(string Path, string Source, Mode[] Modes, string[] Includes, Negative? Negative, bool IsAsync);

internal sealed class TestCatalog
{
    public const string AsyncHarness = "harness/doneprintHandle.js";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex Frontmatter = new(@"(?m)^/\*---[ \t]*\r?\n(?<body>[\s\S]*?)^---\*/[ \t]*\r?$", RegexOptions.CultureInvariant);
    private static readonly Regex Key = new(@"^(?<key>[A-Za-z][A-Za-z0-9]*):[ \t]*(?<value>.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex ListItem = new(@"^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex CasePath = new(@"^test/(?:[A-Za-z0-9_.-]+/)*[A-Za-z0-9_.-]+\.js$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string _root;
    private readonly Manifest _manifest;

    private TestCatalog(string root, Manifest manifest) => (_root, _manifest) = (root, manifest);

    public string Revision => _manifest.Revision;
    public IEnumerable<string> Paths => _manifest.Tests.Keys.Order(StringComparer.Ordinal);

    // Load only the checked-in manifest and verify every required harness file before running tests.
    public static TestCatalog Load(string baseDirectory)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(baseDirectory, "smoke.json")), JsonOptions)
            ?? throw new InvalidDataException("Missing smoke manifest.");
        if (manifest.Repository != "https://github.com/tc39/test262.git" || !Regex.IsMatch(manifest.Revision, "^[0-9a-f]{40}$") || manifest.Tests.Count == 0)
            throw new InvalidDataException("Invalid upstream pin or empty smoke manifest.");
        var catalog = new TestCatalog(Path.Combine(baseDirectory, "upstream"), manifest);
        foreach (string path in new[] { "LICENSE", "harness/assert.js", "harness/sta.js", AsyncHarness })
            _ = catalog.ReadHarness(path);
        return catalog;
    }

    public string ReadHarness(string path) => ReadPinned(path, _manifest.Harness);

    // Parse the small supported Test262 metadata subset and reject unpinned or ambiguous cases early.
    public TestCase ReadTest(string path)
    {
        if (!CasePath.IsMatch(path) || path.Split('/').Any(part => part is "." or ".."))
            throw new InvalidDataException($"Invalid Test262 case path '{path}'.");
        string source = ReadPinned(path, _manifest.Tests);
        MatchCollection matches = Frontmatter.Matches(source);
        if (matches.Count != 1)
            throw new InvalidDataException("Expected one Test262 frontmatter block.");

        string[] flags = [], includes = [], features = [];
        Negative? negative = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string[] lines = matches[0].Groups["body"].Value.ReplaceLineEndings("\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            Match entry = Key.Match(lines[i]);
            if (!entry.Success || !seen.Add(entry.Groups["key"].Value))
                throw new InvalidDataException($"Invalid or duplicate frontmatter entry '{lines[i]}'.");
            string key = entry.Groups["key"].Value;
            string value = entry.Groups["value"].Value.Trim();
            var continuation = new List<string>();
            while (i + 1 < lines.Length && (string.IsNullOrWhiteSpace(lines[i + 1]) || char.IsWhiteSpace(lines[i + 1][0])))
            {
                i++;
                if (!string.IsNullOrWhiteSpace(lines[i])) continuation.Add(lines[i]);
            }

            switch (key)
            {
                case "flags": flags = ParseList(key, value, continuation); break;
                case "includes": includes = ParseList(key, value, continuation); break;
                case "features": features = ParseList(key, value, continuation); break;
                case "negative": negative = ParseNegative(value, continuation); break;
                case "description": case "info": case "esid": case "es5id": case "es6id": case "author": break;
                default: throw new NotSupportedException($"Unsupported Test262 metadata '{key}'.");
            }
        }

        foreach (string flag in flags)
            if (flag is not ("onlyStrict" or "noStrict" or "raw" or "async"))
                throw new NotSupportedException($"Unsupported Test262 flag '{flag}'.");
        bool isAsync = flags.Contains("async");
        string[] modeFlags = flags.Where(flag => flag != "async").ToArray();
        if (modeFlags.Length > 1 || (modeFlags.Contains("raw") && (includes.Length > 0 || isAsync)))
            throw new InvalidDataException("Incompatible Test262 flags or includes.");
        if (isAsync && negative is not null)
            throw new NotSupportedException("Async negative Test262 cases are unsupported.");
        foreach (string feature in features)
            if (feature is not ("hashbang" or "optional-chaining" or "coalesce-expression" or "BigInt" or "Promise.allSettled" or "Symbol.iterator" or "Symbol" or "array-find-from-last" or "change-array-by-copy"))
                throw new NotSupportedException($"Unsupported Test262 feature '{feature}'.");
        foreach (string include in includes)
            if (!include.EndsWith(".js", StringComparison.Ordinal) || include is "assert.js" or "sta.js" or "doneprintHandle.js" || !_manifest.Harness.ContainsKey("harness/" + include))
                throw new NotSupportedException($"Unpinned or unsupported Test262 include '{include}'.");

        Mode[] modes = modeFlags.SingleOrDefault() switch
        {
            "raw" => [Mode.Raw],
            "onlyStrict" => [Mode.Strict],
            "noStrict" => [Mode.Sloppy],
            _ => [Mode.Sloppy, Mode.Strict]
        };
        return new TestCase(path, source, modes, includes, negative, isAsync);
    }

    private static string[] ParseList(string key, string value, List<string> continuation)
    {
        if (continuation.Count > 0 || !value.StartsWith('[') || !value.EndsWith(']'))
            throw new NotSupportedException($"Expected an inline list for Test262 '{key}'.");
        string content = value[1..^1].Trim();
        if (content.Length == 0) return [];
        string[] items = content.Split(',', StringSplitOptions.TrimEntries);
        if (items.Any(item => !ListItem.IsMatch(item)) || items.Distinct(StringComparer.Ordinal).Count() != items.Length)
            throw new InvalidDataException($"Invalid Test262 '{key}' list.");
        return items;
    }

    private static Negative ParseNegative(string value, List<string> continuation)
    {
        string? phase = null, type = null;
        if (value.Length != 0 || continuation.Count != 2)
            throw new InvalidDataException("Expected two fields in Test262 negative metadata.");
        foreach (string line in continuation)
        {
            Match field = Key.Match(line.TrimStart());
            if (field.Success && field.Groups["key"].Value == "phase" && phase is null) phase = field.Groups["value"].Value.Trim();
            else if (field.Success && field.Groups["key"].Value == "type" && type is null) type = field.Groups["value"].Value.Trim();
            else throw new InvalidDataException($"Invalid Test262 negative metadata field '{line.Trim()}'.");
        }
        if (phase is not ("parse" or "runtime"))
            throw new NotSupportedException($"Unsupported Test262 negative phase '{phase}'.");
        if (type is not ("SyntaxError" or "ReferenceError" or "TypeError" or "RangeError" or "EvalError" or "URIError" or "Error") || (phase == "parse" && type != "SyntaxError"))
            throw new NotSupportedException($"Unsupported Test262 negative error '{type}' for phase '{phase}'.");
        return new Negative(phase, type);
    }

    // Manifest hashes make the harness and test inputs reproducible rather than trusting mutable files.
    private string ReadPinned(string path, Dictionary<string, string> hashes)
    {
        if (!hashes.TryGetValue(path, out string? expected))
            throw new InvalidDataException($"Unpinned Test262 file '{path}'.");
        byte[] data = File.ReadAllBytes(Path.Combine(_root, path));
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(data)), expected, StringComparison.Ordinal))
            throw new InvalidDataException($"SHA-256 mismatch for '{path}'.");
        return Utf8.GetString(data);
    }

    private sealed record Manifest(
        [property: JsonPropertyName("repository"), JsonRequired] string Repository,
        [property: JsonPropertyName("revision"), JsonRequired] string Revision,
        [property: JsonPropertyName("harness"), JsonRequired] Dictionary<string, string> Harness,
        [property: JsonPropertyName("tests"), JsonRequired] Dictionary<string, string> Tests);
}
