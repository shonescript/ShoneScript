// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using Sdcb.Arithmetic;
using System.Text;
using ShoneJS.Parsing;

namespace ShoneJS.Runtime;

public sealed partial class Interpreter
{
    private JsValue Eval(Expr expression) => expression switch
    {
        LiteralExpr literal => literal.Value switch
        {
            null => JsValue.Null,
            string text => JsValue.From(text),
            bool flag => JsValue.From(flag),
            double number => JsValue.From(number),
            JsValue value => value,
            _ => throw new NotSupportedException("Unsupported literal")
        },
        BigIntExpr integer => JsValue.From(integer.Value),
        ThisExpr => _this,
        IdentifierExpr or MemberExpr or OptionalChainExpr => Resolve(expression).Read(),
        TemplateExpr template => Interpolate(template),
        UnaryExpr unary => Unary(unary),
        BinaryExpr binary => Binary(binary),
        AssignExpr assignment => Assign(assignment.Target, assignment.Value),
        CompoundAssignExpr assignment => Assign(assignment.Target, assignment.Value, assignment.Op),
        UpdateExpr update => Update(update),
        CallExpr call => Call(call.Target, call.Arguments),
        NewExpr call => Construct(call),
        FunctionExpr function => MakeFunction(function.Parameters, function.Body, _env, function.Name, function.IsMethod),
        ArrowFunctionExpr arrow => MakeFunction(arrow.Parameters.Select(parameter => parameter.Name).ToList(), arrow.BlockBody,
            _env, lexicalThis: _this, expressionBody: arrow.ExpressionBody),
        ArrayExpr array => MakeArray(array),
        ObjectExpr obj => MakeObject(obj),
        ConditionalExpr condition => Eval(condition.Test).ToBoolean() ? Eval(condition.Then) : Eval(condition.Else),
        SequenceExpr sequence => sequence.Expressions.Aggregate(JsValue.Undefined, (_, item) => Eval(item)),
        TypeAssertionExpr assertion => Eval(assertion.Expression),
        SatisfiesExpr satisfies => Eval(satisfies.Expression),
        NonNullExpr assertion => Eval(assertion.Expression),
        _ => throw new NotSupportedException($"Unsupported expression {expression.GetType().Name}")
    };

