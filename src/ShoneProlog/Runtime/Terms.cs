// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT
using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace ShoneProlog.Runtime;

public abstract record Term;
public sealed record Variable(string Name) : Term
{
    public bool Equals(Variable? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}
public sealed record AtomTerm(string Name) : Term;
public sealed record StringTerm(string Value) : Term;
public sealed record NumberTerm(double Value) : Term;
public sealed record CompoundTerm(string Name, IReadOnlyList<Term> Arguments) : Term;
public sealed record ListTerm(IReadOnlyList<Term> Items, Term? Tail = null) : Term;

internal static class TermVariables
{
    public static IEnumerable<Variable> Collect(Term term) => term switch
    {
        Variable v => [v],
        CompoundTerm c => c.Arguments.SelectMany(Collect),
        ListTerm l => l.Items.SelectMany(Collect).Concat(l.Tail is null ? [] : Collect(l.Tail)),
        _ => []
    };
}

internal sealed class Bindings
{
    private readonly Dictionary<Variable, Term> _values = [];
    private readonly List<Variable> _trail = [];
    public int Mark => _trail.Count;

    public Term Dereference(Term term)
    {
        while (true)
        {
            if (term is Variable v && _values.TryGetValue(v, out var bound)) term = bound;
            else if (term is ListTerm { Items.Count: 0, Tail: not null } list) term = list.Tail;
            else return term;
        }
    }

    public void Restore(int mark)
    {
        for (int i = _trail.Count - 1; i >= mark; i--) _values.Remove(_trail[i]);
        _trail.RemoveRange(mark, _trail.Count - mark);
    }

    public bool TryBind(Variable variable, Term term)
    {
        // This engine represents finite terms; do not introduce cyclic structures.
        if (Occurs(variable, term)) return false;
        _values.Add(variable, term);
        _trail.Add(variable);
        return true;
    }

    private bool Occurs(Variable variable, Term term)
    {
        if (term is not (CompoundTerm or ListTerm)) return ReferenceEquals(variable, term);
        var pending = new Stack<Term>();
        var visited = new HashSet<Term>(ReferenceEqualityComparer.Instance);
        pending.Push(term);
        while (pending.TryPop(out var item))
        {
            item = Dereference(item);
            if (ReferenceEquals(variable, item)) return true;
            if (!visited.Add(item)) continue;
            if (item is CompoundTerm compound)
                foreach (Term arg in compound.Arguments) pending.Push(arg);
            else if (item is ListTerm list)
            {
                foreach (Term element in list.Items) pending.Push(element);
                if (list.Tail is not null) pending.Push(list.Tail);
            }
        }
        return false;
    }
}

internal static class Unifier
{
    private static readonly ListTerm Nil = new([]);

    public static bool Unify(Term left, Term right, Bindings env)
    {
        var pending = new Stack<(Term Left, Term Right)>();
        pending.Push((left, right));
        while (pending.TryPop(out var pair))
        {
            left = env.Dereference(pair.Left);
            right = env.Dereference(pair.Right);
            if (ReferenceEquals(left, right)) continue;
            if (left is Variable lv) { if (!env.TryBind(lv, right)) return false; continue; }
            if (right is Variable rv) { if (!env.TryBind(rv, left)) return false; continue; }
            if (left is CompoundTerm lc && right is CompoundTerm rc && lc.Name == rc.Name && lc.Arguments.Count == rc.Arguments.Count)
            {
                for (int i = lc.Arguments.Count - 1; i >= 0; i--) pending.Push((lc.Arguments[i], rc.Arguments[i]));
                continue;
            }
            if (left is ListTerm ll && right is ListTerm rl)
            {
                int count = Math.Min(ll.Items.Count, rl.Items.Count);
                if (count == 0)
                {
                    if (ll.Items.Count != rl.Items.Count) return false;
                    continue;
                }
                pending.Push((Remainder(ll, count), Remainder(rl, count)));
                for (int i = count - 1; i >= 0; i--) pending.Push((ll.Items[i], rl.Items[i]));
                continue;
            }
            if (IsNil(left) && IsNil(right)) continue;
            if (left is AtomTerm or StringTerm or NumberTerm && left.Equals(right)) continue;
            return false;
        }
        return true;
    }

    internal static bool IsNil(Term term) => term is ListTerm { Items.Count: 0, Tail: null } or AtomTerm { Name: "[]" };

    private static Term Remainder(ListTerm list, int count) => count == list.Items.Count
        ? list.Tail ?? Nil
        : new ListTerm(list.Items is ItemSlice slice ? slice.Skip(count) : new ItemSlice(list.Items, count), list.Tail);

