// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Tests;

public sealed class PythonAsyncWithTests
{
    [Fact]
    public void AwaitsManagersInOrderAndUnpacksTargetsBeforeEnteringTheNextManager()
    {
        var engine = new PythonEngine();
        var coroutine = engine.Evaluate("""
            events = []
            class Manager:
                def __init__(self, value): self.value = value
                async def __aenter__(self):
                    events.append(('enter', self.value))
                    return (self.value, self.value + 1)
                async def __aexit__(self, kind, error, traceback):
                    events.append(('exit', self.value, kind, error, traceback))
            async def make(value):
                events.append(('make', value))
                manager = Manager(value)
                manager.__aenter__ = None
                manager.__aexit__ = None
                return manager
            async def run():
                async with (
                    await make(1) as (a, b),
                    await make(b) as pair,
                ):
                    def read():
                        nonlocal a
                        return (a, b, pair)
                    events.append(read())
                return events
            run()
            """);

        Assert.Equal("[]", engine.GetGlobal("events").Repr());
        Assert.Equal("[('make', 1), ('enter', 1), ('make', 2), ('enter', 2), (1, 2, (2, 3)), ('exit', 2, None, None, None), ('exit', 1, None, None, None)]",
            engine.RunCoroutine(coroutine).Repr());
    }

    [Fact]
    public void AwaitsExitForContinueBreakAndReturnWithoutSuppressingThem()
    {
        var engine = new PythonEngine();
        var coroutine = engine.Evaluate("""
            events = []
            async def enter(): return 42
            async def leave(kind, error, traceback):
                events.append((kind, error, traceback))
                return True
            class Manager:
                def __aenter__(self): return enter()
                def __aexit__(self, kind, error, traceback): return leave(kind, error, traceback)
            async def run():
                for i in [1, 2]:
                    async with Manager() as value:
                        if i == 1: continue
                        break
                else: events.append('unexpected loop else')
                async with Manager(): return value
            run()
            """);

        Assert.Equal("42", engine.RunCoroutine(coroutine).Repr());
        Assert.Equal("[(None, None, None), (None, None, None), (None, None, None)]", engine.GetGlobal("events").Repr());
    }

    [Fact]
    public void OnlyExitsManagersThatEnteredAndCanSuppressAnInnerEntryOrBindingFailure()
    {
        var engine = new PythonEngine();
        var coroutine = engine.Evaluate("""
            events = []
            original = ValueError('entry failed')
            class Outer:
                async def __aenter__(self): pass
                async def __aexit__(self, kind, error, traceback):
                    events.append((kind is ValueError, error is original, traceback is None))
                    return 1
            class EntryFails:
                async def __aenter__(self): raise original
                async def __aexit__(self, kind, error, traceback): events.append('unexpected exit')
            class UnpackFails:
                async def __aenter__(self): return (1,)
                async def __aexit__(self, kind, error, traceback):
                    events.append(('unpack', kind is ValueError))
                    return False
            async def run():
                async with Outer(), EntryFails(): events.append('unexpected body')
                async with Outer(), UnpackFails() as (a, b): events.append('unexpected body')
                return events
            run()
            """);

        Assert.Equal("[(True, True, True), ('unpack', True), (True, False, True)]", engine.RunCoroutine(coroutine).Repr());
    }

    [Fact]
    public void PreservesTheActiveExceptionAndChainsAReplacementAcrossAwaitedExits()
    {
        var engine = new PythonEngine();
        var coroutine = engine.Evaluate("""
            original = ValueError('body failed')
            replacement = KeyError('exit failed')
            events = []
            class Reraise:
                async def __aenter__(self): pass
                async def __aexit__(self, kind, error, traceback): raise
            class Replace:
                async def __aenter__(self): pass
                async def __aexit__(self, kind, error, traceback):
                    events.append(error is original)
                    raise replacement
            class Observe:
                async def __aenter__(self): pass
                async def __aexit__(self, kind, error, traceback):
                    events.append((kind is KeyError, error is replacement, error.__context__ is original))
                    return False
            async def run():
                try:
                    async with Observe(), Replace(), Reraise(): raise original
                except KeyError as error: return (error is replacement, events)
            run()
            """);

        Assert.Equal("(True, [True, (True, True, True)])", engine.RunCoroutine(coroutine).Repr());
    }

