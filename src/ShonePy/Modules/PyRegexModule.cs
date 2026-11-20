// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;

namespace ShonePy.Modules;

/// <summary>Managed, bounded subset of Python's re module.</summary>
public static class PyRegexModule
{
    private const int TimeoutMilliseconds = 100;
    private const int MaximumInputLength = 1_000_000;

    public static PyDict CreateModule()
    {
        var module = new PyDict();
        Add("compile", 1, a => Compile(Text(a[0]), a.Count > 1 ? Flags(a[1]) : RegexOptions.None));
        Add("match", 2, a => Match(Text(a[0]), Text(a[1]), a.Count > 2 ? Flags(a[2]) : RegexOptions.None, anchored: true));
        Add("search", 2, a => Match(Text(a[0]), Text(a[1]), a.Count > 2 ? Flags(a[2]) : RegexOptions.None));
        Add("fullmatch", 2, a => Match(Text(a[0]), Text(a[1]), a.Count > 2 ? Flags(a[2]) : RegexOptions.None, full: true));
        Add("findall", 2, a => FindAll(Text(a[0]), Text(a[1]), a.Count > 2 ? Flags(a[2]) : RegexOptions.None));
        Add("sub", 3, a => Substitute(Text(a[0]), Text(a[2]), Text(a[1]), a.Count > 3 ? Index(a[3]) : 0));
        module[PyValue.From("IGNORECASE")] = PyValue.From((int)RegexOptions.IgnoreCase);
        module[PyValue.From("MULTILINE")] = PyValue.From((int)RegexOptions.Multiline);
        module[PyValue.From("DOTALL")] = PyValue.From((int)RegexOptions.Singleline);
        module[PyValue.From("ASCII")] = PyValue.From(0);
        return module;

        void Add(string name, int minimum, Func<IReadOnlyList<PyValue>, PyValue> invoke) =>
            module[PyValue.From(name)] = PyValue.From(new PyCallable(args =>
            {
                if (args.Count < minimum || args.Count > minimum + 1)
                    throw new PythonException("TypeError", $"{name}() takes {minimum} or {minimum + 1} arguments ({args.Count} given)");
                try { return invoke(args); }
                catch (RegexMatchTimeoutException) { throw new PythonException("TimeoutError", "regular expression evaluation exceeded the time limit"); }
                catch (ArgumentException ex) { throw new PythonException("re.error", ex.Message); }
            }, name));
    }

    private static PyValue Compile(string pattern, RegexOptions options)
    {
        var regex = Create(pattern, options);
        var result = new PyDict();
        result[PyValue.From("pattern")] = PyValue.From(pattern);
        result[PyValue.From("groups")] = PyValue.From(regex.GetGroupNumbers().Length - 1);
        result[PyValue.From("match")] = Compiled("match", a => Match(pattern, Text(a[0]), options, anchored: true));
        result[PyValue.From("search")] = Compiled("search", a => Match(pattern, Text(a[0]), options));
        result[PyValue.From("fullmatch")] = Compiled("fullmatch", a => Match(pattern, Text(a[0]), options, full: true));
        result[PyValue.From("findall")] = Compiled("findall", a => FindAll(pattern, Text(a[0]), options));
        return PyValue.From(result);

        PyValue Compiled(string name, Func<IReadOnlyList<PyValue>, PyValue> call) => PyValue.From(new PyCallable(a =>
        {
            Arity(a, 1, name);
            try { return call(a); }
            catch (RegexMatchTimeoutException) { throw new PythonException("TimeoutError", "regular expression evaluation exceeded the time limit"); }
            catch (ArgumentException ex) { throw new PythonException("re.error", ex.Message); }
        }, name));
    }

    private static Regex Create(string pattern, RegexOptions options)
    {
        if (pattern.Length > 4096) throw new PythonException("re.error", "pattern is too large");
        return new Regex(NormalizePattern(pattern), options | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(TimeoutMilliseconds));
    }

    private static PyValue Match(string pattern, string input, RegexOptions options, bool anchored = false, bool full = false)
    {
        CheckInput(input);
        var regex = Create(pattern, options);
        if (full)
        {
            var fullRegex = Create(@"\A(?:" + pattern + @")\z", options);
            var fullMatch = fullRegex.Match(input);
            return fullMatch.Success ? Result(fullMatch, input, regex) : PyValue.None;
        }
        var match = anchored ? Create(@"\A(?:" + pattern + ")", options).Match(input) : regex.Match(input);
        if (!match.Success) return PyValue.None;
        return Result(match, input, regex);
    }

