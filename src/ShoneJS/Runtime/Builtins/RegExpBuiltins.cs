// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ShoneJs.Runtime.Builtins;

internal sealed class RegExpBuiltins
{
    private const int MaximumPattern = 4_096, MaximumInput = 262_144, MaximumOutput = 1_048_576;
    private const int MaximumMatches = 32_768, MaximumCaptures = 128;
    private const string SupportedFlags = "gimsy";
    private const string LineTerminators = @"\r\n\u2028\u2029";
    private const string WhiteSpace = @"\x09-\x0D\x20\xA0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF";
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromMilliseconds(100);

    internal sealed record Definition(string Pattern, string Flags, Regex Regex)
    {
        internal bool Global => Flags.Contains('g');
        internal bool Sticky => Flags.Contains('y');
    }

    private sealed class Instance : JsObject
    {
        internal readonly Definition Definition;
        internal Instance(Definition definition, JsObject prototype)
        {
            Definition = definition;
            Prototype = prototype;
            DefineOwnProperty("lastIndex", PropertyDescriptor.Data(JsValue.From(0d), writable: true));
        }
    }

    internal static bool IsRegExpInstance(JsValue value) => value.Data is Instance;

    private readonly JsObject _prototype;
    private readonly JsArray _arrayPrototype;

    internal RegExpBuiltins(Action<string, JsValue> setGlobal, JsObject objectPrototype, JsArray arrayPrototype)
    {
        _prototype = new JsObject { Prototype = objectPrototype };
        _arrayPrototype = arrayPrototype;
        JsFunction constructor = null!;
        constructor = new JsFunction((_, args) => Construct(args, false), 2)
        {
            Prototype = objectPrototype,
            NativeConstruct = args => Construct(args, true)
        };
        constructor.DefineOwnProperty("name", PropertyDescriptor.Data(JsValue.From("RegExp"), configurable: true));
        constructor.DefineOwnProperty("prototype", PropertyDescriptor.Data(JsValue.From(_prototype)));
        var escape = new JsFunction((_, args) => Escape(At(args, 0)), 1) { Prototype = objectPrototype };
        escape.DefineOwnProperty("name", PropertyDescriptor.Data(JsValue.From("escape"), configurable: true));
        constructor.DefineOwnProperty("escape", PropertyDescriptor.Data(JsValue.From(escape), writable: true, configurable: true));
        _prototype.DefineOwnProperty("constructor", PropertyDescriptor.Data(JsValue.From(constructor), writable: true, configurable: true));
        var builtinExec = AddMethod("exec", 1, (receiver, args) =>
        {
            var regex = Require(receiver);
            var input = Text(At(args, 0));
            return ExecResult(Find(regex, input, new ScanBudget(input)), input);
        });
        AddMethod("test", 1, (receiver, args) =>
        {
            if (receiver.Data is not JsObject obj) throw JsException.TypeError("RegExp.prototype.test requires an object");
            var input = Text(At(args, 0));
            if (obj.Get("exec", receiver).Data is JsFunction exec && !ReferenceEquals(exec, builtinExec))
            {
                var result = exec.Call(receiver, [JsValue.From(input)]);
                if (result.Type != JsType.Null && result.Data is not JsObject)
                    throw JsException.TypeError("RegExp exec must return an object or null");
                return JsValue.From(result.Type != JsType.Null);
            }
            return JsValue.From(Find(Require(receiver), input, new ScanBudget(input)) is not null);
        });
        AddMethod("toString", 0, (receiver, _) => receiver.Data is JsObject obj
            ? JsValue.From($"/{Text(obj.Get("source", receiver))}/{Text(obj.Get("flags", receiver))}")
            : throw JsException.TypeError("RegExp.prototype.toString requires an object"));
        AddGetter("source", receiver => JsValue.From(ReferenceEquals(receiver.Data, _prototype) ? "(?:)" : EscapeSource(Require(receiver).Definition.Pattern)));
        AddGetter("flags", receiver =>
        {
            if (receiver.Data is not JsObject obj) throw JsException.TypeError("RegExp.prototype.flags requires an object");
            var flags = new StringBuilder(SupportedFlags.Length);
            foreach (var (name, flag) in FlagProperties)
                if (obj.Get(name, receiver).ToBoolean()) flags.Append(flag);
            return JsValue.From(flags.ToString());
        });
        foreach (var (name, flag) in FlagProperties)
            AddGetter(name, receiver => ReferenceEquals(receiver.Data, _prototype) ? JsValue.Undefined
                : JsValue.From(Require(receiver).Definition.Flags.Contains(flag)));
        setGlobal("RegExp", JsValue.From(constructor));

        JsValue Construct(JsValue[] args, bool newInstance)
        {
            var pattern = At(args, 0);
            var flags = At(args, 1);
            if (pattern.Data is Instance original && flags.Type == JsType.Undefined)
            {
                if (!newInstance && ReferenceEquals(original.Get("constructor").Data, constructor)) return pattern;
                return CreateLiteral(original.Definition);
            }
            return Create(pattern.Data is Instance regex ? regex.Definition.Pattern : pattern.Type == JsType.Undefined ? "" : Text(pattern),
                flags.Type == JsType.Undefined ? "" : Text(flags));
        }
    }

