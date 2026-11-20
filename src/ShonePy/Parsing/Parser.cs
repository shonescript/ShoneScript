// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShonePy.Runtime;

namespace ShonePy.Parsing;

public static partial class Parser
{
    public static ModuleSyntax Parse(string source, string? fileName = null) => new Engine(new Lexer(source, fileName).Scan(), fileName).Parse();

    private sealed partial class Engine(List<PyToken> tokens, string? fileName)
    {
        private int _index;
        private int _functionDepth;
        private int _yieldCount;
        private int _awaitCount;
        private bool _inAsyncFunction;
        private int _comprehensionDepth;
        private int _loopDepth;
        private int _classDepth;
        private PyToken Current => tokens[Math.Min(_index, tokens.Count - 1)];
        private PyToken Take() => tokens[_index++];
        private static bool HasText(PyToken token, string? text) =>
            token.Kind is TokenKind.Name or TokenKind.Operator && token.Text == text;
        private bool Is(string? text) => HasText(Current, text);
        private bool Match(string text) { if (!Is(text)) return false; Take(); return true; }
        private PyToken Need(string text) => Match(text) ? tokens[_index - 1] : throw Error($"expected '{text}'");

        public ModuleSyntax Parse()
        {
            var body = new List<StatementSyntax>();
            SkipNewlines();
            while (Current.Kind != TokenKind.End)
            {
                body.AddRange(ParseStatement());
                SkipNewlines();
            }
            ValidateNestedScopes(body, []);
            return new ModuleSyntax(body) { Line = 1, Column = 1, FileName = fileName };
        }

        private void ValidateNestedScopes(IEnumerable<StatementSyntax> statements, IReadOnlyList<HashSet<string>> enclosing)
        {
            foreach (var statement in statements)
            {
                switch (statement)
                {
                    case FunctionDefinition function:
                        (HashSet<string> Globals, HashSet<string> Nonlocals) declarations;
                        try { declarations = PyInterpreter.FindDeclarations(function.Body); }
                        catch (PythonException error) { throw ScopeError(error.Message, function); }
                        foreach (string name in function.Parameters.Concat(new[] { function.VarArgName, function.KwArgName }.OfType<string>()))
                        {
                            if (declarations.Globals.Contains(name)) throw ScopeError($"name '{name}' is parameter and global", function);
                            if (declarations.Nonlocals.Contains(name)) throw ScopeError($"name '{name}' is parameter and nonlocal", function);
                        }
                        foreach (string name in declarations.Nonlocals)
                            if (!enclosing.Any(scope => scope.Contains(name)))
                                throw ScopeError($"no binding for nonlocal '{name}' found", function);

                        var locals = PyInterpreter.FindLocals(function.Body);
                        locals.ExceptWith(declarations.Globals);
                        locals.ExceptWith(declarations.Nonlocals);
                        locals.UnionWith(function.Parameters);
                        if (function.VarArgName is not null) locals.Add(function.VarArgName);
                        if (function.KwArgName is not null) locals.Add(function.KwArgName);
                        ValidateNestedScopes(function.Body, [.. enclosing, locals]);
                        break;
                    case ClassDefinition cls:
                        try { declarations = PyInterpreter.FindDeclarations(cls.Body); }
                        catch (PythonException error) { throw ScopeError(error.Message, cls); }
                        foreach (string name in declarations.Nonlocals)
                            if (!enclosing.Any(scope => scope.Contains(name)))
                                throw ScopeError($"no binding for nonlocal '{name}' found", cls);
                        ValidateNestedScopes(cls.Body, enclosing);
                        break;
                    case IfStatement conditional:
                        ValidateNestedScopes(conditional.Body, enclosing);
                        ValidateNestedScopes(conditional.ElseBody, enclosing);
                        break;
                    case WhileStatement loop:
                        ValidateNestedScopes(loop.Body, enclosing);
                        ValidateNestedScopes(loop.ElseBody, enclosing);
                        break;
                    case ForStatement loop:
                        ValidateNestedScopes(loop.Body, enclosing);
                        ValidateNestedScopes(loop.ElseBody, enclosing);
                        break;
                    case TryStatement attempt:
                        foreach (var suite in attempt.Suites()) ValidateNestedScopes(suite, enclosing);
                        break;
                    case WithStatement context:
                        ValidateNestedScopes(context.Body, enclosing);
                        break;
                    case MatchStatement match:
                        foreach (var clause in match.Cases) ValidateNestedScopes(clause.Body, enclosing);
                        break;
                }
            }
        }

        private PythonException ScopeError(string message, SyntaxNode node) =>
            new("SyntaxError", message, node.Line, node.Column, fileName);