    private static PyValue Result(Match match, string input, Regex regex)
    {
        if (!match.Success) return PyValue.None;
        var result = new PyDict();
        result[PyValue.From("string")] = PyValue.From(input);
        result[PyValue.From("re")] = PyValue.From(regex.ToString());
        result[PyValue.From("pos")] = PyValue.From(0);
        result[PyValue.From("endpos")] = PyValue.From(input.Length);
        result[PyValue.From("lastindex")] = PyValue.None;
        result[PyValue.From("lastgroup")] = PyValue.None;
        result[PyValue.From("regs")] = PyValue.From(new PyTuple(Enumerable.Range(0, match.Groups.Count).Select(i =>
        {
            var g = match.Groups[i]; return PyValue.From(new PyTuple(PyValue.From(g.Success ? g.Index : -1), PyValue.From(g.Success ? g.Index + g.Length : -1)));
        })));
        result[PyValue.From("group")] = PyValue.From(new PyCallable(args =>
        {
            if (args.Count == 0) return PyValue.From(match.Value);
            if (args.Count == 1) return Group(args[0]);
            return PyValue.From(new PyTuple(args.Select(Group)));
        }, "group"));
        result[PyValue.From("groups")] = PyValue.From(new PyCallable(args =>
        {
            PyValue fallback = args.Count == 0 ? PyValue.None : args[0];
            return PyValue.From(new PyTuple(Enumerable.Range(1, match.Groups.Count - 1).Select(i => match.Groups[i].Success ? PyValue.From(match.Groups[i].Value) : fallback)));
        }, "groups"));
        result[PyValue.From("groupdict")] = PyValue.From(new PyCallable(args =>
        {
            var d = new PyDict();
            foreach (string name in regex.GetGroupNames().Where(n => !int.TryParse(n, out _)))
            { var g = match.Groups[name]; d[PyValue.From(name)] = g.Success ? PyValue.From(g.Value) : args.Count == 0 ? PyValue.None : args[0]; }
            return PyValue.From(d);
        }, "groupdict"));
        result[PyValue.From("start")] = PyValue.From(new PyCallable(args => PyValue.From(Span(args).Index), "start"));
        result[PyValue.From("end")] = PyValue.From(new PyCallable(args => { var g = Span(args); return PyValue.From(g.Success ? g.Index + g.Length : -1); }, "end"));
        result[PyValue.From("span")] = PyValue.From(new PyCallable(args => { var g = Span(args); return PyValue.From(new PyTuple(PyValue.From(g.Success ? g.Index : -1), PyValue.From(g.Success ? g.Index + g.Length : -1))); }, "span"));
        return PyValue.From(result);

        PyValue Group(PyValue key) { var g = GroupFor(key); return g.Success ? PyValue.From(g.Value) : PyValue.None; }
        Group GroupFor(PyValue key)
        {
            if (key.Type == PyType.String) return match.Groups[(string)key.Data!];
            int index = Index(key);
            if (index < 0 || index >= match.Groups.Count) throw new PythonException("IndexError", "no such group");
            return match.Groups[index];
        }
        Group Span(IReadOnlyList<PyValue> args) => GroupFor(args.Count == 0 ? PyValue.From(0) : args[0]);
    }

    private static PyValue FindAll(string pattern, string input, RegexOptions options)
    {
        CheckInput(input);
        var regex = Create(pattern, options);
        var values = new List<PyValue>();
        foreach (Match match in regex.Matches(input))
            values.Add(match.Groups.Count == 1 ? PyValue.From(match.Value) : match.Groups.Count == 2
                ? (match.Groups[1].Success ? PyValue.From(match.Groups[1].Value) : PyValue.From(""))
                : PyValue.From(new PyTuple(Enumerable.Range(1, match.Groups.Count - 1).Select(i => PyValue.From(match.Groups[i].Success ? match.Groups[i].Value : "")))));
        return PyValue.From(new PyList(values));
    }

    private static PyValue Substitute(string pattern, string input, string replacement, int count)
    {
        CheckInput(input);
        var regex = Create(pattern, RegexOptions.None);
        int remaining = count <= 0 ? int.MaxValue : count;
        return PyValue.From(regex.Replace(input, m =>
        {
            if (remaining-- <= 0) return m.Value;
            return Regex.Replace(replacement, @"\\([0-9]+)|\\g<([^>]+)>|\\\\", token =>
            {
                if (token.Value == "\\\\") return "\\";
                string key = token.Groups[1].Success ? token.Groups[1].Value : token.Groups[2].Value;
                if (int.TryParse(key, out int i) && i < m.Groups.Count) return m.Groups[i].Value;
                return m.Groups[key].Value;
            });
        }, count <= 0 ? int.MaxValue : count));
    }

    private static string NormalizePattern(string pattern) => Regex.Replace(pattern, @"\(\?P<([A-Za-z_]\w*)>", "(?<$1>");

    private static RegexOptions Flags(PyValue value) => (RegexOptions)Index(value) & (RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline);
    private static int Index(PyValue value) => value.Type == PyType.Integer ? (int)(Sdcb.Arithmetic.BigInt)value.Data! : throw new PythonException("TypeError", "an integer is required");
    private static string Text(PyValue value) => value.Type == PyType.String ? (string)value.Data! : throw new PythonException("TypeError", "expected string or bytes-like object");
    private static void CheckInput(string value) { if (value.Length > MaximumInputLength) throw new PythonException("OverflowError", "regular expression input is too large"); }
    private static void Arity(IReadOnlyList<PyValue> a, int n, string name) { if (a.Count != n) throw new PythonException("TypeError", $"{name}() takes exactly {n} argument(s) ({a.Count} given)"); }
}
