// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShonePy.Runtime;

namespace ShonePy.Parsing;

public static partial class Parser
{
    private sealed partial class Engine
    {
        private MatchStatement? TryParseMatch()
        {
            int savedIndex = _index, savedYieldCount = _yieldCount, savedAwaitCount = _awaitCount;
            var start = Take();
            ExpressionSyntax subject;
            try { subject = ExpressionList(terminator: ":"); }
            catch (PythonException error) when (error.ErrorName == "SyntaxError")
            {
                _index = savedIndex;
                _yieldCount = savedYieldCount;
                _awaitCount = savedAwaitCount;
                return null;
            }
            if (!Match(":"))
            {
                _index = savedIndex;
                _yieldCount = savedYieldCount;
                _awaitCount = savedAwaitCount;
                return null;
            }

            RequireUnstarred(subject);
            NeedKind(TokenKind.Newline);
            SkipNewlines();
            NeedKind(TokenKind.Indent);
            var cases = new List<MatchCase>();
            PatternSyntax? irrefutable = null;
            do
            {
                var clause = Need("case");
                if (irrefutable is not null) throw ScopeError("irrefutable pattern makes remaining cases unreachable", irrefutable);
                var pattern = ParseCasePattern();
                var captures = new HashSet<string>(StringComparer.Ordinal);
                bool alwaysMatches = ValidatePattern(pattern, captures);
                var guard = Match("if") ? Expression() : null;
                if (alwaysMatches && guard is null) irrefutable = pattern;
                cases.Add(Located(new MatchCase(pattern, guard, Suite(), captures), clause.Line, clause.Column));
                SkipNewlines();
            } while (Current.Kind is not (TokenKind.Dedent or TokenKind.End));
            NeedKind(TokenKind.Dedent);
            return Located(new MatchStatement(subject, cases), start.Line, start.Column);
        }

        private PatternSyntax ParseCasePattern()
        {
            var first = ParseSequenceItem();
            if (Match(",")) return ParseSequenceTail(first, "if", ":");
            if (first is StarPattern) throw ScopeError("star pattern must be in a sequence pattern", first);
            return first;
        }

        private PatternSyntax ParsePattern()
        {
            var pattern = ParseClosedPattern();
            if (Match("|"))
            {
                var alternatives = new List<PatternSyntax> { pattern };
                do { alternatives.Add(ParseClosedPattern()); }
                while (Match("|"));
                pattern = Located(new OrPattern(alternatives), pattern.Line, pattern.Column);
            }
            if (!Match("as")) return pattern;
            var alias = Current;
            string name = NeedPatternName();
            if (name == "_") throw Error("cannot use '_' as a target", alias);
            return Located(new AsPattern(pattern, name), pattern.Line, pattern.Column);
        }

        private PatternSyntax ParseClosedPattern()
        {
            var start = Current;
            if (Match("["))
            {
                if (Match("]")) return Located(new SequencePattern([]), start.Line, start.Column);
                var first = ParseSequenceItem();
                var sequence = Match(",") ? ParseSequenceTail(first, "]") : new SequencePattern([first]);
                Need("]");
                return Located(sequence, start.Line, start.Column);
            }
            if (Match("("))
            {
                if (Match(")")) return Located(new SequencePattern([]), start.Line, start.Column);
                var first = ParseSequenceItem();
                if (Match(","))
                {
                    var sequence = ParseSequenceTail(first, ")");
                    Need(")");
                    return Located(sequence, start.Line, start.Column);
                }
                if (first is StarPattern) throw ScopeError("star pattern must be in a sequence pattern", first);
                Need(")");
                return first;
            }
            if (Match("{")) return ParseMappingPattern(start);

            if (IsPatternLiteral())
            {
                bool singleton = Is("None") || Is("True") || Is("False");
                return Located(new ValuePattern(ParsePatternLiteral(), singleton), start.Line, start.Column);
            }
            string name = NeedPatternName();
            if (name == "_") return Located(new CapturePattern(null), start.Line, start.Column);
            if (Is("."))
            {
                var value = ParseDottedPatternName(start);
                return Is("(") ? ParseClassPattern(start, value) : Located(new ValuePattern(value, false), start.Line, start.Column);
            }
            var className = Located(new NameExpression(name), start.Line, start.Column);
            if (Is("(")) return ParseClassPattern(start, className);
            return Located(new CapturePattern(name), start.Line, start.Column);
        }

        private ClassPattern ParseClassPattern(PyToken start, ExpressionSyntax classExpression)
        {
            Need("(");
            var positional = new List<PatternSyntax>();
            var keywords = new List<(string Name, PatternSyntax Pattern)>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            bool sawKeyword = false;
            while (!Is(")"))
            {
                var item = Current;
                if (item.Kind == TokenKind.Name && _index + 1 < tokens.Count && HasText(tokens[_index + 1], "="))
                {
                    sawKeyword = true;
                    string name = NeedPatternName();
                    Need("=");
                    if (!names.Add(name)) throw Error($"keyword pattern repeated: {name}", item);
                    keywords.Add((name, ParsePattern()));
                }
                else
                {
                    if (sawKeyword) throw Error("positional patterns follow keyword patterns", item);
                    positional.Add(ParsePattern());
                }
                if (!Match(",")) break;
            }
            Need(")");
            return Located(new ClassPattern(classExpression, positional, keywords), start.Line, start.Column);
        }

        private PatternSyntax ParseSequenceItem()
        {
            var start = Current;
            if (!Match("*")) return ParsePattern();
            string name = NeedPatternName();
            return Located(new StarPattern(name == "_" ? null : name), start.Line, start.Column);
        }