        private List<StatementSyntax> ParseStatement()
        {
            if (Is("@")) return [ParseDecoratedDefinition()];
            if (Match("if")) return [ParseIf(tokens[_index - 1])];
            if (Match("while")) return [ParseWhile(tokens[_index - 1])];
            if (Match("for")) return [ParseFor(tokens[_index - 1])];
            if (Match("def")) return [ParseFunction(tokens[_index - 1])];
            if (Match("async"))
            {
                var start = tokens[_index - 1];
                if (Match("for"))
                {
                    if (!_inAsyncFunction) throw Error("'async for' outside async function", start);
                    return [ParseFor(start, isAsync: true)];
                }
                if (Match("with"))
                {
                    if (!_inAsyncFunction) throw Error("'async with' outside async function", start);
                    return [ParseWith(start, isAsync: true)];
                }
                return [ParseAsyncDefinition(start)];
            }
            if (Match("class")) return [ParseClass(tokens[_index - 1])];
            if (Match("try")) return [ParseTry(tokens[_index - 1])];
            if (Match("with")) return [ParseWith(tokens[_index - 1])];
            if (Is("match") && TryParseMatch() is { } match) return [match];
            return ParseSimpleStatements();
        }

        private StatementSyntax ParseDecoratedDefinition()
        {
            var decorators = new List<ExpressionSyntax>();
            while (Match("@"))
            {
                decorators.Add(Expression());
                NeedKind(TokenKind.Newline);
                SkipNewlines();
            }

            StatementSyntax definition;
            if (Match("def")) definition = ParseFunction(tokens[_index - 1]);
            else if (Match("async")) definition = ParseAsyncDefinition(tokens[_index - 1]);
            else if (Match("class")) definition = ParseClass(tokens[_index - 1]);
            else throw Error("expected function or class definition after decorator");
            return definition with { Decorators = decorators };
        }

        private FunctionDefinition ParseAsyncDefinition(PyToken start)
        {
            Need("def");
            return ParseFunction(start, isAsync: true);
        }

        private List<StatementSyntax> ParseSimpleStatements()
        {
            var statements = new List<StatementSyntax>();
            do { statements.AddRange(ParseSimpleStatement()); }
            while (Match(";") && !AtStatementBoundary());
            if (Current.Kind == TokenKind.Newline) Take();
            else if (!AtStatementBoundary()) throw Error("expected end of statement");
            return statements;
        }

        private List<StatementSyntax> ParseSimpleStatement()
        {
            if (Match("import")) return [ParseImport(tokens[_index - 1])];
            if (Match("from")) return [ParseFromImport(tokens[_index - 1])];
            var start = Current;
            if (Match("yield"))
                return [Located(new ExpressionStatement(ParseYield(start)), start.Line, start.Column)];
            if (Match("del"))
            {
                var statements = new List<StatementSyntax>();
                do
                {
                    if (Expression() is not AttributeExpression target)
                        throw Error("only attribute deletion is supported", start);
                    statements.Add(Located(new DeleteAttributeStatement(target), start.Line, start.Column));
                } while (Match(",") && !AtStatementBoundary());
                return statements;
            }
            if (Match("assert"))
            {
                var test = Expression();
                var message = Match(",") ? Expression() : null;
                return [Located(new AssertStatement(test, message), start.Line, start.Column)];
            }
            if (Match("raise"))
            {
                var value = IsLineEnd() ? null : Expression();
                var cause = value is not null && Match("from") ? Expression() : null;
                return [Located(new RaiseStatement(value) { Cause = cause }, start.Line, start.Column)];
            }
            if (Match("return"))
            {
                if (_functionDepth == 0) throw Error("'return' outside function", start);
                ExpressionSyntax? value = IsLineEnd() ? null : ExpressionList();
                if (value is not null) RequireUnstarred(value);
                return [new ReturnStatement(value) { Line = start.Line, Column = start.Column }];
            }
            if (Match("break")) { if (_loopDepth == 0) throw Error("'break' outside loop", start); return [new BreakStatement { Line = start.Line, Column = start.Column }]; }
            if (Match("continue")) { if (_loopDepth == 0) throw Error("'continue' outside loop", start); return [new ContinueStatement { Line = start.Line, Column = start.Column }]; }
            if (Match("pass")) return [new PassStatement { Line = start.Line, Column = start.Column }];
            if (Match("global") || Match("nonlocal"))
            {
                bool isGlobal = tokens[_index - 1].Text == "global";
                if (_functionDepth == 0 && _classDepth == 0 && !isGlobal) throw Error("'nonlocal' declaration not allowed at module level", start);
                var names = new List<string> { NeedName() };
                while (Match(",")) names.Add(NeedName());
                return [new ScopeDeclarationStatement(isGlobal, names) { Line = start.Line, Column = start.Column }];
            }
            var first = ExpressionList();
            if (Current.Kind == TokenKind.Operator && Current.Text is "=" or "+=" or "-=" or "*=" or "/=" or "//=" or "%=" or "**=" or "&=" or "|=" or "^=" or "<<=" or ">>=")
            {
                var op = Take();
                if (op.Text != "=")
                {
                    if (!IsAugmentedAssignmentTarget(first)) throw Error("invalid assignment target", op);
                    var right = Match("yield") ? ParseYield(tokens[_index - 1]) : ExpressionList();
                    RequireUnstarred(right);
                    return [new AugmentedAssignmentStatement(first, op.Text[..^1], right) { Line = start.Line, Column = start.Column }];
                }
                ValidateAssignmentTarget(first, start);
                var targets = new List<ExpressionSyntax> { first };
                do
                {
                    var value = Match("yield") ? ParseYield(tokens[_index - 1]) : ExpressionList();
                    if (Match("=")) { ValidateAssignmentTarget(value, start); targets.Add(value); }
                    else
                    {
                        RequireUnstarred(value);
                        return [new AssignmentStatement(targets, value) { Line = start.Line, Column = start.Column }];
                    }
                } while (true);
            }
            RequireUnstarred(first);
            return [new ExpressionStatement(first) { Line = start.Line, Column = start.Column }];
        }

