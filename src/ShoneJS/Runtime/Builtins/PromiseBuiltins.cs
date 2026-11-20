using System.Globalization;
using System.Text;

namespace ShoneJS.Runtime.Builtins;

internal sealed class PromiseBuiltins(JsObject objectPrototype, JsObject arrayPrototype, Action<JsException> attachErrorPrototype)
{
    private readonly Queue<Action> _jobs = new();
    private readonly JsObject _prototype = new() { Prototype = objectPrototype };
    private JsFunction _constructor = null!;
    private bool _draining;

    private enum State { Pending, Fulfilled, Rejected }
    private enum Combinator { All, AllSettled, Race }

    private sealed class PromiseObject : JsObject
    {
        public State Status { get; set; }
        public JsValue Result { get; set; }
        public List<Reaction> Reactions { get; } = [];
    }

    private sealed record Capability(JsValue Promise, JsFunction Resolve, JsFunction Reject);
    private sealed record Reaction(Capability Capability, JsFunction? Fulfill, JsFunction? Reject);

    public void Install(Action<string, JsValue> setGlobal)
    {
        _constructor = new JsFunction(_ => throw JsException.TypeError("Promise requires 'new'"), 1)
        {
            Prototype = objectPrototype,
            NativeConstruct = ConstructPromise
        };
        DefineName(_constructor, "Promise");
        _constructor.DefineOwnProperty("prototype", PropertyDescriptor.Data(JsValue.From(_prototype)));
        _prototype.DefineOwnProperty("constructor", PropertyDescriptor.Data(JsValue.From(_constructor), writable: true, configurable: true));
        DefineMethod(_prototype, "then", 2, Then);
        DefineMethod(_prototype, "catch", 1, (receiver, args) =>
            InvokeThen(receiver, JsValue.Undefined, Argument(args, 0)));
        DefineMethod(_prototype, "finally", 1, Finally);
        DefineMethod(_constructor, "resolve", 1, (receiver, args) => Resolve(RequireConstructorObject(receiver), Argument(args, 0)));
        DefineMethod(_constructor, "reject", 1, (receiver, args) =>
        {
            var capability = NewCapability(RequireConstructorObject(receiver));
            capability.Reject.Call(JsValue.Undefined, [Argument(args, 0)]);
            return capability.Promise;
        });
        DefineMethod(_constructor, "all", 1, (receiver, args) => Combine(receiver, Argument(args, 0), Combinator.All));
        DefineMethod(_constructor, "allSettled", 1, (receiver, args) => Combine(receiver, Argument(args, 0), Combinator.AllSettled));
        DefineMethod(_constructor, "race", 1, (receiver, args) => Combine(receiver, Argument(args, 0), Combinator.Race));
        setGlobal("Promise", JsValue.From(_constructor));
    }

    public void DrainJobs(Action consumeIteration)
    {
        if (_draining) return;
        _draining = true;
        try
        {
            while (_jobs.Count != 0)
            {
                consumeIteration();
                _jobs.Dequeue()();
            }
        }
        finally { _draining = false; }
    }

    private JsValue ConstructPromise(JsValue[] args)
    {
        var executor = RequireFunction(Argument(args, 0), "Promise executor is not callable");
        var promise = new PromiseObject { Prototype = _prototype };
        var (resolve, reject) = CreateResolvingFunctions(promise);
        try { executor.Call(JsValue.Undefined, [JsValue.From(resolve), JsValue.From(reject)]); }
        catch (JsException error) { reject.Call(JsValue.Undefined, [ErrorValue(error)]); }
        return JsValue.From(promise);
    }

    private (JsFunction Resolve, JsFunction Reject) CreateResolvingFunctions(PromiseObject promise)
    {
        var alreadyResolved = false;
        var resolve = NewFunction(1, (_, args) =>
        {
            if (!alreadyResolved)
            {
                alreadyResolved = true;
                ResolvePromise(promise, Argument(args, 0));
            }
            return JsValue.Undefined;
        });
        var reject = NewFunction(1, (_, args) =>
        {
            if (!alreadyResolved)
            {
                alreadyResolved = true;
                Settle(promise, State.Rejected, Argument(args, 0));
            }
            return JsValue.Undefined;
        });
        return (resolve, reject);
    }

