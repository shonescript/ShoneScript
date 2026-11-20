// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Tests;

public sealed class PythonCoroutineTests
{
    [Fact]
    public void BindsArgumentsImmediatelyAndRunsTheBodyOnlyWhenDriven()
    {
        var engine = new PythonEngine();
        var coroutine = engine.Evaluate("""
            effects = []
            async def leaf(x, /, *, y=1):
                effects.append(x)
                return x + y
            async def main():
                result = await leaf(20, y=1) ** 2
                return result + await leaf(1)
            main()
            """);

        Assert.Equal(PyType.Coroutine, coroutine.Type);
        Assert.Equal("[]", engine.GetGlobal("effects").Repr());
        Assert.Equal("TypeError", Assert.Throws<PythonException>(() => engine.Evaluate("leaf()")).ErrorName);
        Assert.Equal("443", engine.RunCoroutine(coroutine).Repr());
        Assert.Equal("[20, 1]", engine.GetGlobal("effects").Repr());
        Assert.Equal("RuntimeError", Assert.Throws<PythonException>(() => engine.RunCoroutine(coroutine)).ErrorName);
        Assert.Equal("[20, 1]", engine.GetGlobal("effects").Repr());
    }

    [Fact]
    public void PreservesLexicalScopesAndOrdinaryGeneratorAndMethodBehavior()
    {
        var engine = new PythonEngine();
        var coroutine = engine.Evaluate("""
            completed = 0
            def setup(start):
                def stream(): yield 2
                def decorate(fn): return fn
                @decorate
                async def update():
                    nonlocal start
                    global completed
                    start += next(stream())
                    completed += 1
                    return start
                return update
            update = setup(38)
            class Base:
                async def value(self): return await update()
            class Child(Base):
                async def value(self):
                    await super().value()
                    return await super().value()
            Child().value()
            """);

        Assert.Equal("42", engine.RunCoroutine(coroutine).Repr());
        Assert.Equal("2", engine.GetGlobal("completed").Repr());
        Assert.Equal("NameError", Assert.Throws<PythonException>(() => engine.GetGlobal("start")).ErrorName);
    }

    [Fact]
    public void ReportsReasonAndSourceWhenACoroutineFailsAndCannotRestartIt()
    {
        var engine = new PythonEngine();
        var coroutine = engine.Evaluate("async def main():\n    return await 42\nmain()", "async.py");
        var error = Assert.Throws<PythonException>(() => engine.RunCoroutine(coroutine));
        Assert.Equal("TypeError", error.ErrorName);
        Assert.Equal("async.py", error.FileName);
        Assert.Equal(2, error.Line);
        Assert.Equal("RuntimeError", Assert.Throws<PythonException>(() => engine.RunCoroutine(coroutine)).ErrorName);
    }

    [Fact]
    public void RejectsForeignCoroutinesWithoutConsumingThem()
    {
        var owner = new PythonEngine();
        var coroutine = owner.Evaluate("async def main(): pass\nmain()");
        var other = new PythonEngine();
        Assert.Equal("TypeError", Assert.Throws<PythonException>(() => other.RunCoroutine(coroutine)).ErrorName);
        Assert.Equal(PyValue.None, owner.RunCoroutine(coroutine));
    }

    [Fact]
    public void RejectsReentryWithoutCorruptingExecutionState()
    {
        var engine = new PythonEngine();
        var coroutine = engine.Evaluate("""
            async def main():
                try: await pending
                except ValueError: return 42
            pending = main()
            pending
            """);
        Assert.Equal("42", engine.RunCoroutine(coroutine).Repr());
    }

    [Theory]
    [InlineData("return await recursive()", "RecursionError", "maximum recursion depth exceeded", 10000, 12)]
    [InlineData("while True: pass", "RuntimeError", "maximum execution steps exceeded", 100, 128)]
    [InlineData("return sum(range(10000))", "RuntimeError", "maximum execution steps exceeded", 100, 128)]
    public void SharesBudgetAcrossAwaitFramesAndRestoresTheEngine(string body, string kind, string message, int steps, int depth)
    {
        var engine = new PythonEngine(new PythonOptions { MaximumSteps = steps, MaximumCallDepth = depth });
        var coroutine = engine.Evaluate($"""
            async def recursive():
                {body}
            async def main():
                try: return await recursive()
                except: return -1
            main()
            """);
        var error = Assert.Throws<PythonException>(() => engine.RunCoroutine(coroutine));
        Assert.Equal(kind, error.ErrorName);
        Assert.Equal(message, error.Message);
        Assert.Equal("42", engine.Evaluate("42").Repr());
    }