        private YieldExpression ParseYield(PyToken start, string? terminator = null)
        {
            if (_functionDepth == 0) throw Error("'yield' outside function", start);
            if (_inAsyncFunction) throw Error("async generators are not supported", start);
            if (_comprehensionDepth != 0) throw Error("'yield' inside comprehension", start);
            _yieldCount++;
            bool delegating = Match("from");
            var value = delegating ? Expression() : IsLineEnd() || Is(terminator) ? null : ExpressionList(terminator: terminator);
            if (value is not null) RequireUnstarred(value);
            return Located(new YieldExpression(value, delegating), start.Line, start.Column);
        }

        private TryStatement ParseTry(PyToken start)
        {
            var body = Suite();
            var handlers = new List<ExceptClause>();
            while (Match("except"))
            {
                var clause = tokens[_index - 1];
                if (handlers.Count > 0 && handlers[^1].Type is null)
                    throw Error("default 'except:' must be last", clause);
                if (Is("*")) throw Error("exception groups are not supported");
                var type = Is(":") ? null : Expression();
                bool unparenthesized = Match(",");
                if (unparenthesized)
                {
                    var types = new List<ExpressionSyntax> { type!, Expression() };
                    while (Match(",")) types.Add(Expression());
                    type = Located(new TupleExpression(types), clause.Line, clause.Column);
                    if (Is("as")) throw Error("multiple exception types must be parenthesized when using 'as'");
                }
                string? alias = type is not null && Match("as") ? NeedName() : null;
                handlers.Add(Located(new ExceptClause(type, alias, Suite()), clause.Line, clause.Column));
            }

            if (handlers.Count == 0 && !Is("finally")) throw Error("expected 'except' or 'finally'");
            var elseBody = handlers.Count > 0 && Match("else") ? Suite() : [];
            var finallyBody = Match("finally") ? Suite() : [];
            return Located(new TryStatement(body, handlers, elseBody, finallyBody), start.Line, start.Column);
        }

        private WithStatement ParseWith(PyToken start, bool isAsync = false)
        {
            var items = TryParenthesizedWithItems() ?? ParseWithItems(allowTrailingComma: false);
            return Located(new WithStatement(items, Suite()) { IsAsync = isAsync }, start.Line, start.Column);
        }

        private List<WithItem>? TryParenthesizedWithItems()
        {
            int start = _index;
            if (!Match("(")) return null;
            try
            {
                var items = ParseWithItems(allowTrailingComma: true);
                Need(")");
                if (Is(":")) return items;
            }
            catch (PythonException error) when (error.ErrorName == "SyntaxError")
            {
                // An opening parenthesis can also start a context expression, e.g. (factory)(arg).
            }
            _index = start;
            return null;
        }

        private List<WithItem> ParseWithItems(bool allowTrailingComma)
        {
            var items = new List<WithItem>();
            do
            {
                var start = Current;
                var context = Expression();
                ExpressionSyntax? target = null;
                if (Match("as"))
                {
                    var targetStart = Current;
                    target = Expression();
                    ValidateAssignmentTarget(target, targetStart);
                }
                items.Add(Located(new WithItem(context, target), start.Line, start.Column));
            } while (Match(",") && !(allowTrailingComma && Is(")")));
            return items;
        }

        private IfStatement ParseIf(PyToken start)
        {
            var test = Expression(); var body = Suite(); var otherwise = new List<StatementSyntax>();
            if (Match("elif")) otherwise.Add(ParseIf(tokens[_index - 1]));
            else if (Match("else")) otherwise = Suite();
            return new IfStatement(test, body, otherwise) { Line = start.Line, Column = start.Column };
        }

        private WhileStatement ParseWhile(PyToken start)
        {
            var test = Expression();
            var body = LoopSuite();
            var otherwise = Match("else") ? Suite() : [];
            return new WhileStatement(test, body, otherwise) { Line = start.Line, Column = start.Column };
        }

        private ForStatement ParseFor(PyToken start, bool isAsync = false)
        {
            var target = ExpressionList(5, "in");
            ValidateAssignmentTarget(target, start);
            Need("in");
            var iterable = ExpressionList(0, ":");
            RequireUnstarred(iterable);
            var body = LoopSuite();
            var otherwise = Match("else") ? Suite() : [];
            return new ForStatement(target, iterable, body, otherwise)
            {
                Line = start.Line, Column = start.Column, IsAsync = isAsync
            };
        }

        private List<StatementSyntax> LoopSuite()
        {
            _loopDepth++;
            try { return Suite(); }
            finally { _loopDepth--; }
        }

