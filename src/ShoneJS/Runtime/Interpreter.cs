using ShoneJS.Parsing;

namespace ShoneJS.Runtime;

public sealed class Interpreter
{
    private EnvironmentRecord _env = new();
    public int MaximumLoopIterations { get; set; } = 1_000_000;
    private sealed class ReturnSignal(JsValue value) : Exception { public JsValue Value { get; } = value; }
    public Interpreter() { _env.Declare("print", JsValue.From(new JsFunction(a => { Console.WriteLine(a.Length == 0 ? "" : a[0].ToJsString()); return JsValue.Undefined; }))); }
    public void SetGlobal(string name, JsValue value) { try { _env.Set(name, value); } catch (InvalidOperationException) { _env.Declare(name, value); } }
    public JsValue GetGlobal(string name) => _env.Get(name);
    public JsValue Execute(List<Stmt> statements) { var result = JsValue.Undefined; foreach (var s in statements) result = Exec(s); return result; }
    private JsValue Exec(Stmt s) => s switch {
        BlockStmt b => Block(b), VarStmt v => Declare(v), FunctionStmt f => DefineFunction(f), ExprStmt e => Eval(e.Expression),
        IfStmt i => Eval(i.Test).ToBoolean() ? Exec(i.Then) : i.Else is null ? JsValue.Undefined : Exec(i.Else),
        WhileStmt w => While(w), ReturnStmt r => throw new ReturnSignal(r.Value is null ? JsValue.Undefined : Eval(r.Value)),
        ThrowStmt t => throw new InvalidOperationException(Eval(t.Value).ToJsString()), _ => JsValue.Undefined
    };
    private JsValue Block(BlockStmt b) { var old = _env; _env = new EnvironmentRecord(old); try { var v = JsValue.Undefined; foreach (var x in b.Body) v = Exec(x); return v; } finally { _env = old; } }
    private JsValue Declare(VarStmt v) { var value = v.Init is null ? JsValue.Undefined : Eval(v.Init); _env.Declare(v.Name, value); return value; }
    private JsValue DefineFunction(FunctionStmt f) { _env.Declare(f.Name, MakeFunction(f.Parameters, f.Body, _env)); return _env.Get(f.Name); }
    private JsValue While(WhileStmt w) { var v = JsValue.Undefined; var guard = 0; while (Eval(w.Test).ToBoolean()) { v = Exec(w.Body); if (++guard > MaximumLoopIterations) throw new InvalidOperationException("Execution limit exceeded"); } return v; }
    private JsValue MakeFunction(List<string> p, BlockStmt body, EnvironmentRecord closure) => JsValue.From(new JsFunction(args => { var old = _env; _env = new EnvironmentRecord(closure); for (var i = 0; i < p.Count; i++) _env.Declare(p[i], i < args.Length ? args[i] : JsValue.Undefined); try { Exec(body); return JsValue.Undefined; } catch (ReturnSignal r) { return r.Value; } finally { _env = old; } }, p.Count));
    private JsValue Eval(Expr e) => e switch {
        LiteralExpr l => l.Value switch { null => JsValue.Null, string s => JsValue.From(s), bool b => JsValue.From(b), double n => JsValue.From(n), JsValue v => v, _ => JsValue.Undefined },
        TemplateExpr t => Interpolate(t.Text),
        IdentifierExpr i => _env.Get(i.Name), UnaryExpr u => Unary(u), BinaryExpr b => Binary(b), AssignExpr a => Assign(a), MemberExpr m => GetMember(m),
        CallExpr c => Call(c), FunctionExpr f => MakeFunction(f.Parameters, f.Body, _env), ArrayExpr a => MakeArray(a), ObjectExpr o => MakeObject(o), ConditionalExpr c => Eval(c.Test).ToBoolean() ? Eval(c.Then) : Eval(c.Else), _ => JsValue.Undefined
    };
    private JsValue Interpolate(string text)
    {
        var result = new System.Text.StringBuilder(); var position = 0;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"\$\{([^{}]*)\}"))
        {
            result.Append(text, position, match.Index - position);
            var expression = new Parser(new Lexer(match.Groups[1].Value).Scan()).ParseExpression();
            result.Append(Eval(expression).ToJsString()); position = match.Index + match.Length;
        }
        result.Append(text, position, text.Length - position); return JsValue.From(result.ToString());
    }
    private JsValue Unary(UnaryExpr u) { var v = Eval(u.Value); return u.Op switch { TokenKind.Bang => JsValue.From(!v.ToBoolean()), TokenKind.Minus => JsValue.From(-v.ToNumber()), _ => JsValue.From(v.ToNumber()) }; }
    private JsValue Binary(BinaryExpr b) { var l = Eval(b.Left); if (b.Op == TokenKind.AndAnd) return l.ToBoolean() ? Eval(b.Right) : l; if (b.Op == TokenKind.OrOr) return l.ToBoolean() ? l : Eval(b.Right); var r = Eval(b.Right); if (b.Op == TokenKind.Plus && (l.Type == JsType.String || r.Type == JsType.String)) return JsValue.From(l.ToJsString() + r.ToJsString()); var x = l.ToNumber(); var y = r.ToNumber(); return b.Op switch { TokenKind.Plus => JsValue.From(x + y), TokenKind.Minus => JsValue.From(x - y), TokenKind.Star => JsValue.From(x * y), TokenKind.Slash => JsValue.From(x / y), TokenKind.Percent => JsValue.From(x % y), TokenKind.EqualEqual or TokenKind.StrictEqual => JsValue.From(l.Type == r.Type && l.ToJsString() == r.ToJsString() || l.Type != r.Type && x == y), TokenKind.NotEqual or TokenKind.StrictNotEqual => JsValue.From(!(l.Type == r.Type && l.ToJsString() == r.ToJsString() || l.Type != r.Type && x == y)), TokenKind.Less => JsValue.From(x < y), TokenKind.LessEqual => JsValue.From(x <= y), TokenKind.Greater => JsValue.From(x > y), TokenKind.GreaterEqual => JsValue.From(x >= y), _ => JsValue.Undefined }; }
    private JsValue Assign(AssignExpr a) { var v = Eval(a.Value); if (a.Target is IdentifierExpr i) _env.Set(i.Name, v); else if (a.Target is MemberExpr m && Eval(m.Target).Data is JsObject o) o.Set(Key(Eval(m.Key)), v); else throw new InvalidOperationException("Invalid assignment target"); return v; }
    private JsValue GetMember(MemberExpr m) { var target = Eval(m.Target); if (target.Data is not JsObject o) throw new InvalidOperationException("Cannot access property of non-object"); return o.Get(Key(Eval(m.Key))); }
    private static string Key(JsValue v) => v.ToJsString();
    private JsValue Call(CallExpr c) { var fn = Eval(c.Target); if (fn.Data is not JsFunction f) throw new InvalidOperationException("Value is not callable"); return f.Call(c.Arguments.Select(Eval).ToArray()); }
    private JsValue MakeArray(ArrayExpr a) { var o = new JsArray(); foreach (var e in a.Items) o.Add(Eval(e)); return JsValue.From(o); }
    private JsValue MakeObject(ObjectExpr e) { var o = new JsObject(); foreach (var p in e.Properties) o.Set(p.Key, Eval(p.Value)); return JsValue.From(o); }
}