    private sealed class ItemSlice(IReadOnlyList<Term> items, int start) : IReadOnlyList<Term>
    {
        public int Count => items.Count - start;
        public Term this[int index] => items[start + index];
        public ItemSlice Skip(int count) => new(items, start + count);
        public IEnumerator<Term> GetEnumerator()
        {
            for (int i = start; i < items.Count; i++) yield return items[i];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

internal static class Arithmetic
{
    public static double Evaluate(Term term, Bindings env)
    {
        var pending = new Stack<(Term Term, bool Reduce)>();
        var values = new Stack<double>();
        pending.Push((term, false));
        while (pending.TryPop(out var item))
        {
            Term current = env.Dereference(item.Term);
            if (current is NumberTerm number) { values.Push(number.Value); continue; }
            if (current is not CompoundTerm compound)
                throw new PrologRuntimeException("Arithmetic expression is not instantiated to a number.");
            if (compound.Arguments.Count is < 1 or > 2)
                throw new PrologRuntimeException($"Invalid arithmetic expression '{compound.Name}'.");
            if (!item.Reduce)
            {
                pending.Push((compound, true));
                for (int i = compound.Arguments.Count - 1; i >= 0; i--) pending.Push((compound.Arguments[i], false));
                continue;
            }
            double right = values.Pop();
            double value;
            if (compound.Arguments.Count == 1)
                value = compound.Name switch
                {
                    "+" => right,
                    "-" => -right,
                    _ => throw new PrologRuntimeException($"Invalid arithmetic operator '{compound.Name}/1'.")
                };
            else
            {
                double left = values.Pop();
                value = compound.Name switch
                {
                    "+" => left + right,
                    "-" => left - right,
                    "*" => left * right,
                    "/" or "//" or "mod" or "rem" when right == 0 => throw new PrologRuntimeException("Division by zero."),
                    "/" => left / right,
                    "//" => Math.Truncate(left / right),
                    "mod" => left - Math.Floor(left / right) * right,
                    "rem" => left % right,
                    _ => throw new PrologRuntimeException($"Invalid arithmetic operator '{compound.Name}/2'.")
                };
            }
            if (!double.IsFinite(value)) throw new PrologRuntimeException("Arithmetic result is not finite.");
            values.Push(value);
        }
        return values.Pop();
    }
}

internal static class TermFormatter
{
    public static string Format(Term term, Bindings env)
    {
        var output = new StringBuilder();
        var pending = new Stack<(Term? Term, string? Text)>();
        pending.Push((term, null));
        while (pending.TryPop(out var item))
        {
            if (item.Term is null) { output.Append(item.Text); continue; }
            switch (env.Dereference(item.Term))
            {
                case Variable variable:
                    output.Append(variable.Name);
                    break;
                case AtomTerm atom:
                    output.Append(atom.Name == "[]" ? "[]" : FormatAtom(atom.Name));
                    break;
                case StringTerm text:
                    output.Append(Quote(text.Value, '"'));
                    break;
                case NumberTerm number:
                    output.Append(number.Value.ToString("G", CultureInfo.InvariantCulture));
                    break;
                case CompoundTerm compound:
                    output.Append(FormatAtom(compound.Name)).Append('(');
                    pending.Push((null, ")"));
                    PushItems(compound.Arguments);
                    break;
                case ListTerm list:
                    output.Append('[');
                    var elements = new List<Term>();
                    Term? tail = list;
                    while (tail is ListTerm segment)
                    {
                        elements.AddRange(segment.Items);
                        tail = segment.Tail is null ? null : env.Dereference(segment.Tail);
                    }
                    pending.Push((null, "]"));
                    if (tail is not null && !Unifier.IsNil(tail))
                    {
                        pending.Push((tail, null));
                        pending.Push((null, " | "));
                    }
                    PushItems(elements);
                    break;
            }
        }
        return output.ToString();

        void PushItems(IReadOnlyList<Term> items)
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                pending.Push((items[i], null));
                if (i > 0) pending.Push((null, ", "));
            }
        }
    }

    private static string FormatAtom(string name) => name.Length > 0 && char.IsLetter(name[0]) && !char.IsUpper(name[0]) &&
        name.All(c => char.IsLetterOrDigit(c) || c == '_') ? name : Quote(name, '\'');

    private static string Quote(string value, char delimiter)
    {
        var output = new StringBuilder().Append(delimiter);
        foreach (char c in value)
            output.Append(c == delimiter ? $"\\{delimiter}" : c switch
            {
                '\\' => "\\\\", '\a' => "\\a", '\b' => "\\b", '\f' => "\\f", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\v' => "\\v", _ => c.ToString()
            });
        return output.Append(delimiter).ToString();
    }
}