        private ImportStatement ParseImport(PyToken start)
        {
            var imports = new List<(string Module, string? Alias)>();
            do
            {
                string module = NeedName();
                while (Match(".")) module += "." + NeedName();
                string? alias = Match("as") ? NeedName() : null;
                imports.Add((module, alias));
            } while (Match(","));
            return new ImportStatement(imports) { Line = start.Line, Column = start.Column };
        }

        private FromImportStatement ParseFromImport(PyToken start)
        {
            int relativeLevel = 0;
            while (Match(".")) relativeLevel++;
            string module = string.Empty;
            if (Current.Kind == TokenKind.Name && !Is("import"))
            {
                module = NeedName();
                while (Match(".")) module += "." + NeedName();
            }
            if (relativeLevel == 0 && module.Length == 0) throw Error("expected module name");
            Need("import");
            var imports = new List<(string Name, string? Alias)>();
            do
            {
                string name = NeedName();
                string? alias = Match("as") ? NeedName() : null;
                imports.Add((name, alias));
            } while (Match(","));
            return new FromImportStatement(module, imports, relativeLevel) { Line = start.Line, Column = start.Column };
        }

        private ClassDefinition ParseClass(PyToken start)
        {
            string name = NeedName();
            var bases = new List<ExpressionSyntax>();
            if (Match("("))
            {
                while (!Is(")"))
                {
                    bases.Add(Expression());
                    if (!Match(",")) break;
                }
                Need(")");
            }

            int savedFunctionDepth = _functionDepth, savedLoopDepth = _loopDepth;
            bool savedAsync = _inAsyncFunction;
            _inAsyncFunction = false;
            _functionDepth = _loopDepth = 0;
            _classDepth++;
            try { return Located(new ClassDefinition(name, bases, Suite()), start.Line, start.Column); }
            finally
            {
                _classDepth--;
                _inAsyncFunction = savedAsync;
                _functionDepth = savedFunctionDepth;
                _loopDepth = savedLoopDepth;
            }
        }

        private FunctionDefinition ParseFunction(PyToken start, bool isAsync = false)
        {
            var name = NeedName();
            Need("(");
            var parameters = new List<string>();
            var defaults = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
            var parameterSet = new HashSet<string>(StringComparer.Ordinal);
            int positionalOnlyCount = -1, keywordOnlyStart = -1;
            bool keywordOnly = false, positionalDefaultSeen = false, sawVarArg = false, bareStarNeedsKeywordOnly = false;
            string? varArg = null, kwArg = null;
            if (!Match(")"))
            {
                while (true)
                {
                    var location = Current;
                    if (Match("/"))
                    {
                        if (positionalOnlyCount >= 0 || keywordOnly || parameters.Count == 0 || sawVarArg)
                            throw Error("invalid positional-only separator", location);
                        positionalOnlyCount = parameters.Count;
                    }
                    else if (Match("**"))
                    {
                        if (kwArg is not null) throw Error("duplicate ** parameter", location);
                        kwArg = NeedName();
                        if (!parameterSet.Add(kwArg)) throw Error($"duplicate argument '{kwArg}'", location);
                        Match(",");
                        break;
                    }
                    else if (Match("*"))
                    {
                        if (sawVarArg) throw Error("duplicate * parameter", location);
                        sawVarArg = true;
                        keywordOnly = true;
                        keywordOnlyStart = parameters.Count;
                        if (Current.Kind == TokenKind.Name)
                        {
                            varArg = NeedName();
                            if (!parameterSet.Add(varArg)) throw Error($"duplicate argument '{varArg}'", location);
                        }
                        else bareStarNeedsKeywordOnly = true;
                    }
                    else
                    {
                        var parameter = NeedName();
                        if (!parameterSet.Add(parameter)) throw Error($"duplicate argument '{parameter}'", location);
                        if (bareStarNeedsKeywordOnly) bareStarNeedsKeywordOnly = false;
                        parameters.Add(parameter);
                        if (Match("="))
                        {
                            defaults.Add(parameter, Expression());
                            if (!keywordOnly) positionalDefaultSeen = true;
                        }
                        else if (!keywordOnly && positionalDefaultSeen) throw Error("non-default argument follows default argument", location);
                    }
                    if (!Match(",")) break;
                    if (Is(")")) break;
                }
                Need(")");
            }
            if (bareStarNeedsKeywordOnly) throw Error("named arguments must follow bare *", Current);
            if (positionalOnlyCount < 0) positionalOnlyCount = 0;
            if (keywordOnlyStart < 0) keywordOnlyStart = parameters.Count;
            if (Match("->"))
            {
                int yieldsBefore = _yieldCount, awaitsBefore = _awaitCount;
                Expression();
                if (_yieldCount != yieldsBefore) throw Error("yield in annotations is not supported", start);
                if (_awaitCount != awaitsBefore) throw Error("await in annotations is not supported", start);
            }
            int savedLoopDepth = _loopDepth, savedYieldCount = _yieldCount;
            bool savedAsync = _inAsyncFunction;
            _inAsyncFunction = isAsync;
            _loopDepth = _yieldCount = 0;
            _functionDepth++;
            try
            {
                var body = Suite();
                return new FunctionDefinition(name, parameters, body)
                {
                    Line = start.Line, Column = start.Column, FileName = fileName,
                    IsGenerator = _yieldCount > 0, IsAsync = isAsync,
                    Defaults = parameters.Where(defaults.ContainsKey).Select(p => defaults[p]).ToArray(),
                    NamedDefaults = defaults, PositionalOnlyCount = positionalOnlyCount, KeywordOnlyStart = keywordOnlyStart,
                    VarArgName = varArg, KwArgName = kwArg
                };
            }
            finally
            {
                _functionDepth--;
                _loopDepth = savedLoopDepth;
                _yieldCount = savedYieldCount;
                _inAsyncFunction = savedAsync;
            }
        }