    [Theory]
    [InlineData("SyncOnly", "TypeError", "__aexit__", "[]")]
    [InlineData("MissingExit", "TypeError", "__aexit__", "[]")]
    [InlineData("MissingEnter", "TypeError", "__aenter__", "[]")]
    [InlineData("BrokenLookup", "AttributeError", "exit descriptor failed", "[]")]
    [InlineData("InvalidEnter", "TypeError", "from __aenter__ that does not implement __await__: int", "[]")]
    [InlineData("InvalidExit", "TypeError", "from __aexit__ that does not implement __await__: bool", "['entered', 'body']")]
    [InlineData("CustomAwaitable", "NotImplementedError", "custom awaitables are not supported", "[]")]
    public void RejectsMissingMethodsAndUnsupportedAwaitResults(string manager, string kind, string message, string events)
    {
        var engine = new PythonEngine();
        var coroutine = engine.Evaluate($"""
            events = []
            class SyncOnly:
                def __enter__(self): events.append('wrong protocol')
                def __exit__(self, kind, error, traceback): pass
            class MissingExit:
                async def __aenter__(self): events.append('unexpected enter')
            class MissingEnter:
                async def __aexit__(self, kind, error, traceback): events.append('unexpected exit')
            class BrokenLookup(MissingExit):
                @property
                def __aexit__(self): raise AttributeError('exit descriptor failed')
            class InvalidEnter:
                def __aenter__(self): return 42
                async def __aexit__(self, kind, error, traceback): events.append('unexpected exit')
            class InvalidExit:
                async def __aenter__(self): events.append('entered')
                def __aexit__(self, kind, error, traceback): return True
            class Awaitable:
                def __await__(self): events.append('unexpected custom await')
            class CustomAwaitable(InvalidEnter):
                def __aenter__(self): return Awaitable()
            async def run():
                async with {manager}(): events.append('body')
            run()
            """, "async_with.py");

        var error = Assert.Throws<PythonException>(() => engine.RunCoroutine(coroutine));
        Assert.Equal(kind, error.ErrorName);
        Assert.Contains(message, error.Message);
        Assert.Equal("async_with.py", error.FileName);
        Assert.Equal(events, engine.GetGlobal("events").Repr());
    }

    [Fact]
    public void LooksUpExitBeforeEntryAndUsesTheCachedMethod()
    {
        var engine = new PythonEngine();
        var coroutine = engine.Evaluate("""
            events = []
            async def replacement(self, kind, error, traceback): events.append('replacement')
            class Manager:
                async def __aenter__(self): Manager.__aexit__ = replacement
                async def __aexit__(self, kind, error, traceback): events.append('original')
            async def run():
                async with Manager(): pass
                async with Manager(): pass
                return events
            run()
            """);

        Assert.Equal("['original', 'replacement']", engine.RunCoroutine(coroutine).Repr());
    }

    [Fact]
    public void BudgetAbortInsideAwaitedEntrySkipsCleanupAndRestoresTheEngine()
    {
        var engine = new PythonEngine(new PythonOptions { MaximumSteps = 100 });
        var coroutine = engine.Evaluate("""
            events = []
            class Outer:
                async def __aenter__(self): events.append('entered')
                async def __aexit__(self, kind, error, traceback): events.append('exited')
            class Endless(Outer):
                async def __aenter__(self):
                    while True: pass
            async def run():
                async with Outer(), Endless(): pass
            run()
            """);

        var error = Assert.Throws<PythonException>(() => engine.RunCoroutine(coroutine));
        Assert.Equal("maximum execution steps exceeded", error.Message);
        Assert.Equal("['entered']", engine.GetGlobal("events").Repr());
        Assert.Equal("42", engine.Evaluate("42").Repr());
    }
}
