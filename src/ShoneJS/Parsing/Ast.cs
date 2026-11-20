using Sdcb.Arithmetic;

namespace ShoneJS.Parsing;

public abstract record Stmt
{
    public SourceSpan Span { get; init; }
}

public sealed record BlockStmt(List<Stmt> Body) : Stmt
{
    public bool IsStrict { get; init; }
}

public sealed record VarStmt(TokenKind Kind, string Name, Expr? Init) : Stmt
{
    public TypeNode? TypeAnnotation { get; init; }
}
public sealed record VarListStmt(List<VarStmt> Declarations) : Stmt;
public sealed record FunctionStmt(string Name, List<string> Parameters, BlockStmt Body) : Stmt
{
    public IReadOnlyList<TypeNode?> ParameterTypes { get; init; } = [];
    public IReadOnlyList<TypeParameter> TypeParameters { get; init; } = [];
    public TypeNode? ReturnType { get; init; }
}
public sealed record ExprStmt(Expr Expression) : Stmt;
public sealed record IfStmt(Expr Test, Stmt Then, Stmt? Else) : Stmt;
public sealed record WhileStmt(Expr Test, Stmt Body) : Stmt;
public sealed record DoWhileStmt(Expr Test, Stmt Body) : Stmt;
public sealed record ForStmt(Stmt? Init, Expr? Test, Expr? Update, Stmt Body) : Stmt;
public sealed record SwitchStmt(Expr Discriminant, List<SwitchCase> Cases) : Stmt;
public sealed record SwitchCase(Expr? Test, List<Stmt> Body)
{
    public SourceSpan Span { get; init; }
}
public sealed record BreakStmt : Stmt;
public sealed record ContinueStmt : Stmt;
public sealed record ReturnStmt(Expr? Value) : Stmt;
public sealed record ThrowStmt(Expr Value) : Stmt;
public sealed record TryStmt(BlockStmt Body, string? CatchName, BlockStmt? CatchBody, BlockStmt? FinallyBody) : Stmt;

// Expressions retain source spans and parenthesization because both diagnostics and a few
// JavaScript grammar restrictions (notably '**' and nullish/logical mixing) depend on them.
public abstract record Expr
{
    public SourceSpan Span { get; init; }
    internal bool IsParenthesized { get; init; }
}

public sealed record LiteralExpr(object? Value) : Expr;
public sealed record BigIntExpr(BigInt Value) : Expr;
public sealed record TemplateElement(string Raw, string Cooked, SourceSpan Span);
public sealed record TemplateExpr(IReadOnlyList<TemplateElement> Quasis, IReadOnlyList<Expr> Expressions) : Expr
{
    public TemplateExpr(string text) : this([new TemplateElement(text, text, default)], []) { }
    public string Text => string.Concat(Quasis.Select(quasi => quasi.Cooked));
}
public sealed record IdentifierExpr(string Name) : Expr;
public sealed record ThisExpr : Expr;
public sealed record UnaryExpr(TokenKind Op, Expr Value) : Expr;
public sealed record BinaryExpr(Expr Left, TokenKind Op, Expr Right) : Expr;
public sealed record AssignExpr(Expr Target, Expr Value) : Expr;
public sealed record CompoundAssignExpr(Expr Target, TokenKind Op, Expr Value) : Expr;
public sealed record UpdateExpr(Expr Target, TokenKind Op, bool Prefix) : Expr;
public sealed record MemberExpr(Expr Target, Expr Key) : Expr;
public sealed record CallExpr(Expr Target, List<Expr> Arguments) : Expr
{
    public IReadOnlyList<TypeNode>? TypeArguments { get; init; }
}
// Optional chaining is represented as segments so each '.', '[]', and call can retain
// whether that individual hop was optional.
public sealed record OptionalChainExpr(Expr Target, IReadOnlyList<OptionalChainSegment> Segments) : Expr;
public abstract record OptionalChainSegment(bool Optional);
public sealed record ChainMember(Expr Key, bool Optional) : OptionalChainSegment(Optional);
public sealed record ChainCall(List<Expr> Arguments, bool Optional) : OptionalChainSegment(Optional);
public sealed record NewExpr(Expr Target, List<Expr> Arguments) : Expr;
public sealed record ArrowParameter(string Name, TypeNode? Type = null);
public sealed record ArrowFunctionExpr(IReadOnlyList<ArrowParameter> Parameters, Expr? ExpressionBody, BlockStmt? BlockBody) : Expr
{
    public IReadOnlyList<TypeParameter> TypeParameters { get; init; } = [];
    public TypeNode? ReturnType { get; init; }
}
public sealed record FunctionExpr(List<string> Parameters, BlockStmt Body) : Expr
{
    public string? Name { get; init; }
    public bool IsMethod { get; init; }
    public IReadOnlyList<TypeNode?> ParameterTypes { get; init; } = [];
    public IReadOnlyList<TypeParameter> TypeParameters { get; init; } = [];
    public TypeNode? ReturnType { get; init; }
}
public sealed record ArrayExpr(List<Expr> Items) : Expr;
public sealed record ArrayHoleExpr : Expr;
public sealed record ObjectExpr(List<(string Key, Expr Value)> Properties) : Expr;
public sealed record ConditionalExpr(Expr Test, Expr Then, Expr Else) : Expr;
public sealed record SequenceExpr(List<Expr> Expressions) : Expr;