        private List<StatementSyntax> Suite()
        {
            Need(":");
            if (Current.Kind == TokenKind.Newline)
            {
                Take();
                SkipNewlines();
                NeedKind(TokenKind.Indent);
                var body = new List<StatementSyntax>();
                while (Current.Kind is not (TokenKind.Dedent or TokenKind.End))
                {
                    body.AddRange(ParseStatement());
                    SkipNewlines();
                }
                NeedKind(TokenKind.Dedent);
                return body;
            }
            return ParseSimpleStatements();
        }

        private ExpressionSyntax ExpressionList(int minimum = 0, string? terminator = null)
        {
            var first = StarItem(minimum);
            if (!Match(",")) return first;

            var items = new List<ExpressionSyntax> { first };
            while (!IsLineEnd() && !Is("=") && !Is(terminator))
            {
                items.Add(StarItem(minimum));
                if (!Match(",")) break;
            }
            return Located(new TupleExpression(items), first.Line, first.Column);
        }

        private ExpressionSyntax StarItem(int minimum = 0)
        {
            var token = Current;
            return Match("*") ? Located(new StarredExpression(UnpackingOperand(Math.Max(5, minimum))), token.Line, token.Column) : Expression(minimum);
        }

        private ExpressionSyntax UnpackingOperand(int minimum = 5)
        {
            if (Is("not")) throw Error("expected unpacking operand", Current);
            return Expression(minimum);
        }

        private ExpressionSyntax Expression(int minimum = 0, bool atomOnly = false) =>
            ContinueExpression(Prefix(atomOnly), minimum);

        private ExpressionSyntax ContinueExpression(ExpressionSyntax left, int minimum = 0)
        {
            while (true)
            {
                if (Current.Kind == TokenKind.Operator && Current.Text is "." or "(" or "[")
                {
                    if (14 < minimum) break;
                    if (Match(".")) left = Located(new AttributeExpression(left, NeedName()), left.Line, left.Column);
                    else if (Match("["))
                    {
                        ExpressionSyntax index;
                        if (Is(":") || Is("]")) index = ParseSlice(null);
                        else
                        {
                            var first = Expression();
                            index = Is(":") ? ParseSlice(first) : first;
                        }
                        Need("]"); left = Located(new SubscriptExpression(left, index), left.Line, left.Column);
                    }
                    else
                    {
                        Take();
                        left = ParseCall(left);
                    }
                    continue;
                }
                if (Current.Kind is not (TokenKind.Name or TokenKind.Operator)) break;
                var op = Current.Text;
                if (op == "not" && _index + 1 < tokens.Count && HasText(tokens[_index + 1], "in")) op = "not in";
                var precedence = Precedence(op);
                if (precedence < minimum) break;
                Take();
                if (op == "is" && Match("not")) op = "is not";
                else if (op == "not in") Need("in");
                var right = Expression(precedence + (op == "**" ? 0 : 1));
                if (IsComparisonOperator(op))
                {
                    var operands = new List<ExpressionSyntax> { left, right };
                    var operators = new List<string> { op };
                    while (true)
                    {
                        if (Current.Kind is not (TokenKind.Name or TokenKind.Operator)) break;
                        var next = Current.Text;
                        if (next == "not" && _index + 1 < tokens.Count && HasText(tokens[_index + 1], "in")) next = "not in";
                        if (!IsComparisonOperator(next)) break;
                        Take();
                        if (next == "is" && Match("not")) next = "is not";
                        else if (next == "not in") Need("in");
                        operators.Add(next);
                        operands.Add(Expression(5));
                    }
                    left = operators.Count == 1
                        ? Located(new BinaryExpression(operands[0], operators[0], operands[1]), left.Line, left.Column)
                        : Located(new ComparisonChainExpression(operands, operators), left.Line, left.Column);
                }
                else left = Located(new BinaryExpression(left, op, right), left.Line, left.Column);
            }
            if (minimum == 0 && Match("if"))
            {
                var test = Expression(1);
                Need("else");
                return Located(new ConditionalExpression(test, left, Expression()), left.Line, left.Column);
            }
            return left;
        }