    private void ResolvePromise(PromiseObject promise, JsValue value)
    {
        if (ReferenceEquals(value.Data, promise))
        {
            Settle(promise, State.Rejected, ErrorValue(JsException.TypeError("A promise cannot resolve to itself")));
            return;
        }

        JsFunction? then;
        try { then = (value.Data as JsObject)?.Get("then").Data as JsFunction; }
        catch (JsException error)
        {
            Settle(promise, State.Rejected, ErrorValue(error));
            return;
        }
        if (then is null)
        {
            Settle(promise, State.Fulfilled, value);
            return;
        }

        _jobs.Enqueue(() =>
        {
            var (resolve, reject) = CreateResolvingFunctions(promise);
            try { then.Call(value, [JsValue.From(resolve), JsValue.From(reject)]); }
            catch (JsException error) { reject.Call(JsValue.Undefined, [ErrorValue(error)]); }
        });
    }

    private void Settle(PromiseObject promise, State state, JsValue value)
    {
        promise.Status = state;
        promise.Result = value;
        foreach (var reaction in promise.Reactions)
            EnqueueReaction(reaction, state, value);
        promise.Reactions.Clear();
    }

    private void EnqueueReaction(Reaction reaction, State state, JsValue value) => _jobs.Enqueue(() =>
    {
        var capability = reaction.Capability;
        var handler = state == State.Fulfilled ? reaction.Fulfill : reaction.Reject;
        if (handler is null)
        {
            var settle = state == State.Fulfilled ? capability.Resolve : capability.Reject;
            settle.Call(JsValue.Undefined, [value]);
            return;
        }

        JsValue result;
        try { result = handler.Call(JsValue.Undefined, [value]); }
        catch (JsException error)
        {
            capability.Reject.Call(JsValue.Undefined, [ErrorValue(error)]);
            return;
        }
        capability.Resolve.Call(JsValue.Undefined, [result]);
    });

    private JsValue Then(JsValue receiver, JsValue[] args)
    {
        if (receiver.Data is not PromiseObject promise)
            throw JsException.TypeError("Promise.prototype.then requires a promise receiver");
        var capability = NewCapability(PromiseSpecies(promise));
        var reaction = new Reaction(capability, Argument(args, 0).Data as JsFunction, Argument(args, 1).Data as JsFunction);
        if (promise.Status == State.Pending)
            promise.Reactions.Add(reaction);
        else
            EnqueueReaction(reaction, promise.Status, promise.Result);
        return capability.Promise;
    }

    private JsValue Finally(JsValue receiver, JsValue[] args)
    {
        if (receiver.Data is not JsObject obj)
            throw JsException.TypeError("Promise.prototype.finally requires an object receiver");
        var constructor = PromiseSpecies(obj);
        var callback = Argument(args, 0);
        if (callback.Data is not JsFunction onFinally)
            return InvokeThen(receiver, callback, callback);

        JsValue CreateFinallyHandler(bool rejected) => JsValue.From(NewFunction(1, (_, reactionArgs) =>
        {
            var result = Resolve(constructor, onFinally.Call(JsValue.Undefined, []));
            var original = Argument(reactionArgs, 0);
            var forward = NewFunction(0, (_, _) => rejected ? throw new JsException(original) : original);
            return InvokeThen(result, JsValue.From(forward));
        }));

        return InvokeThen(receiver, CreateFinallyHandler(false), CreateFinallyHandler(true));
    }

    private JsObject PromiseSpecies(JsObject promise)
    {
        var constructor = promise.Get("constructor");
        if (constructor.Type != JsType.Undefined && constructor.Data is not JsObject)
            throw JsException.TypeError("Promise constructor property is not an object");
        // The object model does not yet store Symbol.species properties.
        return _constructor;
    }

    private JsValue Resolve(JsObject constructor, JsValue value)
    {
        if (value.Data is PromiseObject promise && ReferenceEquals(promise.Get("constructor").Data, constructor))
            return value;
        var capability = NewCapability(constructor);
        capability.Resolve.Call(JsValue.Undefined, [value]);
        return capability.Promise;
    }

    private Capability NewCapability(JsObject constructor)
    {
        if (constructor is not JsFunction ctor)
            throw JsException.TypeError("Promise receiver is not a constructor");
        var resolve = JsValue.Undefined;
        var reject = JsValue.Undefined;
        var executor = NewFunction(2, (_, args) =>
        {
            if (resolve.Type != JsType.Undefined || reject.Type != JsType.Undefined)
                throw JsException.TypeError("Promise capability executor has already been initialized");
            resolve = Argument(args, 0);
            reject = Argument(args, 1);
            return JsValue.Undefined;
        });
        var promise = ctor.Construct([JsValue.From(executor)]);
        return new Capability(promise, RequireFunction(resolve, "Promise resolver is not callable"), RequireFunction(reject, "Promise rejecter is not callable"));
    }

