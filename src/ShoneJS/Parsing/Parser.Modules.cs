// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShoneJs.Parsing;

internal sealed record ModuleImport(string Specifier, string Imported, string Local, bool Namespace = false);
internal sealed record ModuleExport(string? Exported, string? Local = null, string? Specifier = null, string? Imported = null, bool Namespace = false);

public sealed partial class Parser
{
    private const string DefaultBinding = "*default*";
    private bool Contextual(string text) => Current.Kind == TokenKind.Identifier && Current.Text == text;
    private bool MatchContextual(string text)
    {
        if (!Contextual(text)) return false;
        Take();
        return true;
    }

    private void NeedContextual(string text)
    {
        if (!MatchContextual(text)) throw Error($"Expected '{text}'", Current);
    }

    private ImportMetaExpr ParseImportMeta()
    {
        NeedContextual("meta");
        return new ImportMetaExpr();
    }

    internal ParsedModule ParseModule()
    {
        cancellationToken.ThrowIfCancellationRequested();
        var statements = new List<Stmt>();
        var imports = new List<ModuleImport>();
        var exports = new List<ModuleExport>();
        var requests = new List<string>();
        var requested = new HashSet<string>(StringComparer.Ordinal);
        while (Current.Kind != TokenKind.End)
        {
            if (Contextual("import") && PeekToken(1).Kind != TokenKind.Dot)
            {
                Take();
                ImportDeclaration(imports);
            }
            else if (MatchContextual("export")) ExportDeclaration(statements, exports);
            else statements.Add(Statement());
        }

        void ImportDeclaration(List<ModuleImport> entries)
        {
            if (Current.Kind == TokenKind.String)
            {
                Request();
                Semicolon();
                return;
            }
            List<(string Imported, string Local, bool Namespace)> names = [];
            var hasDefault = Current.Kind == TokenKind.Identifier;
            if (hasDefault) names.Add(("default", LocalName().Text, false));
            if (!hasDefault || Match(TokenKind.Comma))
            {
                if (Match(TokenKind.Star))
                {
                    NeedContextual("as");
                    names.Add(("", LocalName().Text, true));
                }
                else
                {
                    Need(TokenKind.LeftBrace);
                    while (Current.Kind != TokenKind.RightBrace)
                    {
                        var imported = ExportName();
                        Token local;
                        if (MatchContextual("as")) local = LocalName();
                        else
                        {
                            if (imported.Kind != TokenKind.Identifier) throw Error("Expected an import alias", imported);
                            ValidateBinding(imported);
                            local = imported;
                        }
                        names.Add((imported.Text, local.Text, false));
                        if (!Match(TokenKind.Comma)) break;
                    }
                    Need(TokenKind.RightBrace);
                }
            }
            NeedContextual("from");
            var specifier = Request();
            foreach (var (imported, local, ns) in names) entries.Add(new(specifier, imported, local, ns));
            Semicolon();
        }

        void ExportDeclaration(List<Stmt> body, List<ModuleExport> entries)
        {
            if (Match(TokenKind.Default))
            {
                if (Current.Kind == TokenKind.Function || AsyncFunctionAhead)
                {
                    var function = FunctionDeclaration(DefaultBinding);
                    body.Add(function);
                    entries.Add(new("default", function.Name));
                    return;
                }
                var expr = Expression(1);
                body.Add(new VarStmt(TokenKind.Const, DefaultBinding, expr) { Span = expr.Span });
                entries.Add(new("default", DefaultBinding));
                Semicolon();
                return;
            }
            if (Match(TokenKind.Star))
            {
                var name = MatchContextual("as") ? ExportName().Text : null;
                NeedContextual("from");
                entries.Add(new(name, Specifier: Request(), Namespace: name is not null));
                Semicolon();
                return;
            }
            if (Match(TokenKind.LeftBrace))
            {
                var aliases = new List<(Token Local, Token Exported)>();
                while (Current.Kind != TokenKind.RightBrace)
                {
                    var local = ExportName();
                    aliases.Add((local, MatchContextual("as") ? ExportName() : local));
                    if (!Match(TokenKind.Comma)) break;
                }
                Need(TokenKind.RightBrace);
                var specifier = MatchContextual("from") ? Request() : null;
                foreach (var (local, exported) in aliases)
                {
                    if (specifier is null && local.Kind != TokenKind.Identifier) throw Error("Expected a local binding", local);
                    entries.Add(specifier is null ? new(exported.Text, local.Text) : new(exported.Text, Specifier: specifier, Imported: local.Text));
                }
                Semicolon();
                return;
            }
            if (Current.Kind is not (TokenKind.Const or TokenKind.Let or TokenKind.Var or TokenKind.Function) && !AsyncFunctionAhead)
                throw Error("Expected an export declaration", Current);
            var declaration = Statement();
            body.Add(declaration);
            if (declaration is FunctionStmt functionDeclaration) entries.Add(new(functionDeclaration.Name, functionDeclaration.Name));
            else foreach (var variable in DirectDeclarations([declaration])) entries.Add(new(variable.Name, variable.Name));
        }

        string Request()
        {
            var source = Need(TokenKind.String);
            ValidateString(source);
            if (requested.Add(source.Text)) requests.Add(source.Text);
            return source.Text;
        }

        ValidateDeclarations(statements, functionsAreLexical: true);
        var locals = new HashSet<string>(LexicalNames(statements, functionsAreLexical: true).Select(binding => binding.Name), StringComparer.Ordinal);
        locals.UnionWith(VarDeclaredNames(statements, includeFunctions: false).Select(binding => binding.Name));
        foreach (var import in imports)
            if (!locals.Add(import.Local)) throw Error($"Duplicate binding '{import.Local}'", Current);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var export in exports)
        {
            if (export.Exported is { } name && !names.Add(name)) throw Error($"Duplicate export '{name}'", Current);
            if (export.Local is { } local && !locals.Contains(local)) throw Error($"Export '{local}' is not declared", Current);
        }
        return new ParsedModule(statements, imports, exports, requests);
    }

    private Token LocalName()
    {
        var token = Need(TokenKind.Identifier);
        ValidateBinding(token);
        return token;
    }

    private Token ExportName()
    {
        var token = Take();
        if (token.Kind == TokenKind.String) ValidateString(token);
        else if (!IsIdentifierName(token)) throw Error("Expected an import or export name", token);
        return token;
    }
}