        private CallExpression ParseCall(ExpressionSyntax function)
        {
            var arguments = new List<ExpressionSyntax>();
            var keywords = new List<(string Name, ExpressionSyntax Value)>();
            var ordered = new List<CallArgument>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            bool sawKeyword = false;
            while (!Is(")"))
            {
                var location = Current;
                if (Match("**"))
                {
                    var value = Expression();
                    if (AtComprehensionClause()) throw Error("cannot use dict unpacking in generator expression", location);
                    ordered.Add(new CallArgument(CallArgumentKind.MappingExpansion, value));
                }
                else if (Match("*"))
                {
                    int yieldsBefore = _yieldCount, awaitsBefore = _awaitCount;
                    var value = Expression();
                    if (AtComprehensionClause())
                    {
                        var element = Located(new StarredExpression(value), location.Line, location.Column);
                        AddGeneratorArgument(location, element, yieldsBefore, awaitsBefore);
                    }
                    else ordered.Add(new CallArgument(CallArgumentKind.IterableExpansion, value));
                }
                else if (Current.Kind == TokenKind.Name && _index + 1 < tokens.Count && HasText(tokens[_index + 1], "="))
                {
                    string name = NeedName();
                    Need("=");
                    var value = Expression();
                    if (!names.Add(name)) throw Error($"keyword argument repeated: {name}", location);
                    keywords.Add((name, value));
                    ordered.Add(new CallArgument(CallArgumentKind.Keyword, value, name));
                    sawKeyword = true;
                }
                else
                {
                    if (sawKeyword) throw Error("positional argument follows keyword argument", location);
                    int yieldsBefore = _yieldCount, awaitsBefore = _awaitCount;
                    var value = Expression();
                    if (AtComprehensionClause()) AddGeneratorArgument(location, value, yieldsBefore, awaitsBefore);
                    else
                    {
                        arguments.Add(value);
                        ordered.Add(new CallArgument(CallArgumentKind.Positional, value));
                    }
                }
                if (!Match(",")) break;
            }
            Need(")");
            return Located(new CallExpression(function, arguments) { Keywords = keywords, OrderedArguments = ordered }, function.Line, function.Column);

            void AddGeneratorArgument(PyToken location, ExpressionSyntax element, int yieldsBefore, int awaitsBefore)
            {
                if (ordered.Count != 0) throw Error("generator expression must be parenthesized", location);
                var generator = ParseGeneratorExpression(location, element, yieldsBefore, awaitsBefore);
                if (!Is(")")) throw Error("generator expression must be parenthesized", location);
                arguments.Add(generator);
                ordered.Add(new CallArgument(CallArgumentKind.Positional, generator));
            }
        }

        private ExpressionSyntax? SliceBound() => Is(":") || Is("]") ? null : Expression();

        private ExpressionSyntax ParseSlice(ExpressionSyntax? start)
        {
            Need(":");
            var stop = SliceBound();
            ExpressionSyntax? step = null;
            if (Match(":")) step = SliceBound();
            return new SliceExpression(start, stop, step);
        }

        private ExpressionSyntax Prefix(bool atomOnly = false)
        {
            var token = Take();
            if (atomOnly && (HasText(token, "await") || HasText(token, "not") ||
                HasText(token, "+") || HasText(token, "-") || HasText(token, "~")))
                throw Error("expected primary expression after 'await'", token);
            if (token.Kind is TokenKind.String or TokenKind.Bytes)
            {
                if (Current.Kind is TokenKind.String or TokenKind.Bytes && Current.Kind != token.Kind)
                    throw Error("cannot mix bytes and nonbytes literals", Current);
                if (token.Kind == TokenKind.Bytes && Current.Kind == TokenKind.Bytes)
                {
                    var bytes = new List<byte>();
                    bytes.AddRange((PyBytes)token.Value!);
                    while (Current.Kind == TokenKind.Bytes)
                    {
                        var part = (PyBytes)Take().Value!;
                        if (part.Count > PyBytes.MaximumLength - bytes.Count)
                            throw Error("bytes literal is too large", token);
                        bytes.AddRange(part);
                    }
                    if (Current.Kind == TokenKind.String) throw Error("cannot mix bytes and nonbytes literals", Current);
                    return Located(new ConstantExpression(PyBytes.FromOwnedArray(bytes.ToArray())), token.Line, token.Column);
                }
            }
            if (token.Kind is TokenKind.Number or TokenKind.String or TokenKind.Bytes)
                return Located(new ConstantExpression(token.Value), token.Line, token.Column);
            if (token.Kind == TokenKind.Name)
            {
                if (token.Text is "True" or "False" or "None") return Located(new ConstantExpression(token.Text switch { "True" => true, "False" => false, _ => null }), token.Line, token.Column);
                if (token.Text == "not") return Located(new UnaryExpression(token.Text, Expression(3)), token.Line, token.Column);
                if (token.Text is "lambda") throw Error("lambda expressions are not supported yet", token);
                if (token.Text == "await")
                {
                    if (!_inAsyncFunction) throw Error("'await' outside async function", token);
                    _awaitCount++;
                    // Await binds to a primary, including its trailers, before any binary operator.
                    return Located(new AwaitExpression(Expression(14, atomOnly: true)), token.Line, token.Column);
                }
                if (token.Text is "and" or "as" or "assert" or "async" or "break" or "class" or "continue" or
                    "def" or "del" or "elif" or "else" or "except" or "finally" or "for" or "from" or "global" or "if" or
                    "import" or "in" or "is" or "nonlocal" or "or" or "pass" or "raise" or "return" or "try" or "while" or
                    "with" or "yield") throw Error("expected expression", token);
                return Located(new NameExpression(token.Text), token.Line, token.Column);
            }
            if (token.Kind == TokenKind.Operator && token.Text is "+" or "-" or "~")
                return Located(new UnaryExpression(token.Text, Expression(11)), token.Line, token.Column);
            if (token.Text == "(")
            {
                if (Match(")")) return Located(new TupleExpression([]), token.Line, token.Column);
                if (Match("yield"))
                {
                    var expression = ParseYield(tokens[_index - 1], ")");
                    Need(")");
                    return expression;
                }
                int yieldsBefore = _yieldCount, awaitsBefore = _awaitCount;
                ExpressionSyntax first;
                if (Match("*"))
                {
                    var star = tokens[_index - 1];
                    var operand = ParsePossiblyUnpackingComprehensionOperand(out bool comprehensionOnly);
                    if (comprehensionOnly && !AtComprehensionClause()) throw Error("expected generator expression", star);
                    first = Located(new StarredExpression(operand), star.Line, star.Column);
                }
                else first = Expression();
                if (AtComprehensionClause())
                {
                    var generator = ParseGeneratorExpression(token, first, yieldsBefore, awaitsBefore);
                    Need(")");
                    return generator;
                }
                if (!Match(",")) { RequireUnstarred(first); Need(")"); return first; }
                var items = new List<ExpressionSyntax> { first };
                while (!Is(")")) { items.Add(StarItem()); if (!Match(",")) break; }
                Need(")"); return Located(new TupleExpression(items), token.Line, token.Column);
            }
            if (token.Text == "[") return ParseList(token);
            if (token.Text == "{") return ParseBraceDisplay(token);
            throw Error("expected expression", token);
        }

