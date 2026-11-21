// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using Sdcb.Arithmetic;
using System.Text;
using ShoneJs.Parsing;

namespace ShoneJs.Runtime;

public sealed partial class Interpreter
{
    private JsValue Eval(Expr expression)
    {
        ConsumeStep();
        return EvalCore(expression);
    }

    private JsValue EvalCore(Expr expression) => expression switch
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
        RegExpLiteralExpr { Definition: { } definition } => _regularExpressions.CreateLiteral(definition),
        RegExpLiteralExpr regex => _regularExpressions.CreateLiteral(regex.Pattern, regex.Flags),
        ThisExpr => _this,
        ImportMetaExpr => JsValue.From(_activeModule!.Meta),
        IdentifierExpr or MemberExpr or OptionalChainExpr => Resolve(expression).Read(),
        TemplateExpr template => Interpolate(template),
        UnaryExpr unary => Unary(unary),
        BinaryExpr binary => Binary(binary),
        AssignExpr assignment => Assign(assignment.Target, assignment.Value),
        CompoundAssignExpr assignment => Assign(assignment.Target, assignment.Value, assignment.Op),
        UpdateExpr update => Update(update),
        CallExpr call => Call(call.Target, call.Arguments),
        NewExpr call => Construct(call),
        FunctionExpr function => MakeFunction(function.Parameters, function.Body, _env, function.Name, function.IsMethod, isAsync: function.IsAsync),
        ArrowFunctionExpr arrow => MakeFunction(arrow.Parameters.Select(parameter => parameter.Name).ToList(), arrow.BlockBody,
            _env, lexicalThis: _this, expressionBody: arrow.ExpressionBody, isAsync: arrow.IsAsync),
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

    private enum ReferenceKind { Value, Binding, GlobalBinding, GlobalProperty, Member }

    private readonly struct Reference(ReferenceKind kind, Interpreter? interpreter = null, string? key = null,
        EnvironmentRecord.Binding? binding = null, JsValue value = default)
    {
        public JsValue Receiver => kind == ReferenceKind.Member ? value : JsValue.Undefined;

        public JsValue Read() => kind switch
        {
            ReferenceKind.Value => value,
            ReferenceKind.Binding => binding!.Read(key!),
            ReferenceKind.GlobalBinding or ReferenceKind.GlobalProperty => interpreter!.GetGlobal(key!),
            _ => value.ObjectValue is { } obj ? obj.Get(key!, value) : interpreter!._primitives.GetProperty(value, key!)
        };

        public void Write(JsValue result)
        {
            switch (kind)
            {
                case ReferenceKind.Binding:
                    binding!.Write(key!, result);
                    break;
                case ReferenceKind.GlobalBinding:
                    interpreter!.WriteBinding(interpreter._global, key!, result);
                    break;
                case ReferenceKind.GlobalProperty:
                    if (interpreter!._strict && !interpreter._globalObject.HasProperty(key!))
                        throw JsException.ReferenceError($"'{key}' is not defined");
                    interpreter._globalObject.Set(key!, result);
                    break;
                case ReferenceKind.Member:
                    if ((value.ObjectValue is not { } obj || !obj.Set(key!, result, value)) && interpreter!._strict)
                        throw JsException.TypeError($"Cannot assign to property '{key}'");
                    break;
                default:
                    throw new InvalidOperationException("Invalid assignment target");
            }
        }

        public bool Delete() => kind switch
        {
            ReferenceKind.Value => true,
            ReferenceKind.GlobalProperty => interpreter!._globalObject.Delete(key!),
            ReferenceKind.Member => value.ObjectValue?.Delete(key!) ?? true,
            _ => false
        };
    }

    private Reference Resolve(Expr expression)
    {
        expression = UnwrapTypeExpression(expression);
        if (expression is IdentifierExpr identifier)
        {
            var name = identifier.Name;
            var binding = _env.ResolveBinding(name, out var environment);
            if (binding is null) return new Reference(ReferenceKind.GlobalProperty, this, name);
            if (environment == _global && !binding.Lexical) return new Reference(ReferenceKind.GlobalBinding, this, name);
            return new Reference(ReferenceKind.Binding, key: name, binding: binding);
        }
        if (expression is MemberExpr member) return MemberReference(Eval(member.Target), member.Key);
        if (expression is OptionalChainExpr chain) return ResolveChain(chain);
        throw new InvalidOperationException("Invalid assignment target");
    }

    private Reference MemberReference(JsValue receiver, Expr keyExpression)
    {
        return MemberReference(receiver, Eval(keyExpression));
    }

