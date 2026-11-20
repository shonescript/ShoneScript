// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using Sdcb.Arithmetic;
using ShonePy.Parsing;

namespace ShonePy.Runtime;

public sealed partial class PyInterpreter
{
    private void ExecuteMatch(MatchStatement statement)
    {
        var subject = Evaluate(statement.Subject);
        foreach (var clause in statement.Cases)
        {
            var captures = new Dictionary<string, PyValue>(StringComparer.Ordinal);
            if (!MatchPattern(clause.Pattern, subject, captures)) continue;

            // Bindings from a successful pattern are visible to its guard and survive a false guard.
            foreach (var (name, value) in captures) _scope.Assign(name, value);
            if (clause.Guard is not null && !Evaluate(clause.Guard).IsTruthy()) continue;
            ExecuteBlock(clause.Body);
            return;
        }
    }

    private bool MatchPattern(PatternSyntax pattern, PyValue subject, Dictionary<string, PyValue> captures)
    {
        Tick(pattern);
        switch (pattern)
        {
            case CapturePattern capture:
                if (capture.Name is not null) captures.Add(capture.Name, subject);
                return true;
            case ValuePattern value:
                var expected = Evaluate(value.Value);
                return value.IsSingleton ? PyOperations.Identity(subject, expected) : PyOperations.Equal(subject, expected);
            case SequencePattern sequence:
                return MatchSequence(sequence, subject, captures);
            case MappingPattern mapping:
                return MatchMapping(mapping, subject, captures);
            case ClassPattern cls:
                return MatchClass(cls, subject, captures);
            case AsPattern alias:
                if (!MatchPattern(alias.Pattern, subject, captures)) return false;
                captures.Add(alias.Name, subject);
                return true;
            case OrPattern either:
                foreach (var alternative in either.Alternatives)
                {
                    // A failed alternative never contributes bindings to the next one.
                    var branch = new Dictionary<string, PyValue>(StringComparer.Ordinal);
                    if (!MatchPattern(alternative, subject, branch)) continue;
                    foreach (var (name, value) in branch) captures.Add(name, value);
                    return true;
                }
                return false;
            default:
                throw Error("NotImplementedError", $"unsupported pattern {pattern.GetType().Name}", pattern);
        }
    }

    private bool MatchSequence(SequencePattern pattern, PyValue subject, Dictionary<string, PyValue> captures)
    {
        if (subject.Type is not (PyType.List or PyType.Tuple or PyType.Range)) return false;
        if (pattern.Items is [StarPattern { Name: null }]) return true;

        var length = subject.Data switch
        {
            PyList list => new BigInt(list.Count),
            PyTuple tuple => new BigInt(tuple.Count),
            PyRange range => range.Length,
            _ => throw new InvalidOperationException("Expected a sequence")
        };
        if (length > new BigInt(long.MaxValue))
            throw Error("OverflowError", "Python int too large to convert to C ssize_t", pattern);

        int star = pattern.Items.FindIndex(item => item is StarPattern);
        var fixedCount = new BigInt(pattern.Items.Count - (star < 0 ? 0 : 1));
        if (star < 0 ? length != fixedCount : length < fixedCount) return false;
        BigInt middleCount = length - fixedCount;
        for (int i = 0; i < pattern.Items.Count; i++)
        {
            var item = pattern.Items[i];
            if (item is StarPattern rest)
            {
                if (rest.Name is null) continue;
                var tail = new PyList();
                var end = new BigInt(i) + middleCount;
                for (var j = new BigInt(i); j < end; j += BigInt.One)
                {
                    Tick(rest);
                    tail.Add(PyOperations.GetItem(subject, PyValue.From(j)));
                }
                captures.Add(rest.Name, PyValue.From(tail));
            }
            else if (item is not CapturePattern { Name: null })
            {
                var index = new BigInt(i) + (star >= 0 && i > star ? middleCount - BigInt.One : BigInt.Zero);
                if (!MatchPattern(item, PyOperations.GetItem(subject, PyValue.From(index)), captures)) return false;
            }
        }
        return true;
    }

    private bool MatchClass(ClassPattern pattern, PyValue subject, Dictionary<string, PyValue> captures)
    {
        var candidate = Evaluate(pattern.Class);
        if (candidate.Data is not PyClass cls)
            throw Error("TypeError", "called match pattern must be a type", pattern);
        if (subject.Data is not PyInstance instance || !instance.Class.Mro.Contains(cls)) return false;

        IReadOnlyList<string> matchArgs = [];
        try
        {
            PyValue value = cls.GetAttribute("__match_args__");
            if (value.Data is not PyTuple tuple)
                throw Error("TypeError", $"{cls.Name}.__match_args__ must be a tuple (got {PyOperations.TypeName(value)})", pattern);
            var names = new List<string>(tuple.Count);
            foreach (PyValue item in tuple)
            {
                if (item.Type != PyType.String)
                    throw Error("TypeError", $"__match_args__ elements must be strings (got {PyOperations.TypeName(item)})", pattern);
                names.Add((string)item.Data!);
            }
            matchArgs = names;
        }
        catch (PythonException error) when (error.ErrorName == "AttributeError") { }

        if (pattern.Positional.Count > matchArgs.Count)
            throw Error("TypeError", $"{cls.Name}() accepts {matchArgs.Count} positional sub-patterns ({pattern.Positional.Count} given)", pattern);
        var attributes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, _) in pattern.Keywords)
            if (!attributes.Add(name)) throw Error("TypeError", $"{cls.Name}() got multiple sub-patterns for attribute '{name}'", pattern);
        for (int i = 0; i < pattern.Positional.Count; i++)
        {
            string name = matchArgs[i];
            if (!attributes.Add(name))
                throw Error("TypeError", $"{cls.Name}() got multiple sub-patterns for attribute '{name}'", pattern);
            if (!MatchAttribute(name, pattern.Positional[i])) return false;
        }
        foreach (var (name, subpattern) in pattern.Keywords)
            if (!MatchAttribute(name, subpattern)) return false;
        return true;

        bool MatchAttribute(string name, PatternSyntax subpattern)
        {
            PyValue attribute;
            try { attribute = instance.GetAttribute(name); }
            catch (PythonException error) when (error.ErrorName == "AttributeError") { return false; }
            return MatchPattern(subpattern, attribute, captures);
        }
    }

    private bool MatchMapping(MappingPattern pattern, PyValue subject, Dictionary<string, PyValue> captures)
    {
        if (subject.Data is not PyDict mapping || mapping.Count < pattern.Items.Count) return false;

        var keys = pattern.Items.Select(item => Evaluate(item.Key)).ToArray();
        var used = new HashSet<PyValue>();
        var values = new PyValue[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            if (!used.Add(key)) throw Error("ValueError", $"mapping pattern checks duplicate key ({key.Repr()})", pattern);
            // A missing key must fail the pattern, even for dict types with a missing-value fallback.
            if (!mapping.TryGetValue(key, out values[i])) return false;
        }
        for (int i = 0; i < values.Length; i++)
            if (!MatchPattern(pattern.Items[i].Pattern, values[i], captures)) return false;

        if (pattern.Rest is { } rest)
        {
            var remaining = new PyDict();
            foreach (var (key, value) in mapping)
            {
                Tick(pattern);
                if (!used.Contains(key)) remaining[key] = value;
            }
            captures.Add(rest, PyValue.From(remaining));
        }
        return true;
    }
}
