using ShoneJs.Parsing;
using ShoneJs.Runtime;

namespace ShoneHtml;

internal sealed partial class HtmlScriptHost
{
    private void BindDynamicFunction()
    {
        Constructor("Function", 1, args =>
        {
            if (args.Length == 0) return _engine.Evaluate("(function anonymous(){})");
            var parameters = args.Length < 2 ? "" : string.Join(',', args[..^1].Select(value => value.ToJsString()));
            var body = args[^1].ToJsString();
            var source = $"(function anonymous({parameters}){{{body}\n}})";
            if (source.Length > (Renderer?.Options.MaximumResponseBytes ?? 1_048_576))
                throw BudgetError("Function constructor source limit exceeded");
            try { return _engine.Evaluate(source, _page.Lifetime); }
            catch (JsSyntaxException error) { throw new JsException(Error("SyntaxError", error.Message)); }
        }, callable: true);
    }
}
