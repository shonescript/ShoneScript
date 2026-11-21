// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShoneLisp.Runtime;

internal static class LispBuiltins
{
    public static void Install(LispEnvironment environment)
    {
        void Add(string name, int min, int max, Func<CallSite, LispValue[], LispValue> invoke) =>
            environment.Define(name, LispValue.FromProcedure(new Builtin(name, min, max, invoke)));
        void Predicate(string name, LispValueKind kind) => Add(name, 1, 1, (_, args) => Boolean(args[0].Kind == kind));

        Add("+", 0, int.MaxValue, (site, args) => Fold(site, args, 0, 0d, (a, b) => a + b));
        Add("*", 0, int.MaxValue, (site, args) => Fold(site, args, 0, 1d, (a, b) => a * b));
        Add("-", 1, int.MaxValue, (site, args) => args.Length == 1
            ? Number(site, -Numeric(site, args[0])) : Fold(site, args, 1, Numeric(site, args[0]), (a, b) => a - b));
        Add("/", 1, int.MaxValue, (site, args) => args.Length == 1
            ? Divide(site, args, 0, 1d) : Divide(site, args, 1, Numeric(site, args[0])));

        Add("=", 2, int.MaxValue, (site, args) => Compare(site, args, (a, b) => a == b));
        Add("<", 2, int.MaxValue, (site, args) => Compare(site, args, (a, b) => a < b));
        Add("<=", 2, int.MaxValue, (site, args) => Compare(site, args, (a, b) => a <= b));
        Add(">", 2, int.MaxValue, (site, args) => Compare(site, args, (a, b) => a > b));
        Add(">=", 2, int.MaxValue, (site, args) => Compare(site, args, (a, b) => a >= b));
        Add("not", 1, 1, (_, args) => Boolean(args[0] == LispValue.False));
        Add("eq?", 2, 2, (_, args) => Boolean(Eq(args[0], args[1])));
        Add("equal?", 2, 2, (site, args) => Boolean(Equal(site, args[0], args[1])));

        Add("cons", 2, 2, (_, args) => LispValue.Cons(args[0], args[1]));
        Add("car", 1, 1, (site, args) => Pair(site, args[0]).Car);
        Add("cdr", 1, 1, (site, args) => Pair(site, args[0]).Cdr);
        Add("list", 0, int.MaxValue, (_, args) => MakeList(args, LispValue.EmptyList));
        Add("list?", 1, 1, (site, args) => Boolean(MeasureList(site, args[0]) >= 0));
        Add("length", 1, 1, (site, args) =>
        {
            int length = MeasureList(site, args[0]);
            return length >= 0 ? LispValue.FromNumber(length) : throw site.Error("'length' expects a proper list.");
        });
        Add("append", 0, int.MaxValue, Append);
        Predicate("null?", LispValueKind.EmptyList);
        Predicate("pair?", LispValueKind.Pair);
        Predicate("number?", LispValueKind.Number);
        Predicate("string?", LispValueKind.String);
        Predicate("boolean?", LispValueKind.Boolean);
        Predicate("symbol?", LispValueKind.Symbol);
        Predicate("procedure?", LispValueKind.Procedure);

        Add("display", 1, 1, (site, args) => Output(site, args, write: false, newline: false));
        Add("write", 1, 1, (site, args) => Output(site, args, write: true, newline: false));
        Add("print", 0, int.MaxValue, (site, args) => Output(site, args, write: false, newline: true));
        Add("newline", 0, 0, (site, args) => Output(site, args, write: false, newline: true));
    }

    private static LispValue Boolean(bool value) => LispValue.FromBoolean(value);
    private static double Numeric(CallSite site, LispValue value) => value.Kind == LispValueKind.Number
        ? value.AsNumber() : throw site.Error("Expected a number.");
    private static LispValue Pair(CallSite site, LispValue value) => value.Kind == LispValueKind.Pair
        ? value : throw site.Error("Expected a pair.");
    private static LispValue Number(CallSite site, double value) => double.IsFinite(value)
        ? LispValue.FromNumber(value) : throw site.Error("Numeric result is outside the finite double range.");

    private static LispValue Fold(CallSite site, LispValue[] args, int start, double accumulator,
        Func<double, double, double> operation)
    {
        for (int i = start; i < args.Length; i++)
            accumulator = operation(accumulator, Numeric(site, args[i]));
        return Number(site, accumulator);
    }

    private static LispValue Divide(CallSite site, LispValue[] args, int start, double accumulator)
    {
        for (int i = start; i < args.Length; i++)
        {
            var divisor = Numeric(site, args[i]);
            if (divisor == 0)
                throw site.Error("Division by zero.");
            accumulator /= divisor;
        }
        return Number(site, accumulator);
    }

    private static LispValue Compare(CallSite site, LispValue[] args, Func<double, double, bool> predicate)
    {
        bool result = true;
        double previous = Numeric(site, args[0]);
        for (int i = 1; i < args.Length; i++)
        {
            double next = Numeric(site, args[i]);
            result &= predicate(previous, next);
            previous = next;
        }
        return Boolean(result);
    }

    private static LispValue MakeList(ReadOnlySpan<LispValue> args, LispValue tail)
    {
        for (int i = args.Length - 1; i >= 0; i--)
            tail = LispValue.Cons(args[i], tail);
        return tail;
    }

    private static int MeasureList(CallSite site, LispValue value)
    {
        int count = 0;
        while (value.Kind == LispValueKind.Pair)
        {
            site.Step();
            count++;
            value = value.Cdr;
        }
        return value.Kind == LispValueKind.EmptyList ? count : -1;
    }

    private static LispValue Append(CallSite site, LispValue[] args)
    {
        if (args.Length == 0)
            return LispValue.EmptyList;
        var items = new List<LispValue>();
        foreach (var arg in args.AsSpan(0, args.Length - 1))
        {
            var item = arg;
            while (item.Kind == LispValueKind.Pair)
            {
                site.Step();
                items.Add(item.Car);
                item = item.Cdr;
            }
            if (item.Kind != LispValueKind.EmptyList)
                throw site.Error("'append' expects proper lists except for its final argument.");
        }
        var tail = args[^1];
        for (int i = items.Count - 1; i >= 0; i--)
            tail = LispValue.Cons(items[i], tail);
        return tail;
    }

    private static bool Eq(LispValue left, LispValue right) => ReferenceEquals(left, right) || (left.Kind == right.Kind && (left.Kind switch
    {
        LispValueKind.Number => left.AsNumber() == right.AsNumber(),
        LispValueKind.Symbol => left.AsSymbol() == right.AsSymbol(),
        _ => false,
    }));

    private static bool Equal(CallSite site, LispValue left, LispValue right)
    {
        var pending = new Stack<(LispValue Left, LispValue Right)>();
        pending.Push((left, right));
        while (pending.TryPop(out var pair))
        {
            site.Step();
            if (Eq(pair.Left, pair.Right))
                continue;
            if (pair.Left.Kind != pair.Right.Kind)
                return false;
            switch (pair.Left.Kind)
            {
                case LispValueKind.Pair:
                    pending.Push((pair.Left.Cdr, pair.Right.Cdr));
                    pending.Push((pair.Left.Car, pair.Right.Car));
                    break;
                case LispValueKind.String when pair.Left.AsString() == pair.Right.AsString():
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    private static LispValue Output(CallSite site, LispValue[] args, bool write, bool newline)
    {
        var writer = site.Budget.Output;
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0)
                writer.Write(' ');
            writer.Write(args[i].Format(write, site.Step));
        }
        if (newline)
            writer.WriteLine();
        return LispValue.Unspecified;
    }
}
