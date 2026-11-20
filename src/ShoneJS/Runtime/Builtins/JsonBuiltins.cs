// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace ShoneJS.Runtime.Builtins;

internal static class JsonBuiltins
{
    private const int MaxDepth = 512;

    internal static void Install(Action<string, JsValue> setGlobal, JsObject objectPrototype, JsObject arrayPrototype)
    {
        var json = new JsObject { Prototype = objectPrototype };
        DefineMethod("parse", 2, args =>
        {
            var text = AbstractOperations.ToPrimitive(Argument(args, 0), preferString: true).ToJsString();
            var value = new JsonParser(text, objectPrototype, arrayPrototype).Parse();
            return Argument(args, 1).Data is JsFunction reviver
                ? Revive(Wrap(value, objectPrototype), "", reviver, 0)
                : value;
        });
        DefineMethod("stringify", 3, args => new JsonWriter(Argument(args, 1), Argument(args, 2))
            .Stringify(Wrap(Argument(args, 0), objectPrototype)));
        setGlobal("JSON", JsValue.From(json));

        void DefineMethod(string name, int arity, Func<JsValue[], JsValue> call)
        {
            var method = new JsFunction(call, arity) { Prototype = objectPrototype };
            method.DefineOwnProperty("name", PropertyDescriptor.Data(JsValue.From(name), configurable: true));
            json.DefineOwnProperty(name, PropertyDescriptor.Data(JsValue.From(method), writable: true, configurable: true));
        }
    }

    private static JsValue Argument(JsValue[] args, int index) => index < args.Length ? args[index] : JsValue.Undefined;

    private static JsObject Wrap(JsValue value, JsObject objectPrototype)
    {
        var holder = new JsObject { Prototype = objectPrototype };
        DefineDataProperty(holder, "", value);
        return holder;
    }

    private static void DefineDataProperty(JsObject obj, string key, JsValue value) =>
        obj.DefineOwnProperty(key, PropertyDescriptor.Data(value, writable: true, enumerable: true, configurable: true));

    private static string[] EnumerableKeys(JsObject obj) => obj.OwnPropertyKeys()
        .Where(key => obj.GetOwnProperty(key)?.Enumerable == true).ToArray();

    private static long ArrayLikeLength(JsObject obj)
    {
        var length = AbstractOperations.ToPrimitive(obj.Get("length")).ToNumber();
        return double.IsNaN(length) || length <= 0 ? 0 : (long)Math.Min(length, 9_007_199_254_740_991d);
    }

    private static string IndexKey(long index) => index.ToString(CultureInfo.InvariantCulture);

