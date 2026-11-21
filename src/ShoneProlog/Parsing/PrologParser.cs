// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text;
using ShoneJs.Parsing;
using ShoneProlog.Runtime;

namespace ShoneProlog.Parsing;

public abstract record PrologGoal { internal abstract IEnumerable<Variable> Variables { get; } }
public sealed record CallGoal(string Name, IReadOnlyList<Term> Arguments) : PrologGoal { internal override IEnumerable<Variable> Variables => Arguments.SelectMany(TermVariables.Collect); }
public sealed record UnificationGoal(Term Left, Term Right) : PrologGoal { internal override IEnumerable<Variable> Variables => TermVariables.Collect(Left).Concat(TermVariables.Collect(Right)); }
public sealed record NegationGoal(PrologGoal Goal) : PrologGoal { internal override IEnumerable<Variable> Variables => Goal.Variables; }
public sealed record ComparisonGoal(Term Left, string Operator, Term Right) : PrologGoal { internal override IEnumerable<Variable> Variables => TermVariables.Collect(Left).Concat(TermVariables.Collect(Right)); }
internal sealed record CutGoal : PrologGoal { internal override IEnumerable<Variable> Variables => []; }
public sealed record PrologClause(CallGoal Head, IReadOnlyList<PrologGoal> Body)
{
    internal PrologClause Fresh()
    {
        var variables = new Dictionary<Variable, Variable>();
        Term FreshTerm(Term term) => term switch
        {
            Variable v => variables.TryGetValue(v, out var fresh) ? fresh : variables[v] = new(v.Name),
            CompoundTerm c => new CompoundTerm(c.Name, c.Arguments.Select(FreshTerm).ToArray()),
            ListTerm l => new ListTerm(l.Items.Select(FreshTerm).ToArray(), l.Tail is null ? null : FreshTerm(l.Tail)),
            _ => term
        };
        PrologGoal FreshGoal(PrologGoal goal) => goal switch
        {
            CallGoal c => new CallGoal(c.Name, c.Arguments.Select(FreshTerm).ToArray()),
            UnificationGoal u => new UnificationGoal(FreshTerm(u.Left), FreshTerm(u.Right)),
            ComparisonGoal c => new ComparisonGoal(FreshTerm(c.Left), c.Operator, FreshTerm(c.Right)),
            NegationGoal n => new NegationGoal(FreshGoal(n.Goal)),
            _ => goal
        };
        return new((CallGoal)FreshGoal(Head), Body.Select(FreshGoal).ToArray());
    }
}

internal sealed class PrologParser(string source, string? fileName, int maximumDepth)
{
    private enum Kind { End, Symbol, Atom, QuotedAtom, Variable, Number, String }
    private readonly record struct Token(Kind Kind, string Text, SourceSpan Span);
    private readonly record struct ParsedTerm(Term Value, int Height = 0);

    private readonly List<Token> _tokens = Lex(source, fileName);
    private int _index;
    private Token Current => _tokens[_index];

    public PrologProgram ParseProgram() => new(ParseClauses(stopAtQuery: false), fileName);

    public PrologScript ParseScript()
    {
        IReadOnlyList<PrologClause> clauses = _tokens.Any(t => t.Kind == Kind.Symbol && t.Text == "?-")
            ? ParseClauses(stopAtQuery: true)
            : [];
        return new(new(clauses, fileName), ParseQuery(), fileName);
    }

    public PrologQuery ParseQuery()
    {
        Take("?-");
        var variables = NewVariableScope();
        var goals = ParseGoals(variables);
        Take(".");
        if (Current.Kind != Kind.End) throw Error($"Unexpected token '{Current.Text}'.");
        return new(goals, variables.Keys.ToArray(), fileName);
    }

    private List<PrologClause> ParseClauses(bool stopAtQuery)
    {
        var clauses = new List<PrologClause>();
        while (Current.Kind != Kind.End && !(stopAtQuery && Is("?-")))
        {
            var variables = NewVariableScope();
            Token start = Current;
            CallGoal head = AsCall(ParseTerm(variables, 0).Value) ?? throw Error("Expected a predicate head.", start);
            IReadOnlyList<PrologGoal> body = Take(":-") ? ParseGoals(variables) : [];
            Expect(".");
            clauses.Add(new(head, body));
        }
        return clauses;
    }

    private static Dictionary<string, Variable> NewVariableScope() => new(StringComparer.Ordinal);

    private IReadOnlyList<PrologGoal> ParseGoals(Dictionary<string, Variable> variables)
    {
        var goals = new List<PrologGoal>();
        do { goals.Add(ParseGoal(variables, 0)); } while (Take(","));
        return goals;
    }