    private Reference MemberReference(JsValue receiver, JsValue property)
    {
        if (AbstractOperations.IsNullish(receiver)) throw JsException.TypeError("Cannot access property of null or undefined");
        var key = Builtins.ObjectBuiltins.ToPropertyKey(property);
        return new Reference(ReferenceKind.Member, this, key, value: receiver);
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

    private static Reference ValueReference(JsValue value) => new(ReferenceKind.Value, value: value);

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

    private JsValue Update(UpdateExpr update) => Update(update, Resolve(update.Target));

    private static JsValue Update(UpdateExpr update, Reference reference)
    {
        var value = AbstractOperations.ToPrimitive(reference.Read());
        if (value.Type != JsType.BigInt) value = JsValue.From(value.ToNumber());
        var increment = update.Op == TokenKind.PlusPlus ? 1 : -1;
        var result = value.Type == JsType.BigInt ? JsValue.From((BigInt)value.Data! + increment) : JsValue.From(value.ToNumber() + increment);
        reference.Write(result);
        return update.Prefix ? result : value;
    }

    private JsValue Unary(UnaryExpr unary)
    {
        if (unary.Op == TokenKind.Typeof && UnwrapTypeExpression(unary.Value) is IdentifierExpr id && _env.ResolveBinding(id.Name, out _) is null && !_globalObject.HasProperty(id.Name))
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
            TokenKind.Minus when value.Type == JsType.BigInt => JsValue.From(-(BigInt)value.Data!),
            TokenKind.Minus => JsValue.From(-value.ToNumber()),
            TokenKind.Plus => JsValue.From(value.ToNumber()),
            TokenKind.Tilde when value.Type == JsType.BigInt => JsValue.From(~(BigInt)value.Data!),
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
        if (left.Type == JsType.String && right.Type == JsType.String)
            return string.CompareOrdinal((string)left.Data!, (string)right.Data!);
        if (left.Type == JsType.BigInt)
        {
            var x = (BigInt)left.Data!;
            if (right.Type == JsType.BigInt) return x.CompareTo((BigInt)right.Data!);
            if (right.Type == JsType.String)
                return AbstractOperations.TryParseBigInt((string)right.Data!, out var parsed) ? x.CompareTo(parsed) : null;
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
        var reference = IsReference(target) ? Resolve(target) : ValueReference(Eval(target));
        return Invoke(reference.Read(), reference.Receiver, arguments);
    }

    private JsValue Invoke(JsValue function, JsValue receiver, List<Expr> arguments)
    {
        var values = EvaluateArguments(arguments);
        if (function.ObjectValue is not JsFunction callable) throw JsException.TypeError("Value is not callable");
        return callable.Call(receiver, values);
    }

    private JsValue Construct(NewExpr expression)
    {
        var value = Eval(expression.Target);
        var args = EvaluateArguments(expression.Arguments);
        if (value.ObjectValue is not JsFunction constructor)
            throw JsException.TypeError("Value is not a constructor");
        return constructor.Construct(args);
    }

    private JsValue[] EvaluateArguments(List<Expr> arguments)
    {
        if (arguments.Count == 0) return [];
        // Host functions can retain or mutate the array after the call.
        var values = new JsValue[arguments.Count];
        for (var i = 0; i < values.Length; i++) values[i] = Eval(arguments[i]);
        return values;
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
        foreach (var property in expression.Properties)
        {
            var key = Builtins.ObjectBuiltins.ToPropertyKey(Eval(property.Key));
            DefineLiteralProperty(obj, key, Eval(property.Value), property.Kind);
        }
        return JsValue.From(obj);
    }

    private static void DefineLiteralProperty(JsObject obj, string key, JsValue value, ObjectPropertyKind kind)
    {
        if (kind == ObjectPropertyKind.Prototype)
        {
            if (value.Data is JsObject prototype) obj.SetPrototypeOf(prototype);
            else if (value.Type == JsType.Null) obj.SetPrototypeOf(null);
            return;
        }
        var descriptor = kind switch
        {
            ObjectPropertyKind.Getter => new PropertyDescriptor { Get = value, Enumerable = true, Configurable = true },
            ObjectPropertyKind.Setter => new PropertyDescriptor { Set = value, Enumerable = true, Configurable = true },
            _ => PropertyDescriptor.Data(value, writable: true, enumerable: true, configurable: true)
        };
        obj.DefineOwnProperty(key, descriptor);
    }
}