        private GeneratorExpression ParseGeneratorExpression(PyToken start, ExpressionSyntax element, int yieldsBefore, int awaitsBefore)
        {
            ValidateComprehensionElement(start, "generator expression", yieldsBefore);
            if (_awaitCount != awaitsBefore) throw Error("async generator expressions are not supported", start);
            return Located(new GeneratorExpression(element, ParseComprehensionClauses(generator: true)), start.Line, start.Column);
        }

        private ExpressionSyntax ParseList(PyToken start)
        {
            if (Match("]")) return Located(new ListExpression([]), start.Line, start.Column);
            int yieldsBefore = _yieldCount;
            var first = StarItem();
            if (AtComprehensionClause())
            {
                ValidateComprehensionElement(start, "list comprehension", yieldsBefore);
                var clauses = ParseComprehensionClauses();
                Need("]");
                return Located(new ListComprehensionExpression(first, clauses), start.Line, start.Column);
            }

            var items = new List<ExpressionSyntax> { first };
            while (Match(",") && !Is("]")) items.Add(StarItem());
            Need("]");
            return Located(new ListExpression(items), start.Line, start.Column);
        }

        private ExpressionSyntax ParseBraceDisplay(PyToken start)
        {
            if (Match("}")) return Located(new DictExpression([]), start.Line, start.Column);
            int yieldsBefore = _yieldCount;
            if (Match("**"))
            {
                var mapping = ParsePossiblyUnpackingComprehensionOperand(out bool comprehensionOnly);
                if (AtComprehensionClause())
                {
                    ValidateComprehensionElement(start, "dict comprehension", yieldsBefore);
                    var clauses = ParseComprehensionClauses();
                    Need("}");
                    return Located(new DictUnpackingComprehensionExpression(mapping, clauses), start.Line, start.Column);
                }
                if (comprehensionOnly) throw Error("expected dict comprehension", Current);
                return ParseDictDisplayTail(start, (null, mapping));
            }
            var first = StarItem();
            if (Match(":"))
            {
                RequireUnstarred(first);
                return ParseDictTail(start, first, Expression(), yieldsBefore);
            }

            if (AtComprehensionClause())
            {
                ValidateComprehensionElement(start, "set comprehension", yieldsBefore);
                var clauses = ParseComprehensionClauses();
                Need("}");
                return Located(new SetComprehensionExpression(first, clauses), start.Line, start.Column);
            }

            var items = new List<ExpressionSyntax> { first };
            while (Match(",") && !Is("}")) items.Add(StarItem());
            Need("}");
            return Located(new SetExpression(items), start.Line, start.Column);
        }

        private ExpressionSyntax ParseDictTail(PyToken start, ExpressionSyntax key, ExpressionSyntax value, int yieldsBefore)
        {
            var first = (Key: key, Value: value);
            if (AtComprehensionClause())
            {
                ValidateComprehensionElement(start, "dict comprehension", yieldsBefore);
                var clauses = ParseComprehensionClauses();
                Need("}");
                return Located(new DictComprehensionExpression(first.Key, first.Value, clauses), start.Line, start.Column);
            }

            return ParseDictDisplayTail(start, first);
        }

        private DictExpression ParseDictDisplayTail(PyToken start, (ExpressionSyntax? Key, ExpressionSyntax Value) first)
        {
            var items = new List<(ExpressionSyntax? Key, ExpressionSyntax Value)> { first };
            while (Match(",") && !Is("}"))
            {
                if (Match("**")) items.Add((null, UnpackingOperand()));
                else
                {
                    var key = Expression();
                    Need(":");
                    items.Add((key, Expression()));
                }
            }
            Need("}");
            return Located(new DictExpression(items), start.Line, start.Column);
        }