    private static JsValue Escape(JsValue value)
    {
        if (value.Data is not string text) throw JsException.TypeError("RegExp.escape requires a string");
        if (text.Length > MaximumOutput) throw JsException.RangeError("RegExp.escape input limit exceeded");
        Interpreter.ChargeNativeWork(text.Length);
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (i == 0 && char.IsAsciiLetterOrDigit(ch)) AppendHex(ch);
            else if ("^$\\.*+?()[]{}|/".Contains(ch)) result.Append('\\').Append(ch);
            else if (",-=<>#&!%:;@~'`\"".Contains(ch) || ch == ' ') AppendHex(ch);
            else if (ch == '\f') result.Append(@"\f");
            else if (ch == '\n') result.Append(@"\n");
            else if (ch == '\r') result.Append(@"\r");
            else if (ch == '\t') result.Append(@"\t");
            else if (ch == '\v') result.Append(@"\v");
            else if (i + 1 < text.Length && char.IsSurrogatePair(ch, text[i + 1])) result.Append(ch).Append(text[++i]);
            else if (char.IsSurrogate(ch) || char.GetUnicodeCategory(ch) == UnicodeCategory.SpaceSeparator || ch is '\uFEFF' or '\u2028' or '\u2029') AppendHex(ch);
            else result.Append(ch);
            if (result.Length > MaximumOutput) throw JsException.RangeError("RegExp.escape output limit exceeded");
        }
        return JsValue.From(result.ToString());