    [Fact]
    public void RejectsCustomAwaitablesWithoutRunningTheirProtocol()
    {
        var engine = new PythonEngine();
        var coroutine = engine.Evaluate("""
            class Custom:
                def __await__(self): raise ValueError()
            async def main(): return await Custom()
            main()
            """);
        var error = Assert.Throws<PythonException>(() => engine.RunCoroutine(coroutine));
        Assert.Equal("NotImplementedError", error.ErrorName);
        Assert.Contains("custom awaitables", error.Message);
    }

    [Fact]
    public void AsyncForUsesClassProtocolAndPreservesUnpackingAndLoopControl()
    {
        var engine = new PythonEngine();
        var coroutine = engine.Evaluate("""
            effects = []
            class Iterator:
                def __init__(self, stop):
                    self.index = 0
                    self.stop = stop
                async def __anext__(self):
                    if self.index == self.stop: raise StopAsyncIteration
                    self.index += 1
                    return (self.index, self.index * 10)
            class Source:
                def __init__(self, stop): self.stop = stop
                def __aiter__(self):
                    effects.append(self.stop)
                    return Iterator(self.stop)
            async def make(stop):
                item = Source(stop)
                item.__aiter__ = None
                return item
            async def main():
                items = []
                async for index, value in await make(3):
                    if index == 2: continue
                    items.append(value)
                else: items.append(index)
                async for item in Source(0): pass
                else: items.append('empty')
                async for item in Source(4): break
                else: items.append('break else')
                return (items, effects)
            main()
            """);

        Assert.Equal("([10, 30, 3, 'empty'], [3, 0, 4])", engine.RunCoroutine(coroutine).Repr());
    }