    private JsValue Interpolate(TemplateExpr template)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < template.Quasis.Count; i++)
        {
            builder.Append(template.Quasis[i].Cooked);
            if (i < template.Expressions.Count)
                builder.Append(AbstractOperations.ToPrimitive(Eval(template.Expressions[i]), preferString: true).ToJsString());
        }
        return JsValue.From(builder.ToString());
    }

    private sealed record Reference(Func<JsValue> Read, Action<JsValue> Write, Func<bool> Delete, JsValue Receiver);

    private Reference Resolve(Expr expression)
    {
        expression = UnwrapTypeExpression(expression);
        if (expression is IdentifierExpr identifier)
        {
            var name = identifier.Name;
            var environment = _env.Resolve(name);
            return new Reference(
                () => environment is null || environment == _global ? GetGlobal(name) : environment.Get(name),
                value =>
                {
                    if (environment is not null) WriteBinding(environment, name, value);
                    else if (_strict && !_globalObject.HasProperty(name)) throw JsException.ReferenceError($"'{name}' is not defined");
                    else _globalObject.Set(name, value);
                },
                () => environment is null && _globalObject.Delete(name), JsValue.Undefined);
        }
        if (expression is MemberExpr member) return MemberReference(Eval(member.Target), member.Key);
        if (expression is OptionalChainExpr chain) return ResolveChain(chain);
        throw new InvalidOperationException("Invalid assignment target");
    }

    private Reference MemberReference(JsValue receiver, Expr keyExpression)
    {
        var property = Eval(keyExpression);
        if (AbstractOperations.IsNullish(receiver)) throw JsException.TypeError("Cannot access property of null or undefined");
        var key = Builtins.ObjectBuiltins.ToPropertyKey(property);
        var obj = receiver.Data as JsObject;
        return new Reference(
            () => obj is null ? GetPrimitiveProperty(receiver, key) : obj.Get(key, receiver),
            value =>
            {
                if ((obj is null || !obj.Set(key, value, receiver)) && _strict)
                    throw JsException.TypeError($"Cannot assign to property '{key}'");
            },
            () => obj is null || obj.Delete(key), receiver);
    }

    private static bool IsReference(Expr expression) => UnwrapTypeExpression(expression) is IdentifierExpr or MemberExpr or OptionalChainExpr;

    private static Expr UnwrapTypeExpression(Expr expression)
    {
        while (true)
        {
            switch (expression)
            {
                case TypeAssertionExpr assertion: expression = assertion.Expression; break;
                case SatisfiesExpr satisfies: expression = satisfies.Expression; break;
                case NonNullExpr assertion: expression = assertion.Expression; break;
                default: return expression;
            }
        }
    }

    private Reference ResolveChain(OptionalChainExpr chain)
    {
        var current = IsReference(chain.Target) ? Resolve(chain.Target) : ValueReference(Eval(chain.Target));
        foreach (var segment in chain.Segments)
        {
            var value = current.Read();
            if (segment.Optional && AbstractOperations.IsNullish(value)) return ValueReference(JsValue.Undefined);
            current = segment switch
            {
                ChainMember member => MemberReference(value, member.Key),
                ChainCall call => ValueReference(Invoke(value, current.Receiver, call.Arguments)),
                _ => throw new NotSupportedException($"Unsupported chain segment {segment.GetType().Name}")
            };
        }
        return current;
    }

    private static Reference ValueReference(JsValue value) => new(() => value,
        _ => throw new InvalidOperationException("Invalid assignment target"), () => true, JsValue.Undefined);

    private static JsValue GetPrimitiveProperty(JsValue receiver, string key)
    {
        if (receiver.Data is not string text) return JsValue.Undefined;
        if (key == "length") return JsValue.From((double)text.Length);
        if (key == "indexOf") return JsValue.From(StringIndexOf);
        if (uint.TryParse(key, out var index) && index < text.Length && key == index.ToString(System.Globalization.CultureInfo.InvariantCulture))
            return JsValue.From(text[(int)index].ToString());
        return JsValue.Undefined;
    }

    private static readonly JsFunction StringIndexOf = new((receiver, args) =>
    {
        if (AbstractOperations.IsNullish(receiver)) throw JsException.TypeError("String.prototype.indexOf requires a receiver");
        var text = receiver.ToJsString();
        var search = (args.Length == 0 ? JsValue.Undefined : args[0]).ToJsString();
        var number = args.Length < 2 ? 0 : args[1].ToNumber();
        var position = double.IsNaN(number) ? 0 : (int)Math.Clamp(Math.Truncate(number), 0, text.Length);
        return JsValue.From((double)text.IndexOf(search, position, StringComparison.Ordinal));
    }, 1);

    private JsValue Assign(Expr target, Expr expression, TokenKind? op = null)
    {
        var reference = Resolve(target);
        JsValue value;
        if (op is null) value = Eval(expression);
        else
        {
            var current = reference.Read();
            switch (op.Value)
            {
                case TokenKind.AndAnd:
                    if (!current.ToBoolean()) return current;
                    value = Eval(expression);
                    break;
                case TokenKind.OrOr:
                    if (current.ToBoolean()) return current;
                    value = Eval(expression);
                    break;
                case TokenKind.NullishCoalescing:
                    if (!AbstractOperations.IsNullish(current)) return current;
                    value = Eval(expression);
                    break;
                default:
                    value = ApplyBinary(op.Value, current, Eval(expression));
                    break;
            }
        }
        reference.Write(value);
        return value;
    }

    private JsValue Update(UpdateExpr update)
    {
        var reference = Resolve(update.Target);
        var value = AbstractOperations.ToPrimitive(reference.Read());
        if (value.Type != JsType.BigInt) value = JsValue.From(value.ToNumber());
        var increment = update.Op == TokenKind.PlusPlus ? 1 : -1;
        var result = value.Data is BigInt integer ? JsValue.From(integer + increment) : JsValue.From(value.ToNumber() + increment);
        reference.Write(result);
        return update.Prefix ? result : value;
    }

    private JsValue Unary(UnaryExpr unary)
    {
        if (unary.Op == TokenKind.Typeof && UnwrapTypeExpression(unary.Value) is IdentifierExpr id && _env.Resolve(id.Name) is null && !_globalObject.HasProperty(id.Name))
            return JsValue.From("undefined");
        if (unary.Op == TokenKind.Delete)
        {
            var deleted = true;
            if (IsReference(unary.Value)) deleted = Resolve(unary.Value).Delete();
            else Eval(unary.Value);
            if (!deleted && _strict) throw JsException.TypeError("Cannot delete property");
            return JsValue.From(deleted);
        }
        var value = Eval(unary.Value);
        if (unary.Op is TokenKind.Plus or TokenKind.Minus or TokenKind.Tilde)
            value = AbstractOperations.ToPrimitive(value);
        return unary.Op switch
        {
            TokenKind.Bang => JsValue.From(!value.ToBoolean()),
            TokenKind.Minus when value.Data is BigInt integer => JsValue.From(-integer),
            TokenKind.Minus => JsValue.From(-value.ToNumber()),
            TokenKind.Plus => JsValue.From(value.ToNumber()),
            TokenKind.Tilde when value.Data is BigInt integer => JsValue.From(~integer),
            TokenKind.Tilde => JsValue.From((double)~ToInt32(value.ToNumber())),
            TokenKind.Void => JsValue.Undefined,
            TokenKind.Typeof => JsValue.From(value.Type switch { JsType.Null => "object", JsType.BigInt => "bigint", _ => value.Type.ToString().ToLowerInvariant() }),
            _ => throw new NotSupportedException($"Unsupported unary operator {unary.Op}")
        };
    }

    private static int ToInt32(double value)
    {
        if (!double.IsFinite(value)) return 0;
        return unchecked((int)(long)(Math.Truncate(value) % 4_294_967_296d));
    }

    private JsValue Binary(BinaryExpr binary)
    {
        var left = Eval(binary.Left);
        return binary.Op switch
        {
            TokenKind.AndAnd => left.ToBoolean() ? Eval(binary.Right) : left,
            TokenKind.OrOr => left.ToBoolean() ? left : Eval(binary.Right),
            TokenKind.NullishCoalescing => AbstractOperations.IsNullish(left) ? Eval(binary.Right) : left,
            _ => ApplyBinary(binary.Op, left, Eval(binary.Right))
        };
    }

    private static JsValue ApplyBinary(TokenKind op, JsValue left, JsValue right)
    {
        switch (op)
        {
            case TokenKind.StrictEqual: return JsValue.From(AbstractOperations.StrictEqual(left, right));
            case TokenKind.StrictNotEqual: return JsValue.From(!AbstractOperations.StrictEqual(left, right));
            case TokenKind.EqualEqual: return JsValue.From(AbstractOperations.LooseEqual(left, right));
            case TokenKind.NotEqual: return JsValue.From(!AbstractOperations.LooseEqual(left, right));
            case TokenKind.Instanceof: return JsValue.From(InstanceOf(left, right));
            case TokenKind.In:
                if (right.Data is not JsObject obj) throw JsException.TypeError("Right operand of 'in' must be an object");
                return JsValue.From(obj.HasProperty(Builtins.ObjectBuiltins.ToPropertyKey(left)));
            case TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual:
                var order = Compare(left, right);
                return JsValue.From(op switch { TokenKind.Less => order < 0, TokenKind.LessEqual => order <= 0, TokenKind.Greater => order > 0, _ => order >= 0 });
        }
        left = AbstractOperations.ToPrimitive(left);
        right = AbstractOperations.ToPrimitive(right);
        if (op == TokenKind.Plus && (left.Type == JsType.String || right.Type == JsType.String))
            return JsValue.From(left.ToJsString() + right.ToJsString());
        if (left.Type == JsType.BigInt || right.Type == JsType.BigInt)
        {
            if (left.Data is not BigInt a || right.Data is not BigInt b) throw JsException.TypeError("Cannot mix BigInt and other numeric types");
            if (b.IsZero && op is TokenKind.Slash or TokenKind.Percent) throw JsException.RangeError("Division by zero");
            return JsValue.From(op switch
            {
                TokenKind.Plus => a + b, TokenKind.Minus => a - b, TokenKind.Star => a * b,
                TokenKind.Slash => a / b, TokenKind.Percent => a % b,
                TokenKind.BitwiseAnd => a & b,
                TokenKind.BitwiseOr => a | b,
                TokenKind.BitwiseXor => a ^ b,
                TokenKind.ShiftLeft => ShiftBigInt(a, b),
                TokenKind.ShiftRight => ShiftBigInt(a, -b),
                TokenKind.ShiftUnsignedRight => throw JsException.TypeError("BigInt does not support unsigned right shift"),
                TokenKind.Power => PowerBigInt(a, b),
                _ => throw new NotSupportedException($"Unsupported BigInt operator {op}")
            });
        }
        var x = left.ToNumber();
        var y = right.ToNumber();
        return JsValue.From(op switch
        {
            TokenKind.Plus => x + y, TokenKind.Minus => x - y, TokenKind.Star => x * y,
            TokenKind.Slash => x / y, TokenKind.Percent => x % y,
            TokenKind.BitwiseAnd => ToInt32(x) & ToInt32(y),
            TokenKind.BitwiseOr => ToInt32(x) | ToInt32(y),
            TokenKind.BitwiseXor => ToInt32(x) ^ ToInt32(y),
            TokenKind.ShiftLeft => ToInt32(x) << (ToInt32(y) & 31),
            TokenKind.ShiftRight => ToInt32(x) >> (ToInt32(y) & 31),
            TokenKind.ShiftUnsignedRight => unchecked((uint)ToInt32(x)) >> (ToInt32(y) & 31),
            TokenKind.Power => double.IsInfinity(y) && Math.Abs(x) == 1 ? double.NaN : Math.Pow(x, y),
            _ => throw new NotSupportedException($"Unsupported binary operator {op}")
        });
    }

    private const int MaximumBigIntResultBits = 16 * 1024 * 1024;

    private static BigInt ShiftBigInt(BigInt value, BigInt shift)
    {
        if (value.IsZero) return BigInt.Zero;
        if (shift <= -value.GetBitLength()) return value.Sign < 0 ? BigInt.MinusOne : BigInt.Zero;
        if (shift > MaximumBigIntResultBits - value.GetBitLength()) throw JsException.RangeError("BigInt result is too large");
        return shift.Sign < 0 ? value >> (int)-shift : value << (int)shift;
    }

    private static BigInt PowerBigInt(BigInt value, BigInt exponent)
    {
        if (exponent.Sign < 0) throw JsException.RangeError("BigInt exponent must be nonnegative");
        if (exponent.IsZero) return BigInt.One;
        if (value.IsZero || value.IsOne) return value;
        if (value == BigInt.MinusOne) return exponent.IsEven ? BigInt.One : BigInt.MinusOne;
        if (exponent * BigInt.Abs(value).GetBitLength() > MaximumBigIntResultBits)
            throw JsException.RangeError("BigInt result is too large");
        return BigInt.Pow(value, (uint)exponent);
    }

    private static int? Compare(JsValue left, JsValue right)
    {
        left = AbstractOperations.ToPrimitive(left);
        right = AbstractOperations.ToPrimitive(right);
        if (left.Data is string a && right.Data is string b) return string.CompareOrdinal(a, b);
        if (left.Data is BigInt x)
        {
            if (right.Data is BigInt y) return x.CompareTo(y);
            if (right.Data is string text) return AbstractOperations.TryParseBigInt(text, out var parsed) ? x.CompareTo(parsed) : null;
            return CompareIntegerAndNumber(x, right.ToNumber());
        }
        if (right.Type == JsType.BigInt) return -Compare(right, left);
        var first = left.ToNumber();
        var second = right.ToNumber();
        return double.IsNaN(first) || double.IsNaN(second) ? null : first.CompareTo(second);
    }

    private static int? CompareIntegerAndNumber(BigInt integer, double number)
    {
        if (double.IsNaN(number)) return null;
        if (double.IsPositiveInfinity(number)) return -1;
        if (double.IsNegativeInfinity(number)) return 1;
        var truncated = Math.Truncate(number);
        var order = integer.CompareTo(new BigInt(truncated));
        return order != 0 ? order : truncated.CompareTo(number);
    }

    private static bool InstanceOf(JsValue left, JsValue right)
    {
        if (right.Data is not JsFunction function) throw JsException.TypeError("Right operand of 'instanceof' is not callable");
        if (left.Data is not JsObject instance) return false;
        if (function.Get("prototype").Data is not JsObject prototype) throw JsException.TypeError("Function prototype is not an object");
        for (var current = instance.GetPrototypeOf(); current is not null; current = current.GetPrototypeOf())
            if (ReferenceEquals(current, prototype)) return true;
        return false;
    }

    private JsValue Call(Expr target, List<Expr> arguments)
    {
        var reference = IsReference(target) ? Resolve(target) : null;
        return Invoke(reference?.Read() ?? Eval(target), reference?.Receiver ?? JsValue.Undefined, arguments);
    }

    private JsValue Invoke(JsValue function, JsValue receiver, List<Expr> arguments)
    {
        var values = arguments.Select(Eval).ToArray();
        if (function.Data is not JsFunction callable) throw JsException.TypeError("Value is not callable");
        return callable.Call(receiver, values);
    }

    private JsValue Construct(NewExpr expression)
    {
        var value = Eval(expression.Target);
        var args = expression.Arguments.Select(Eval).ToArray();
        if (value.Data is not JsFunction constructor)
            throw JsException.TypeError("Value is not a constructor");
        return constructor.Construct(args);
    }

    private JsValue MakeArray(ArrayExpr array)
    {
        var obj = new JsArray { Prototype = _arrayPrototype };
        foreach (var item in array.Items)
        {
            if (item is ArrayHoleExpr) obj.Set("length", JsValue.From(obj.ArrayLength + 1d));
            else obj.Add(Eval(item));
        }
        return JsValue.From(obj);
    }

    private JsValue MakeObject(ObjectExpr expression)
    {
        var obj = new JsObject { Prototype = _objectPrototype };
        foreach (var (key, value) in expression.Properties)
            obj.DefineOwnProperty(key, PropertyDescriptor.Data(Eval(value), writable: true, enumerable: true, configurable: true));
        return JsValue.From(obj);
    }
}