    private JsValue Combine(JsValue receiver, JsValue input, Combinator kind)
    {
        var constructor = RequireConstructorObject(receiver);
        var capability = NewCapability(constructor);
        try
        {
            var resolve = RequireFunction(constructor.Get("resolve"), "Promise.resolve is not callable");
            var results = new JsArray { Prototype = arrayPrototype };
            long remaining = 1;
            void FinishElement()
            {
                if (--remaining == 0)
                    capability.Resolve.Call(JsValue.Undefined, [JsValue.From(results)]);
            }

            foreach (var item in GetElements(input))
            {
                var next = resolve.Call(receiver, [item]);
                if (kind == Combinator.Race)
                {
                    InvokeThen(next, JsValue.From(capability.Resolve), JsValue.From(capability.Reject));
                    continue;
                }

                var index = results.ArrayLength.ToString(CultureInfo.InvariantCulture);
                results.Add(JsValue.Undefined);
                remaining++;
                var alreadyCalled = false;
                JsValue StoreResult(bool rejected, JsValue[] args)
                {
                    if (alreadyCalled) return JsValue.Undefined;
                    alreadyCalled = true;
                    var value = Argument(args, 0);
                    if (kind == Combinator.AllSettled)
                    {
                        var record = new JsObject { Prototype = objectPrototype };
                        record.DefineOwnProperty("status", PropertyDescriptor.Data(JsValue.From(rejected ? "rejected" : "fulfilled"), writable: true, enumerable: true, configurable: true));
                        record.DefineOwnProperty(rejected ? "reason" : "value", PropertyDescriptor.Data(value, writable: true, enumerable: true, configurable: true));
                        value = JsValue.From(record);
                    }
                    results.DefineOwnProperty(index, new PropertyDescriptor { Value = value });
                    FinishElement();
                    return JsValue.Undefined;
                }

                var onFulfilled = NewFunction(1, (_, args) => StoreResult(false, args));
                var onRejected = kind == Combinator.AllSettled
                    ? NewFunction(1, (_, args) => StoreResult(true, args))
                    : capability.Reject;
                InvokeThen(next, JsValue.From(onFulfilled), JsValue.From(onRejected));
            }

            if (kind != Combinator.Race)
                FinishElement();
        }
        catch (JsException error) { capability.Reject.Call(JsValue.Undefined, [ErrorValue(error)]); }
        return capability.Promise;
    }

    private static IEnumerable<JsValue> GetElements(JsValue input)
    {
        if (input.Data is JsArray array)
        {
            for (uint i = 0; i < array.ArrayLength; i++)
                yield return array.Get(i.ToString(CultureInfo.InvariantCulture));
            yield break;
        }
        if (input.Data is string text)
        {
            // String iteration combines surrogate pairs and preserves isolated surrogates.
            for (var i = 0; i < text.Length;)
            {
                var width = Rune.TryGetRuneAt(text, i, out var rune) ? rune.Utf16SequenceLength : 1;
                yield return JsValue.From(text.Substring(i, width));
                i += width;
            }
            yield break;
        }
        throw JsException.TypeError("Promise combinator requires an iterable");
    }

    private static JsValue InvokeThen(JsValue receiver, params JsValue[] args)
    {
        if (receiver.Data is not JsObject obj)
            throw JsException.TypeError("Cannot invoke then on a non-object");
        return RequireFunction(obj.Get("then"), "then is not callable").Call(receiver, args);
    }

    private JsValue ErrorValue(JsException error)
    {
        attachErrorPrototype(error);
        return error.Value;
    }

    private static JsValue Argument(JsValue[] args, int index) => index < args.Length ? args[index] : JsValue.Undefined;

    private static JsObject RequireConstructorObject(JsValue receiver) => receiver.Data as JsObject
        ?? throw JsException.TypeError("Promise receiver is not an object");

    private static JsFunction RequireFunction(JsValue value, string message) => value.Data as JsFunction
        ?? throw JsException.TypeError(message);

    private JsFunction NewFunction(int arity, Func<JsValue, JsValue[], JsValue> call, string name = "")
    {
        var function = new JsFunction(call, arity) { Prototype = objectPrototype };
        DefineName(function, name);
        return function;
    }

    private static void DefineName(JsFunction function, string name) =>
        function.DefineOwnProperty("name", PropertyDescriptor.Data(JsValue.From(name), configurable: true));

    private void DefineMethod(JsObject target, string name, int arity, Func<JsValue, JsValue[], JsValue> call)
    {
        var function = NewFunction(arity, call, name);
        target.DefineOwnProperty(name, PropertyDescriptor.Data(JsValue.From(function), writable: true, configurable: true));
    }
}