    [Theory]
    [InlineData("async for item in Source(): raise StopAsyncIteration('body')", "body")]
    [InlineData("async for receiver.value in Source(): pass", "target")]
    [InlineData("async for item in BrokenSource(): pass", "aiter")]
    [InlineData("async for item in fail(): pass", "iterable")]
    [InlineData("async for item in Empty(): pass\n    else: raise StopAsyncIteration('else')", "else")]
    [InlineData("async for item in Invalid(): pass", "'async for' received an invalid object from __anext__: int", "TypeError")]
    public void AsyncForDoesNotTreatErrorsOutsideRetrievalAsExhaustion(string loop, string message, string kind = "StopAsyncIteration")
    {
        var engine = new PythonEngine();
        var coroutine = engine.Evaluate($"""
            class Source:
                def __aiter__(self): return self
                async def __anext__(self): return 42
            class Empty(Source):
                def __anext__(self): raise StopAsyncIteration
            class Invalid(Source):
                def __anext__(self): return 42
            class BrokenSource(Source):
                def __aiter__(self): raise StopAsyncIteration('aiter')
            def fail(): raise StopAsyncIteration('iterable')
            class Target:
                @property
                def value(self): return None
                @value.setter
                def value(self, item): raise StopAsyncIteration('target')
            receiver = Target()
            async def main():
                {loop}
            main()
            """);

        var error = Assert.Throws<PythonException>(() => engine.RunCoroutine(coroutine));
        Assert.Equal(kind, error.ErrorName);
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void AsyncNextCallsTheMethodImmediatelyAndAppliesDefaultOnlyWhenAwaited()
    {
        var engine = new PythonEngine();
        var coroutine = engine.Evaluate("""
            events = []
            async def value(item):
                events.append('await')
                if item == 2: raise StopAsyncIteration(item)
                return item
            class Iterator:
                def __aiter__(self): return self
                def __anext__(self):
                    events.append('call')
                    return value(len(events))
            class Empty:
                def __anext__(self): raise StopAsyncIteration('sync')
            async def main():
                source = Iterator()
                item = aiter(source)
                pending = anext(item)
                stopped = anext(item, 99)
                events.append('between')
                first = await pending
                second = await stopped
                try: anext(Empty(), None)
                except StopAsyncIteration as error: events.append(str(error))
                return (item is source, first, second, events)
            main()
            """);

        Assert.Equal("(True, 1, 99, ['call', 'call', 'between', 'await', 'await', 'sync'])", engine.RunCoroutine(coroutine).Repr());
    }

    [Theory]
    [InlineData("aiter(42)")]
    [InlineData("aiter(Invalid())")]
    [InlineData("anext(42)")]
    [InlineData("anext()")]
    [InlineData("anext(42, 0, 1)")]
    public void AsyncIteratorBuiltinsRejectInvalidArguments(string expression)
    {
        var engine = new PythonEngine();
        var error = Assert.Throws<PythonException>(() => engine.Evaluate($"""
            class Invalid:
                def __aiter__(self): return 42
            {expression}
            """));
        Assert.Equal("TypeError", error.ErrorName);
    }

    [Fact]
    public void AsyncForChargesTheBudgetAndRestoresTheEngine()
    {
        var engine = new PythonEngine(new PythonOptions { MaximumSteps = 100 });
        var coroutine = engine.Evaluate("""
            class Endless:
                def __aiter__(self): return self
                async def __anext__(self): return 1
            async def main():
                try:
                    async for item in Endless(): continue
                except: return -1
            main()
            """);

        var error = Assert.Throws<PythonException>(() => engine.RunCoroutine(coroutine));
        Assert.Equal("maximum execution steps exceeded", error.Message);
        Assert.Equal("42", engine.Evaluate("42").Repr());
    }

    [Fact]
    public void AsyncComprehensionChargesTheBudgetAndRestoresTheEngine()
    {
        var engine = new PythonEngine(new PythonOptions { MaximumSteps = 100 });
        var coroutine = engine.Evaluate("""
            class Endless:
                def __aiter__(self): return self
                async def __anext__(self): return 1
            item = 42
            async def main():
                try: return [item async for item in Endless() if False]
                except: return -1
            main()
            """);

        var error = Assert.Throws<PythonException>(() => engine.RunCoroutine(coroutine));
        Assert.Equal("maximum execution steps exceeded", error.Message);
        Assert.Equal("42", engine.Evaluate("item").Repr());
    }

    [Theory]
    [InlineData("await main()", "outside async function")]
    [InlineData("async def outer():\n    def inner(): return await work()", "outside async function")]
    [InlineData("async def outer():\n    class Inner:\n        a = await work()", "outside async function")]
    [InlineData("async for x in items: pass", "'async for' outside async function")]
    [InlineData("async def outer():\n    def inner():\n        async for x in items: pass", "'async for' outside async function")]
    [InlineData("async def outer():\n    class Inner:\n        async for x in items: pass", "'async for' outside async function")]
    [InlineData("async with lock: pass", "'async with' outside async function")]
    [InlineData("async def outer():\n    def inner():\n        async with lock: pass", "'async with' outside async function")]
    [InlineData("async def outer():\n    class Inner:\n        async with lock: pass", "'async with' outside async function")]
    [InlineData("async def f(): yield 1", "async generators are not supported")]
    [InlineData("async def f(): yield from items", "async generators are not supported")]
    [InlineData("[x async for x in items]", "asynchronous comprehension outside of an asynchronous function")]
    [InlineData("async def outer():\n    def inner(): return {x async for x in items}", "asynchronous comprehension outside of an asynchronous function")]
    [InlineData("(x async for x in items)", "async generator expressions are not supported")]
    [InlineData("async def f(): return list(await g(x) for x in items)", "async generator expressions are not supported")]
    [InlineData("async def f(): return (x for x in items if await g(x))", "async generator expressions are not supported")]
    [InlineData("async def f(): return (x for y in items for x in await g(y))", "async generator expressions are not supported")]
    [InlineData("async def f(): return ([x async for x in items] for y in items)", "async generator expressions are not supported")]
    [InlineData("async def f():\n    def inner() -> await g(): pass", "await in annotations is not supported")]
    [InlineData("async def f(): return await -1", "expected primary expression")]
    [InlineData("async def await(): pass", "expected identifier")]
    public void RejectsUnsupportedAsyncSyntaxDuringParsing(string source, string message)
    {
        var engine = new PythonEngine();
        var error = Assert.Throws<PythonException>(() => engine.ParseScript(source, "invalid.py"));
        Assert.Equal("SyntaxError", error.ErrorName);
        Assert.Equal("invalid.py", error.FileName);
        Assert.Contains(message, error.Message);
    }
}