        void AppendHex(char ch)
        {
            result.Append(ch <= '\u00FF' ? @"\x" : @"\u");
            result.Append(((int)ch).ToString(ch <= '\u00FF' ? "x2" : "x4", CultureInfo.InvariantCulture));
        }
    }

    private static readonly (string Name, char Flag)[] FlagProperties =
        [("global", 'g'), ("ignoreCase", 'i'), ("multiline", 'm'), ("dotAll", 's'), ("sticky", 'y')];

    private JsFunction AddMethod(string name, int arity, Func<JsValue, JsValue[], JsValue> implementation)
    {
        var function = new JsFunction(implementation, arity) { Prototype = _prototype.Prototype };
        function.DefineOwnProperty("name", PropertyDescriptor.Data(JsValue.From(name), configurable: true));
        _prototype.DefineOwnProperty(name, PropertyDescriptor.Data(JsValue.From(function), writable: true, configurable: true));
        return function;
    }

    private void AddGetter(string name, Func<JsValue, JsValue> implementation)
    {
        var getter = new JsFunction((receiver, _) => implementation(receiver)) { Prototype = _prototype.Prototype };
        getter.DefineOwnProperty("name", PropertyDescriptor.Data(JsValue.From($"get {name}"), configurable: true));
        _prototype.DefineOwnProperty(name, PropertyDescriptor.Accessor(get: JsValue.From(getter), configurable: true));
    }

    private JsValue Create(string pattern, string flags)
    {
        Interpreter.ChargeNativeWork((long)pattern.Length + flags.Length + 1);
        try { return CreateLiteral(Compile(pattern, flags)); }
        catch (ArgumentException error) { throw new JsException(JsValue.From(new JsError("SyntaxError", error.Message))); }
    }

    internal JsValue CreateLiteral(Definition definition) => JsValue.From(new Instance(definition, _prototype));
    internal JsValue CreateLiteral(string pattern, string flags) => Create(pattern, flags);

    internal static Definition Compile(string pattern, string flags)
    {
        if (pattern.Length > MaximumPattern) throw Invalid("pattern length limit exceeded");
        if (flags.Length > SupportedFlags.Length || flags.Any(flag => !SupportedFlags.Contains(flag)) || flags.Distinct().Count() != flags.Length)
            throw Invalid("invalid or unsupported flags (supported flags: gimsy)");
        var canonical = new string(SupportedFlags.Where(flags.Contains).ToArray());
        var options = RegexOptions.ECMAScript | RegexOptions.CultureInvariant;
        if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
        var translated = Translate(pattern, flags);
        if (flags.Contains('y')) translated = $@"\G(?:{translated})";
        try { return new Definition(pattern, canonical, new Regex(translated, options, MatchTimeout)); }
        catch (RegexParseException) { throw Invalid("invalid regular expression pattern"); }
    }

    private static ArgumentException Invalid(string message) => new($"RegExp: {message}");

    // Keep JavaScript anchors, whitespace and identity escapes independent of their .NET meanings.
    private static string Translate(string pattern, string flags)
    {
        var translated = new StringBuilder(pattern.Length);
        var inClass = false;
        var captures = 0;
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '\\')
            {
                if (++i == pattern.Length) throw Invalid("unterminated escape");
                var escaped = pattern[i];
                switch (escaped)
                {
                    case 's': translated.Append(inClass ? WhiteSpace : $"[{WhiteSpace}]"); break;
                    case 'S' when !inClass: translated.Append($"[^{WhiteSpace}]"); break;
                    case 'S': throw Invalid(@"\S in a character class is not supported");
                    case 'x' or 'u':
                        var digits = escaped == 'x' ? 2 : 4;
                        if (i + digits >= pattern.Length || !pattern.AsSpan(i + 1, digits).ToArray().All(Uri.IsHexDigit))
                            throw Invalid("expected a fixed-width hexadecimal escape");
                        translated.Append(pattern, i - 1, digits + 2);
                        i += digits;
                        break;
                    case 'c':
                        if (i + 1 >= pattern.Length || !char.IsAsciiLetter(pattern[i + 1])) throw Invalid("invalid control escape");
                        translated.Append(@"\c").Append(pattern[++i]);
                        break;
                    case '0' when i + 1 == pattern.Length || !char.IsAsciiDigit(pattern[i + 1]): translated.Append(@"\u0000"); break;
                    case >= '0' and <= '9': throw Invalid("backreferences and legacy octal escapes are not supported");
                    case 'k' when i + 1 < pattern.Length && pattern[i + 1] == '<': throw Invalid("named backreferences are not supported");
                    case 'B' when inClass: translated.Append('B'); break;
                    case 'b' or 'B' or 'd' or 'D' or 'w' or 'W' or 'f' or 'n' or 'r' or 't' or 'v': translated.Append('\\').Append(escaped); break;
                    default: translated.Append(escaped is '-' or ']' ? $"\\{escaped}" : Regex.Escape(escaped.ToString())); break;
                }
                continue;
            }
            if (inClass)
            {
                if (ch == ']') inClass = false;
                if (ch == '[' || ch == '-' && i + 1 < pattern.Length && pattern[i + 1] == '[') translated.Append('\\');
                translated.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '[' when pattern.AsSpan(i).StartsWith(@"[\s\S]", StringComparison.Ordinal) ||
                              pattern.AsSpan(i).StartsWith(@"[\S\s]", StringComparison.Ordinal): translated.Append(@"[\s\S]"); i += 5; break;
                case '[' when pattern.AsSpan(i).StartsWith("[^]", StringComparison.Ordinal): translated.Append(@"[\s\S]"); i += 2; break;
                case '[' when i + 1 < pattern.Length && pattern[i + 1] == ']': translated.Append("(?!)"); i++; break;
                case '[': inClass = true; translated.Append(ch); break;
                case '(':
                    if (i + 1 < pattern.Length && pattern[i + 1] == '?')
                    {
                        if (i + 2 >= pattern.Length || pattern[i + 2] is not (':' or '=' or '!'))
                            throw Invalid("only noncapturing groups and forward lookahead are supported after '(?'");
                    }
                    else if (++captures > MaximumCaptures) throw Invalid("capture count limit exceeded");
                    translated.Append(ch);
                    break;
                case '.': translated.Append(flags.Contains('s') ? @"[\s\S]" : $"[^{LineTerminators}]"); break;
                case '^': translated.Append(flags.Contains('m') ? $@"(?:\A|(?<=[{LineTerminators}]))" : @"\A"); break;
                case '$': translated.Append(flags.Contains('m') ? $@"(?:\z|(?=[{LineTerminators}]))" : @"\z"); break;
                default: translated.Append(ch); break;
            }
        }
        return translated.ToString();
    }

    private static string EscapeSource(string pattern)
    {
        if (pattern.Length == 0) return "(?:)";
        var source = new StringBuilder(pattern.Length);
        var escaped = false;
        var inClass = false;
        foreach (var ch in pattern)
        {
            if (!escaped)
            {
                if (ch == '[') inClass = true;
                else if (ch == ']') inClass = false;
                else if (ch == '/' && !inClass) source.Append('\\');
            }
            source.Append(ch switch { '\r' => @"\r", '\n' => @"\n", '\u2028' => @"\u2028", '\u2029' => @"\u2029", _ => ch.ToString() });
            escaped = ch == '\\' && !escaped;
        }
        return source.ToString();
    }

    private sealed class ScanBudget
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private int _searches;
        internal ScanBudget(string input)
        {
            if (input.Length > MaximumInput) throw JsException.RangeError("RegExp input length limit exceeded");
        }
        internal void BeforeSearch()
        {
            if (++_searches > MaximumMatches + 1 || Stopwatch.GetElapsedTime(_started) >= ScanTimeout)
                throw JsException.RangeError("RegExp scan limit exceeded");
            Interpreter.ChargeNativeWork(1);
        }
        internal void AfterSearch(int characters)
        {
            if (Stopwatch.GetElapsedTime(_started) >= ScanTimeout) throw JsException.RangeError("RegExp scan time limit exceeded");
            Interpreter.ChargeNativeWork(characters);
        }
    }

    private static Match? Find(Instance regex, string input, ScanBudget budget)
    {
        var definition = regex.Definition;
        var stateful = definition.Global || definition.Sticky;
        var number = AbstractOperations.ToPrimitive(regex.Get("lastIndex")).ToNumber();
        var start = !stateful || !(number > 0) ? 0 : Math.Truncate(number);
        if (start > input.Length)
        {
            if (stateful) SetIndex(regex, 0);
            return null;
        }
        budget.BeforeSearch();
        Match match;
        try { match = definition.Regex.Match(input, (int)start); }
        catch (RegexMatchTimeoutException)
        {
            Interpreter.ChargeNativeWork(Math.Max(1_024, input.Length));
            throw JsException.RangeError("RegExp match time limit exceeded");
        }
        budget.AfterSearch(match.Success ? match.Index - (int)start + match.Length : input.Length - (int)start);
        if (!match.Success || definition.Sticky && match.Index != start)
        {
            if (stateful) SetIndex(regex, 0);
            return null;
        }
        if (stateful) SetIndex(regex, match.Index + match.Length);
        return match;
    }

    private static void SetIndex(Instance regex, int index)
    {
        if (!regex.Set("lastIndex", JsValue.From((double)index), JsValue.From(regex)))
            throw JsException.TypeError("RegExp lastIndex is not writable");
    }

    private JsValue ExecResult(Match? match, string input)
    {
        if (match is null) return JsValue.Null;
        var result = new JsArray { Prototype = _arrayPrototype };
        var totalLength = 0;
        foreach (Group group in match.Groups)
        {
            totalLength += group.Length;
            if (totalLength > MaximumOutput) throw JsException.RangeError("RegExp capture output length limit exceeded");
            Interpreter.ChargeNativeWork(group.Length + 1L);
            result.Add(Capture(group));
        }
        result.Set("index", JsValue.From((double)match.Index));
        result.Set("input", JsValue.From(input));
        result.Set("groups", JsValue.Undefined);
        return JsValue.From(result);
    }

    private static JsValue Capture(Group group) => group.Success ? JsValue.From(group.Value) : JsValue.Undefined;

    internal JsValue StringMatch(JsValue receiver, JsValue[] args)
    {
        var input = ReceiverText(receiver);
        var pattern = At(args, 0);
        var regex = pattern.Data as Instance ?? Require(Create(pattern.Type == JsType.Undefined ? "" : Text(pattern), ""));
        var budget = new ScanBudget(input);
        if (!regex.Definition.Global) return ExecResult(Find(regex, input, budget), input);
        var matches = new JsArray { Prototype = _arrayPrototype };
        foreach (var match in AllMatches(regex, input, budget)) matches.Add(JsValue.From(match.Value));
        return matches.Length == 0 ? JsValue.Null : JsValue.From(matches);
    }

    internal static JsValue StringReplace(JsValue receiver, JsValue[] args) => Replace(receiver, args, all: false);

    internal static JsValue StringReplaceAll(JsValue receiver, JsValue[] args) => Replace(receiver, args, all: true);

    private static JsValue Replace(JsValue receiver, JsValue[] args, bool all)
    {
        var search = At(args, 0);
        var regex = search.Data as Instance;
        if (all && !AbstractOperations.IsNullish(receiver) && regex is not null && !Text(regex.Get("flags")).Contains('g'))
            throw JsException.TypeError("String.prototype.replaceAll requires a global RegExp");
        var input = ReceiverText(receiver);
        var needle = regex is null ? Text(search) : null;
        var replacement = At(args, 1);
        var callback = replacement.Data as JsFunction;
        var template = callback is null ? Text(replacement) : null;
        var output = new StringBuilder();
        var end = 0;
        var replaced = false;
        if (regex is not null)
        {
            var budget = new ScanBudget(input);
            if (regex.Definition.Global)
            {
                var matches = new List<Match>();
                matches.AddRange(AllMatches(regex, input, budget));
                foreach (var match in matches) ReplaceMatch(match.Index, match.Length, match.Groups);
            }
            else if (Find(regex, input, budget) is { } match) ReplaceMatch(match.Index, match.Length, match.Groups);
        }
        else
        {
            Interpreter.ChargeNativeWork((long)input.Length + needle!.Length + 1);
            var start = 0;
            while (start <= input.Length)
            {
                var index = input.IndexOf(needle, start, StringComparison.Ordinal);
                if (index < 0) break;
                Interpreter.ChargeNativeWork();
                ReplaceMatch(index, needle.Length, null);
                if (!all || index == input.Length) break;
                start = index + Math.Max(needle.Length, 1);
            }
        }
        if (!replaced) return JsValue.From(input);
        Append(input.AsSpan(end));
        return JsValue.From(output.ToString());

        void Append(ReadOnlySpan<char> value)
        {
            if (value.Length > MaximumOutput - output.Length) throw JsException.RangeError("String.replace output length limit exceeded");
            Interpreter.ChargeNativeWork(value.Length);
            output.Append(value);
        }

        void ReplaceMatch(int index, int length, GroupCollection? groups)
        {
            replaced = true;
            Append(input.AsSpan(end, index - end));
            end = index + length;
            var count = (groups?.Count ?? 1) - 1;
            if (callback is not null)
            {
                var arguments = new JsValue[count + 3];
                arguments[0] = JsValue.From(input.Substring(index, length));
                var argumentLength = length;
                for (var i = 1; i <= count; i++)
                {
                    var group = groups![i];
                    argumentLength += group.Length;
                    if (argumentLength > MaximumOutput) throw JsException.RangeError("RegExp capture output length limit exceeded");
                    Interpreter.ChargeNativeWork(group.Length + 1L);
                    arguments[i] = Capture(group);
                }
                arguments[^2] = JsValue.From((double)index);
                arguments[^1] = JsValue.From(input);
                Append(Text(callback.Call(JsValue.Undefined, arguments)));
                return;
            }
            for (var i = 0; i < template!.Length; i++)
            {
                if (template[i] == '$' && i + 1 < template.Length)
                {
                    switch (template[i + 1])
                    {
                        case '$': Append("$"); i++; continue;
                        case '&': Append(input.AsSpan(index, length)); i++; continue;
                        case '`': Append(input.AsSpan(0, index)); i++; continue;
                        case '\'': Append(input.AsSpan(end)); i++; continue;
                        case >= '0' and <= '9':
                            var group = template[i + 1] - '0';
                            var digits = 1;
                            if (i + 2 < template.Length && char.IsAsciiDigit(template[i + 2]))
                            {
                                var candidate = group * 10 + template[i + 2] - '0';
                                if (candidate > 0 && candidate <= count) { group = candidate; digits = 2; }
                            }
                            if (group > 0 && group <= count)
                            {
                                var capture = groups![group];
                                if (capture.Success) Append(input.AsSpan(capture.Index, capture.Length));
                                i += digits;
                                continue;
                            }
                            break;
                    }
                }
                Append(template.AsSpan(i, 1));
            }
        }
    }

    private static IEnumerable<Match> AllMatches(Instance regex, string input, ScanBudget budget)
    {
        SetIndex(regex, 0);
        var count = 0;
        while (Find(regex, input, budget) is { } match)
        {
            if (++count > MaximumMatches) throw JsException.RangeError("RegExp match count limit exceeded");
            if (match.Length == 0) SetIndex(regex, match.Index + 1);
            yield return match;
        }
    }

    private static Instance Require(JsValue receiver) => receiver.Data as Instance ?? throw JsException.TypeError("RegExp method called on an incompatible receiver");
    private static JsValue At(JsValue[] args, int index) => index < args.Length ? args[index] : JsValue.Undefined;
    private static string Text(JsValue value) => AbstractOperations.ToPrimitive(value, preferString: true).ToJsString();
    private static string ReceiverText(JsValue receiver) => AbstractOperations.IsNullish(receiver)
        ? throw JsException.TypeError("String method called on null or undefined") : Text(receiver);
}
