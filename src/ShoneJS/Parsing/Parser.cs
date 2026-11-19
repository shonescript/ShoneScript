using ShoneJS.Runtime;

namespace ShoneJS.Parsing;

public sealed class Parser(IReadOnlyList<Token> tokens)
{
    private int _i;
    private Token Current => tokens[Math.Min(_i, tokens.Count - 1)];
    private Token Take() => tokens[_i++];
    private bool Match(TokenKind k) { if (Current.Kind != k) return false; Take(); return true; }
    private Token Need(TokenKind k) { if (Current.Kind != k) throw Error($"Expected {k}"); return Take(); }
    private JsSyntaxException Error(string message) => new(message, Current.Line, Current.Column);
    public List<Stmt> Parse() { var s = new List<Stmt>(); while (Current.Kind != TokenKind.End) s.Add(Statement()); return s; }
    public Expr ParseExpression() { var expression = Expression(); if (Current.Kind != TokenKind.End) throw Error("Unexpected token after expression"); return expression; }
    private Stmt Statement()
    {
        if (Match(TokenKind.Semicolon)) return new ExprStmt(new LiteralExpr(null));
        if (Match(TokenKind.LeftBrace)) { var body = new List<Stmt>(); while (Current.Kind != TokenKind.RightBrace && Current.Kind != TokenKind.End) body.Add(Statement()); Need(TokenKind.RightBrace); return new BlockStmt(body); }
        if (Current.Kind is TokenKind.Let or TokenKind.Const or TokenKind.Var) { var kind = Take().Kind; var name = Need(TokenKind.Identifier).Text; Expr? init = Match(TokenKind.Equal) ? Expression() : null; if (kind == TokenKind.Const && init is null) throw Error("const declaration requires an initializer"); Match(TokenKind.Semicolon); return new VarStmt(kind, name, init); }
        if (Match(TokenKind.Function)) { var name = Need(TokenKind.Identifier).Text; var args = Parameters(); return new FunctionStmt(name, args, (BlockStmt)Statement()); }
        if (Match(TokenKind.Return)) { Expr? value = Current.Kind is TokenKind.Semicolon or TokenKind.RightBrace or TokenKind.End ? null : Expression(); Match(TokenKind.Semicolon); return new ReturnStmt(value); }
        if (Match(TokenKind.Throw)) { var value = Expression(); Match(TokenKind.Semicolon); return new ThrowStmt(value); }
        if (Match(TokenKind.If)) { Need(TokenKind.LeftParen); var test = Expression(); Need(TokenKind.RightParen); var then = Statement(); return new IfStmt(test, then, Match(TokenKind.Else) ? Statement() : null); }
        if (Match(TokenKind.While)) { Need(TokenKind.LeftParen); var test = Expression(); Need(TokenKind.RightParen); return new WhileStmt(test, Statement()); }
        var expr = Expression(); Match(TokenKind.Semicolon); return new ExprStmt(expr);
    }
    private List<string> Parameters() { Need(TokenKind.LeftParen); var p = new List<string>(); if (Current.Kind != TokenKind.RightParen) do p.Add(Need(TokenKind.Identifier).Text); while (Match(TokenKind.Comma)); Need(TokenKind.RightParen); return p; }
    private Expr Expression(int min = 0)
    {
        Expr left;
        var t = Take();
        left = t.Kind switch {
            TokenKind.Number => new LiteralExpr(double.Parse(t.Text, System.Globalization.CultureInfo.InvariantCulture)), TokenKind.String => new LiteralExpr(t.Text), TokenKind.Template => new TemplateExpr(t.Text), TokenKind.True => new LiteralExpr(true), TokenKind.False => new LiteralExpr(false), TokenKind.Null => new LiteralExpr(null), TokenKind.Undefined => new LiteralExpr(JsValue.Undefined), TokenKind.Identifier => new IdentifierExpr(t.Text),
            TokenKind.Bang or TokenKind.Minus or TokenKind.Plus => new UnaryExpr(t.Kind, Expression(12)),
            TokenKind.LeftParen => Parenthesized(), TokenKind.LeftBracket => ArrayLiteral(), TokenKind.LeftBrace => ObjectLiteral(),
            TokenKind.Function => new FunctionExpr(Parameters(), (BlockStmt)Statement()),
            _ => throw new JsSyntaxException("Expected expression", t.Line, t.Column)
        };
        while (true)
        {
            if (min <= 14 && Match(TokenKind.Dot)) { left = new MemberExpr(left, new LiteralExpr(Need(TokenKind.Identifier).Text)); continue; }
            if (min <= 14 && Match(TokenKind.LeftBracket)) { var key = Expression(); Need(TokenKind.RightBracket); left = new MemberExpr(left, key); continue; }
            if (min <= 14 && Match(TokenKind.LeftParen)) { var a = new List<Expr>(); if (Current.Kind != TokenKind.RightParen) do a.Add(Expression()); while (Match(TokenKind.Comma)); Need(TokenKind.RightParen); left = new CallExpr(left, a); continue; }
            if (min <= 2 && Match(TokenKind.Question)) { var y = Expression(); Need(TokenKind.Colon); left = new ConditionalExpr(left, y, Expression(2)); continue; }
            var op = Current.Kind; var prec = Precedence(op); if (prec < min) break; Take(); var right = Expression(prec + (op == TokenKind.Equal ? 0 : 1)); left = op == TokenKind.Equal ? new AssignExpr(left, right) : new BinaryExpr(left, op, right);
        }
        return left;
    }
    private Expr Parenthesized() { var x = Expression(); Need(TokenKind.RightParen); return x; }
    private Expr ArrayLiteral() { var a = new List<Expr>(); if (Current.Kind != TokenKind.RightBracket) do a.Add(Expression()); while (Match(TokenKind.Comma)); Need(TokenKind.RightBracket); return new ArrayExpr(a); }
    private Expr ObjectLiteral() { var p = new List<(string, Expr)>(); if (Current.Kind != TokenKind.RightBrace) do { var k = Take(); if (k.Kind is not (TokenKind.Identifier or TokenKind.String or TokenKind.Number)) throw new JsSyntaxException("Expected property name", k.Line, k.Column); Need(TokenKind.Colon); p.Add((k.Text, Expression())); } while (Match(TokenKind.Comma)); Need(TokenKind.RightBrace); return new ObjectExpr(p); }
    private static int Precedence(TokenKind k) => k switch { TokenKind.Equal => 1, TokenKind.OrOr => 3, TokenKind.AndAnd => 4, TokenKind.EqualEqual or TokenKind.StrictEqual or TokenKind.NotEqual or TokenKind.StrictNotEqual => 7, TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual => 8, TokenKind.Plus or TokenKind.Minus => 10, TokenKind.Star or TokenKind.Slash or TokenKind.Percent => 11, _ => -1 };
}