        private SequencePattern ParseSequenceTail(PatternSyntax first, string end, string? alternativeEnd = null)
        {
            var items = new List<PatternSyntax> { first };
            while (!Is(end) && !Is(alternativeEnd))
            {
                items.Add(ParseSequenceItem());
                if (!Match(",")) break;
            }
            return Located(new SequencePattern(items), first.Line, first.Column);
        }

        private MappingPattern ParseMappingPattern(PyToken start)
        {
            var items = new List<(ExpressionSyntax Key, PatternSyntax Pattern)>();
            var literalKeys = new HashSet<PyValue>();
            string? rest = null;
            while (!Is("}"))
            {
                var keyToken = Current;
                if (Match("**"))
                {
                    var restToken = Current;
                    rest = NeedPatternName();
                    if (rest == "_") throw Error("cannot use '_' as a target", restToken);
                    Match(",");
                    break;
                }
                ExpressionSyntax key;
                if (IsPatternLiteral())
                {
                    var literal = ParsePatternLiteral();
                    var value = PyValue.From(literal.Value);
                    if (!literalKeys.Add(value)) throw Error($"mapping pattern checks duplicate key ({value.Repr()})", keyToken);
                    key = literal;
                }
                else
                {
                    NeedPatternName();
                    key = ParseDottedPatternName(keyToken);
                }
                Need(":");
                items.Add((key, ParsePattern()));
                if (!Match(",")) break;
            }
            Need("}");
            return Located(new MappingPattern(items, rest), start.Line, start.Column);
        }

        private bool IsPatternLiteral() => Current.Kind is TokenKind.Number or TokenKind.String or TokenKind.Bytes ||
            Is("-") || Is("None") || Is("True") || Is("False");

        private ConstantExpression ParsePatternLiteral()
        {
            var start = Current;
            if (Match("-"))
            {
                if (Current.Kind != TokenKind.Number) throw Error("expected number after '-'");
                var number = Take();
                var value = PyOperations.Unary("-", PyValue.From(number.Value));
                return Located(new ConstantExpression(value.Data), start.Line, start.Column);
            }
            var literal = (ConstantExpression)Prefix();
            if (start.Kind != TokenKind.String || Current.Kind != TokenKind.String) return literal;
            var text = new System.Text.StringBuilder((string)literal.Value!);
            while (Current.Kind == TokenKind.String) text.Append((string)Take().Value!);
            if (Current.Kind == TokenKind.Bytes) throw Error("cannot mix bytes and nonbytes literals");
            return literal with { Value = text.ToString() };
        }

        private ExpressionSyntax ParseDottedPatternName(PyToken start)
        {
            ExpressionSyntax value = Located(new NameExpression(start.Text), start.Line, start.Column);
            do
            {
                Need(".");
                value = Located(new AttributeExpression(value, NeedPatternName()), start.Line, start.Column);
            } while (Is("."));
            return value;
        }

        private string NeedPatternName()
        {
            var token = Current;
            if (token.Kind != TokenKind.Name || token.Text is "False" or "None" or "True" or
                "and" or "as" or "assert" or "async" or "await" or "break" or "class" or "continue" or "def" or
                "del" or "elif" or "else" or "except" or "finally" or "for" or "from" or "global" or "if" or
                "import" or "in" or "is" or "lambda" or "nonlocal" or "not" or "or" or "pass" or "raise" or
                "return" or "try" or "while" or "with" or "yield") throw Error("expected pattern identifier", token);
            return Take().Text;
        }

        private bool ValidatePattern(PatternSyntax pattern, HashSet<string> captures)
        {
            void Capture(string? name, PatternSyntax location)
            {
                if (name is "__debug__") throw ScopeError("cannot assign to __debug__", location);
                if (name is not null && !captures.Add(name))
                    throw ScopeError($"multiple assignments to name '{name}' in pattern", location);
            }

            switch (pattern)
            {
                case CapturePattern capture:
                    Capture(capture.Name, pattern);
                    return true;
                case StarPattern star:
                    Capture(star.Name, pattern);
                    return false;
                case AsPattern alias:
                    bool irrefutable = ValidatePattern(alias.Pattern, captures);
                    Capture(alias.Name, pattern);
                    return irrefutable;
                case SequencePattern sequence:
                    bool hasStar = false;
                    foreach (var item in sequence.Items)
                    {
                        if (item is StarPattern)
                        {
                            if (hasStar) throw ScopeError("multiple starred names in sequence pattern", item);
                            hasStar = true;
                        }
                        ValidatePattern(item, captures);
                    }
                    return false;
                case MappingPattern mapping:
                    foreach (var (_, item) in mapping.Items) ValidatePattern(item, captures);
                    Capture(mapping.Rest, pattern);
                    return false;
                case ClassPattern cls:
                    foreach (var item in cls.Positional) ValidatePattern(item, captures);
                    foreach (var (_, item) in cls.Keywords) ValidatePattern(item, captures);
                    return false;
                case OrPattern either:
                    HashSet<string>? expected = null;
                    bool matchesAll = false;
                    foreach (var alternative in either.Alternatives)
                    {
                        if (matchesAll) throw ScopeError("irrefutable pattern makes remaining alternatives unreachable", alternative);
                        var names = new HashSet<string>(StringComparer.Ordinal);
                        matchesAll = ValidatePattern(alternative, names);
                        if (expected is null) expected = names;
                        else if (!expected.SetEquals(names)) throw ScopeError("alternative patterns bind different names", alternative);
                    }
                    foreach (string name in expected!) Capture(name, pattern);
                    return matchesAll;
                default:
                    return false;
            }
        }
    }
}