    private static void CheckDepth(int depth)
    {
        if (depth >= MaxDepth || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw JsException.RangeError("JSON nesting is too deep");
    }

    private static JsValue Revive(JsObject holder, string key, JsFunction reviver, int depth)
    {
        var value = holder.Get(key);
        if (value.Data is JsObject obj)
        {
            CheckDepth(depth);
            if (ProxyBuiltins.IsArray(obj))
            {
                var length = ArrayLikeLength(obj);
                for (long index = 0; index < length; index++)
                    ReviveProperty(IndexKey(index));
            }
            else
            {
                foreach (var property in EnumerableKeys(obj))
                    ReviveProperty(property);
            }

            void ReviveProperty(string property)
            {
                var replacement = Revive(obj, property, reviver, depth + 1);
                if (replacement.Type == JsType.Undefined)
                    obj.Delete(property);
                else
                    DefineDataProperty(obj, property, replacement);
            }
        }
        return reviver.Call(JsValue.From(holder), [JsValue.From(key), value]);
    }

    private sealed class JsonParser(string text, JsObject objectPrototype, JsObject arrayPrototype)
    {
        private int _position;

        public JsValue Parse()
        {
            var value = ParseValue(0);
            SkipWhitespace();
            if (_position != text.Length)
                throw InvalidJson();
            return value;
        }

        private JsValue ParseValue(int depth)
        {
            SkipWhitespace();
            if (_position == text.Length)
                throw InvalidJson();
            return text[_position] switch
            {
                '{' => ParseObject(depth),
                '[' => ParseArray(depth),
                '"' => JsValue.From(ParseString()),
                't' => ParseLiteral("true", JsValue.From(true)),
                'f' => ParseLiteral("false", JsValue.From(false)),
                'n' => ParseLiteral("null", JsValue.Null),
                '-' or (>= '0' and <= '9') => ParseNumber(),
                _ => throw InvalidJson()
            };
        }

        private JsValue ParseObject(int depth)
        {
            CheckDepth(depth);
            _position++;
            var obj = new JsObject { Prototype = objectPrototype };
            SkipWhitespace();
            if (Consume('}')) return JsValue.From(obj);
            do
            {
                SkipWhitespace();
                var key = ParseString();
                SkipWhitespace();
                Require(':');
                DefineDataProperty(obj, key, ParseValue(depth + 1));
                SkipWhitespace();
            } while (Consume(','));
            Require('}');
            return JsValue.From(obj);
        }

        private JsValue ParseArray(int depth)
        {
            CheckDepth(depth);
            _position++;
            var array = new JsArray { Prototype = arrayPrototype };
            SkipWhitespace();
            if (Consume(']')) return JsValue.From(array);
            do
            {
                array.Add(ParseValue(depth + 1));
                SkipWhitespace();
            } while (Consume(','));
            Require(']');
            return JsValue.From(array);
        }

        private JsValue ParseLiteral(string literal, JsValue value)
        {
            if (!text.AsSpan(_position).StartsWith(literal, StringComparison.Ordinal))
                throw InvalidJson();
            _position += literal.Length;
            return value;
        }

        private JsValue ParseNumber()
        {
            var start = _position;
            Consume('-');
            if (!Consume('0')) ReadDigits();
            if (Consume('.')) ReadDigits();
            if (Consume('e') || Consume('E'))
            {
                if (!Consume('+')) Consume('-');
                ReadDigits();
            }
            return JsValue.From(double.Parse(text.AsSpan(start, _position - start), NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        private void ReadDigits()
        {
            var start = _position;
            while (_position < text.Length && char.IsAsciiDigit(text[_position])) _position++;
            if (start == _position) throw InvalidJson();
        }

        private string ParseString()
        {
            Require('"');
            var start = _position;
            StringBuilder? decoded = null;
            while (_position < text.Length)
            {
                var character = text[_position++];
                if (character == '"')
                {
                    if (decoded is null) return text[start..(_position - 1)];
                    return decoded.Append(text, start, _position - 1 - start).ToString();
                }
                if (character < ' ') throw InvalidJson();
                if (character != '\\') continue;

                decoded ??= new StringBuilder();
                decoded.Append(text, start, _position - 1 - start);
                if (_position == text.Length) throw InvalidJson();
                decoded.Append(text[_position++] switch
                {
                    '"' => '"', '\\' => '\\', '/' => '/', 'b' => '\b', 'f' => '\f',
                    'n' => '\n', 'r' => '\r', 't' => '\t', 'u' => ParseCodeUnit(),
                    _ => throw InvalidJson()
                });
                start = _position;
            }
            throw InvalidJson();
        }

        // Escapes denote UTF-16 code units, including unpaired surrogates.
        private char ParseCodeUnit()
        {
            var value = 0;
            for (var digit = 0; digit < 4; digit++)
            {
                if (_position == text.Length) throw InvalidJson();
                var character = text[_position++];
                value = (value << 4) | (character switch
                {
                    >= '0' and <= '9' => character - '0',
                    >= 'a' and <= 'f' => character - 'a' + 10,
                    >= 'A' and <= 'F' => character - 'A' + 10,
                    _ => throw InvalidJson()
                });
            }
            return (char)value;
        }

        private void SkipWhitespace()
        {
            while (_position < text.Length && text[_position] is ' ' or '\t' or '\r' or '\n') _position++;
        }

        private bool Consume(char character)
        {
            if (_position == text.Length || text[_position] != character) return false;
            _position++;
            return true;
        }

        private void Require(char character)
        {
            if (!Consume(character)) throw InvalidJson();
        }

        private JsException InvalidJson() => new(JsValue.From(new JsError("SyntaxError", $"Invalid JSON at position {_position}")));
    }

    private sealed class JsonWriter(JsValue replacer, JsValue space)
    {
        private readonly JsFunction? _replacer = replacer.Data as JsFunction;
        private readonly List<string>? _propertyList = ReadPropertyList(replacer);
        private readonly string _gap = space.Type switch
        {
            JsType.Number when space.ToNumber() > 0 => new string(' ', (int)Math.Min(space.ToNumber(), 10)),
            JsType.String => ((string)space.Data!)[..Math.Min(((string)space.Data!).Length, 10)],
            _ => ""
        };
        private readonly StringBuilder _output = new();
        private readonly HashSet<JsObject> _stack = new(ReferenceEqualityComparer.Instance);

        public JsValue Stringify(JsObject holder) => WriteProperty(holder, "", 0)
            ? JsValue.From(_output.ToString())
            : JsValue.Undefined;

        private static List<string>? ReadPropertyList(JsValue replacer)
        {
            if (replacer.Data is not JsObject obj || obj is JsFunction || !ProxyBuiltins.IsArray(obj))
                return null;
            var length = ArrayLikeLength(obj);
            var properties = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (long index = 0; index < length; index++)
            {
                var item = obj.Get(IndexKey(index));
                if (item.Type is not (JsType.String or JsType.Number)) continue;
                var key = item.ToJsString();
                if (seen.Add(key)) properties.Add(key);
            }
            return properties;
        }

        private bool WriteProperty(JsObject holder, string key, int depth)
        {
            var value = holder.Get(key);
            var keyValue = JsValue.From(key);
            if (value.Data is JsObject obj && obj.Get("toJSON").Data is JsFunction toJson)
                value = toJson.Call(value, [keyValue]);
            if (_replacer is not null)
                value = _replacer.Call(JsValue.From(holder), [keyValue, value]);

            switch (value.Type)
            {
                case JsType.Null:
                    _output.Append("null");
                    break;
                case JsType.Boolean:
                    _output.Append(value.ToBoolean() ? "true" : "false");
                    break;
                case JsType.Number:
                    _output.Append(double.IsFinite(value.ToNumber()) ? value.ToJsString() : "null");
                    break;
                case JsType.String:
                    Quote((string)value.Data!);
                    break;
                case JsType.Object:
                    WriteContainer((JsObject)value.Data!, depth);
                    break;
                case JsType.BigInt:
                    throw JsException.TypeError("Cannot serialize a BigInt value to JSON");
                default:
                    return false;
            }
            return true;
        }

        private void WriteContainer(JsObject obj, int depth)
        {
            var isArray = ProxyBuiltins.IsArray(obj);
            if (!_stack.Add(obj)) throw JsException.TypeError("Cannot serialize a circular structure to JSON");
            CheckDepth(depth);

            _output.Append(isArray ? '[' : '{');
            var hasEntries = false;
            if (isArray)
            {
                var length = ArrayLikeLength(obj);
                for (long index = 0; index < length; index++)
                {
                    BeginEntry(depth + 1, hasEntries);
                    if (!WriteProperty(obj, IndexKey(index), depth + 1)) _output.Append("null");
                    hasEntries = true;
                }
            }
            else
            {
                IEnumerable<string> keys = _propertyList is null ? EnumerableKeys(obj) : _propertyList;
                foreach (var key in keys)
                {
                    var start = _output.Length;
                    BeginEntry(depth + 1, hasEntries);
                    Quote(key);
                    _output.Append(_gap.Length == 0 ? ":" : ": ");
                    if (WriteProperty(obj, key, depth + 1))
                        hasEntries = true;
                    else
                        _output.Length = start;
                }
            }
            if (hasEntries) Indent(depth);
            _output.Append(isArray ? ']' : '}');
            _stack.Remove(obj);
        }

        private void BeginEntry(int depth, bool hasEntries)
        {
            if (hasEntries) _output.Append(',');
            Indent(depth);
        }

        private void Indent(int depth)
        {
            if (_gap.Length == 0) return;
            _output.Append('\n');
            for (var level = 0; level < depth; level++) _output.Append(_gap);
        }

        private void Quote(string text)
        {
            _output.Append('"');
            for (var index = 0; index < text.Length; index++)
            {
                var character = text[index];
                switch (character)
                {
                    case '"': _output.Append("\\\""); break;
                    case '\\': _output.Append("\\\\"); break;
                    case '\b': _output.Append("\\b"); break;
                    case '\f': _output.Append("\\f"); break;
                    case '\n': _output.Append("\\n"); break;
                    case '\r': _output.Append("\\r"); break;
                    case '\t': _output.Append("\\t"); break;
                    default:
                        if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                            _output.Append(character).Append(text[++index]);
                        else if (character < ' ' || char.IsSurrogate(character))
                            _output.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            _output.Append(character);
                        break;
                }
            }
            _output.Append('"');
        }
    }
}