    private PrologGoal ParseGoal(Dictionary<string, Variable> variables, int depth)
    {
        CheckDepth(depth);
        if (Take("\\+")) return new NegationGoal(ParseGoal(variables, depth + 1));
        if (Take("!")) return new CutGoal();
        Token start = Current;
        Term left = ParseTerm(variables, depth).Value;
        if (Take("=")) return new UnificationGoal(left, ParseTerm(variables, depth).Value);
        if (Current.Kind == Kind.Atom && Current.Text == "is" ||
            Current.Kind == Kind.Symbol && Current.Text is "=:=" or "=\\=" or "<" or ">" or "=<" or ">=")
        {
            string op = Next().Text;
            return new ComparisonGoal(left, op, ParseTerm(variables, depth).Value);
        }
        return AsCall(left) ?? throw Error("Expected a callable goal.", start);
    }

    private static CallGoal? AsCall(Term term) => term switch
    {
        CompoundTerm c => new(c.Name, c.Arguments),
        AtomTerm a => new(a.Name, []),
        _ => null
    };

    private ParsedTerm ParseTerm(Dictionary<string, Variable> variables, int depth, int minPrecedence = 0)
    {
        CheckDepth(depth);
        ParsedTerm left = ParsePrimary(variables, depth);
        while (BinaryPrecedence(Current) is int precedence && precedence >= minPrecedence)
        {
            string op = Next().Text;
            var right = ParseTerm(variables, depth + 1, precedence + 1);
            left = new(new CompoundTerm(op, [left.Value, right.Value]), Math.Max(left.Height, right.Height) + 1);
            CheckDepth(depth + left.Height);
        }
        return left;
    }

    private static int? BinaryPrecedence(Token token) => token.Kind switch
    {
        Kind.Symbol when token.Text is "+" or "-" => 1,
        Kind.Symbol when token.Text is "*" or "/" or "//" => 2,
        Kind.Atom when token.Text is "mod" or "rem" => 2,
        _ => null
    };