        private ExpressionSyntax ParsePossiblyUnpackingComprehensionOperand(out bool comprehensionOnly)
        {
            // Generator and dict comprehension unpacking take a full expression.
            // Tuple stars and dict display unpacking take a bitwise-or expression.
            if (Is("not"))
            {
                comprehensionOnly = true;
                return Expression();
            }
            var displayOperand = UnpackingOperand();
            var operand = ContinueExpression(displayOperand);
            comprehensionOnly = !ReferenceEquals(displayOperand, operand);
            return operand;
        }

        private bool AtComprehensionClause() => Is("for") || Is("async");

        private void ValidateComprehensionElement(PyToken start, string kind, int yieldsBefore)
        {
            if (_yieldCount != yieldsBefore) throw Error($"'yield' inside {kind}", start);
        }

        private List<ComprehensionClause> ParseComprehensionClauses(bool generator = false)
        {
            _comprehensionDepth++;
            try { return ReadComprehensionClauses(generator); }
            finally { _comprehensionDepth--; }
        }

        private List<ComprehensionClause> ReadComprehensionClauses(bool generator)
        {
            var clauses = new List<ComprehensionClause>();
            while (AtComprehensionClause())
            {
                var start = Take();
                bool isAsync = start.Text == "async";
                if (isAsync)
                {
                    Need("for");
                    if (generator) throw Error("async generator expressions are not supported", start);
                    if (!_inAsyncFunction)
                        throw Error("asynchronous comprehension outside of an asynchronous function", start);
                    _awaitCount++;
                }

                int awaitsBefore = _awaitCount;
                var target = ExpressionList(5, "in");
                ValidateAssignmentTarget(target, start);
                Need("in");
                if (generator && _awaitCount != awaitsBefore)
                    throw Error("async generator expressions are not supported", start);

                // At this level 'if' starts a filter. Parentheses can still enclose a conditional expression.
                var iterable = Expression(1);
                // The outer iterable of a generator expression runs immediately in the enclosing scope.
                if (clauses.Count == 0) awaitsBefore = _awaitCount;
                var filters = new List<ExpressionSyntax>();
                while (Match("if")) filters.Add(Expression(1));
                if (generator && _awaitCount != awaitsBefore)
                    throw Error("async generator expressions are not supported", start);
                clauses.Add(Located(new ComprehensionClause(target, iterable, filters) { IsAsync = isAsync }, start.Line, start.Column));
            }
            return clauses;
        }

        private static bool IsComparisonOperator(string op) => op is "==" or "!=" or "<" or ">" or "<=" or ">=" or "in" or "not in" or "is" or "is not";

        private static int Precedence(string op) => op switch
        {
            "or" => 1, "and" => 2, "not" => 3, "==" or "!=" or "<" or ">" or "<=" or ">=" or "in" or "is" or "is not" or "not in" => 4,
            "|" => 5, "^" => 6, "&" => 7, "<<" or ">>" => 8, "+" or "-" => 9, "*" or "@" or "/" or "//" or "%" => 10, "**" => 11, _ => -1
        };

        private static bool IsAugmentedAssignmentTarget(ExpressionSyntax target) => target is NameExpression or AttributeExpression or SubscriptExpression;
        private void ValidateAssignmentTarget(ExpressionSyntax target, PyToken location)
        {
            if (target is NameExpression or AttributeExpression or SubscriptExpression) return;
            IReadOnlyList<ExpressionSyntax> items = target switch
            {
                TupleExpression tuple => tuple.Items,
                ListExpression list => list.Items,
                _ => throw Error("cannot assign to expression", location)
            };
            bool foundStar = false;
            foreach (var item in items)
            {
                if (item is StarredExpression star)
                {
                    if (foundStar) throw Error("multiple starred expressions in assignment", location);
                    foundStar = true;
                    ValidateAssignmentTarget(star.Value, location);
                }
                else ValidateAssignmentTarget(item, location);
            }
        }

        private void RequireUnstarred(ExpressionSyntax value)
        {
            if (value is StarredExpression)
                throw new PythonException("SyntaxError", "can't use starred expression here", value.Line, value.Column, fileName);
        }

        private string NeedName()
        {
            if (Current.Kind != TokenKind.Name || Is("yield") || Is("async") || Is("await")) throw Error("expected identifier");
            return Take().Text;
        }
        private bool IsLineEnd() => AtStatementBoundary() || Is(";");
        private bool AtStatementBoundary() => Current.Kind is TokenKind.Newline or TokenKind.End or TokenKind.Dedent;
        private void SkipNewlines() { while (Current.Kind == TokenKind.Newline) Take(); }
        private void NeedKind(TokenKind kind) { if (Current.Kind != kind) throw Error($"expected {kind}"); Take(); }
        private PythonException Error(string message, PyToken token) => new("SyntaxError", message, token.Line, token.Column, fileName);
        private PythonException Error(string message) => Error(message, Current);
        private static T Located<T>(T node, int line, int column) where T : SyntaxNode => (T)(node with { Line = line, Column = column });
    }
}
