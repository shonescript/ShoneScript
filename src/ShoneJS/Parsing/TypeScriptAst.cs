namespace ShoneJS.Parsing;

// Type nodes are preserved in the AST for tooling/analysis; they describe TypeScript syntax
// and are not runtime JavaScript expressions.
public abstract record TypeNode
{
    public SourceSpan Span { get; init; }
}

public sealed record NamedTypeNode(string Name, IReadOnlyList<TypeNode>? TypeArguments = null) : TypeNode;
public sealed record UnionTypeNode(IReadOnlyList<TypeNode> Types) : TypeNode;
public sealed record IntersectionTypeNode(IReadOnlyList<TypeNode> Types) : TypeNode;
public sealed record TupleTypeNode(IReadOnlyList<TypeNode> Elements) : TypeNode;
public sealed record ArrayTypeNode(TypeNode Element) : TypeNode;
public sealed record ParenthesizedTypeNode(TypeNode Inner) : TypeNode;
public sealed record ObjectTypeNode(IReadOnlyList<TypeMember> Members) : TypeNode;
public sealed record TypeMember(string Name, TypeNode Type, bool Optional = false);
public sealed record TypeParameter(string Name, TypeNode? Constraint = null, TypeNode? Default = null);
public sealed record TypeAliasStmt(string Name, TypeNode Type) : Stmt
{
    public IReadOnlyList<TypeParameter> TypeParameters { get; init; } = [];
}
public sealed record InterfaceStmt(string Name, IReadOnlyList<TypeMember> Members) : Stmt
{
    public IReadOnlyList<TypeParameter> TypeParameters { get; init; } = [];
    public IReadOnlyList<TypeNode> BaseTypes { get; init; } = [];
}
public sealed record EnumMember(string Name, Expr? Initializer);
public sealed record EnumStmt(string Name, IReadOnlyList<EnumMember> Members, bool IsConst = false) : Stmt;
public sealed record TypeAssertionExpr(Expr Expression, TypeNode Type) : Expr;
public sealed record SatisfiesExpr(Expr Expression, TypeNode Type) : Expr;
public sealed record NonNullExpr(Expr Expression) : Expr;