    private ParsedTerm ParsePrimary(Dictionary<string, Variable> variables, int depth)
    {
        Token token = Next();
        if (token.Kind == Kind.Symbol)
        {
            if (token.Text is "+" or "-")
            {
                var operand = ParseTerm(variables, depth + 1, 3);
                return operand.Value is NumberTerm number
                    ? new(new NumberTerm(token.Text == "-" ? -number.Value : number.Value))
                    : new(new CompoundTerm(token.Text, [operand.Value]), operand.Height + 1);
            }
            if (token.Text == "[")
            {
                var items = new List<Term>();
                Term? tail = null;
                int height = 0;
                if (!Take("]"))
                {
                    do
                    {
                        var item = ParseTerm(variables, depth + 1);
                        items.Add(item.Value);
                        height = Math.Max(height, item.Height + 1);
                    } while (Take(","));
                    if (Take("|"))
                    {
                        var last = ParseTerm(variables, depth + 1);
                        tail = last.Value;
                        height = Math.Max(height, last.Height + 1);
                    }
                    Expect("]");
                }
                return new(new ListTerm(items, tail), height);
            }
            if (token.Text == "(")
            {
                var inner = ParseTerm(variables, depth + 1);
                Expect(")");
                return inner;
            }
        }
        if (token.Kind == Kind.Variable)
            return new(token.Text == "_" ? new Variable("_") : variables.TryGetValue(token.Text, out var variable) ? variable : variables[token.Text] = new(token.Text));
        if (token.Kind == Kind.Number)
        {
            if (!double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
                throw Error($"Invalid number '{token.Text}'.", token);
            return new(new NumberTerm(value));
        }
        if (token.Kind == Kind.String) return new(new StringTerm(token.Text));
        if (token.Kind is Kind.Atom or Kind.QuotedAtom)
        {
            if (!Take("(")) return new(new AtomTerm(token.Text));
            var args = new List<Term>();
            int height = 0;
            if (!Take(")"))
            {
                do
                {
                    var arg = ParseTerm(variables, depth + 1);
                    args.Add(arg.Value);
                    height = Math.Max(height, arg.Height + 1);
                } while (Take(","));
                Expect(")");
            }
            return new(new CompoundTerm(token.Text, args), height);
        }
        throw Error($"Unexpected token '{token.Text}'.", token);
    }

    private void CheckDepth(int depth)
    {
        if (depth > maximumDepth) throw Error("Maximum parse depth exceeded.");
    }

    private Token Next() => Current.Kind == Kind.End ? throw Error("Unexpected end of input.") : _tokens[_index++];
    private bool Is(string text) => Current.Kind == Kind.Symbol && Current.Text == text;
    private bool Take(string text) { if (!Is(text)) return false; _index++; return true; }
    private void Expect(string text) { if (!Take(text)) throw Error($"Expected '{text}', found '{Current.Text}'."); }
    private PrologParseException Error(string message, Token? token = null) => new(message, (token ?? Current).Span, fileName);

    private static readonly string[] Operators = ["=\\=", "=:=", ":-", "?-", "\\+", "=<", ">=", "//"];

    private static List<Token> Lex(string source, string? fileName)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new List<Token>();
        int i = 0, line = 1, column = 1;
        SourcePosition Position() => new(i, line, column);
        char Advance()
        {
            char c = source[i++];
            if (c == '\r') { line++; column = 1; }
            else if (c == '\n') { if (i < 2 || source[i - 2] != '\r') line++; column = 1; }
            else column++;
            return c;
        }
        bool StartsWith(string text) => source.AsSpan(i).StartsWith(text, StringComparison.Ordinal);
        PrologParseException ErrorAt(string message, SourcePosition start) => new(message, new(start, Position()), fileName);
        while (i < source.Length)
        {
            char c = source[i];
            SourcePosition start = Position();
            if (char.IsWhiteSpace(c)) { Advance(); continue; }
            if (c == '%')
            {
                while (i < source.Length && source[i] is not ('\r' or '\n')) Advance();
                continue;
            }
            if (StartsWith("/*"))
            {
                Advance(); Advance();
                int nesting = 1;
                while (i < source.Length && nesting > 0)
                {
                    if (StartsWith("/*")) { Advance(); Advance(); nesting++; }
                    else if (StartsWith("*/")) { Advance(); Advance(); nesting--; }
                    else Advance();
                }
                if (nesting != 0) throw ErrorAt("Unterminated block comment.", start);
                continue;
            }
            Kind kind;
            string? text = null;
            if (c is '\'' or '"')
            {
                kind = c == '\'' ? Kind.QuotedAtom : Kind.String;
                Advance();
                var value = new StringBuilder();
                bool closed = false;
                while (i < source.Length)
                {
                    SourcePosition characterStart = Position();
                    char ch = Advance();
                    if (ch == c)
                    {
                        if (i < source.Length && source[i] == c) { value.Append(Advance()); continue; }
                        closed = true;
                        break;
                    }
                    if (ch != '\\') { value.Append(ch); continue; }
                    if (i == source.Length) break;
                    char escape = Advance();
                    value.Append(escape switch
                    {
                        'a' => '\a', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', 'v' => '\v',
                        '\\' or '\'' or '"' => escape,
                        _ => throw ErrorAt($"Unknown escape sequence '\\{escape}'.", characterStart)
                    });
                }
                if (!closed) throw ErrorAt(c == '\'' ? "Unterminated quoted atom." : "Unterminated string.", start);
                text = value.ToString();
            }
            else if (char.IsAsciiDigit(c))
            {
                kind = Kind.Number;
                while (i < source.Length && char.IsAsciiDigit(source[i])) Advance();
                if (i + 1 < source.Length && source[i] == '.' && char.IsAsciiDigit(source[i + 1]))
                {
                    Advance();
                    while (i < source.Length && char.IsAsciiDigit(source[i])) Advance();
                }
                if (i < source.Length && source[i] is 'e' or 'E')
                {
                    Advance();
                    if (i < source.Length && source[i] is '+' or '-') Advance();
                    int digits = i;
                    while (i < source.Length && char.IsAsciiDigit(source[i])) Advance();
                    if (i == digits) throw ErrorAt("Expected digits in number exponent.", start);
                }
            }
            else if (char.IsLetter(c) || c == '_')
            {
                kind = c == '_' || char.IsUpper(c) ? Kind.Variable : Kind.Atom;
                Advance();
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) Advance();
            }
            else if (Operators.FirstOrDefault(StartsWith) is { } op)
            {
                kind = Kind.Symbol;
                for (int j = 0; j < op.Length; j++) Advance();
            }
            else if ("()[],.|!<>=+-*/".Contains(c))
            {
                kind = Kind.Symbol;
                Advance();
            }
            else
            {
                Advance();
                throw ErrorAt($"Unexpected character '{c}'.", start);
            }
            result.Add(new(kind, text ?? source[start.Offset..i], new(start, Position())));
        }
        SourcePosition end = Position();
        result.Add(new(Kind.End, "<eof>", new(end, end)));
        return result;
    }
}
