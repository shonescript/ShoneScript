// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Tests;

public sealed class PythonClassTests
{
    [Fact]
    public void BindsMethodsAndUpdatesTheReceiverOnce()
    {
        var result = new PythonEngine().Evaluate("""
            calls = []
            class Counter:
                total = 0
                def __init__(self, *, start):
                    self.total = start
                def add(self, step=1):
                    self.total += step
                    return self.total
            counter = Counter(start=10)
            bound = counter.add
            def receiver():
                calls.append(1)
                return counter
            receiver().total += 2
            (bound(step=3), Counter.add(counter), Counter.total, calls)
            """);

        Assert.Equal("(15, 16, 0, [1])", result.Repr());
    }

    [Fact]
    public void ResolvesDiamondInheritanceAndObservesClassMutations()
    {
        var result = new PythonEngine().Evaluate("""
            class Root:
                value = 'root'
                def __init__(self, tag): self.tag = tag
            class Left(Root): pass
            class Right(Root): value = 'right'
            class Child(Left, Right): pass
            child = Child('inherited')
            before = child.value
            Left.value = 'left'
            (before, child.value, child.tag, child.__class__ is Child, [c.__name__ for c in Child.__mro__])
            """);

        Assert.Equal("('right', 'left', 'inherited', True, ['Child', 'Left', 'Right', 'Root', 'object'])", result.Repr());
    }

    [Fact]
    public void ClassNamespaceIsUsedForDefaultsButNotAsAMethodClosure()
    {
        var result = new PythonEngine().Evaluate("""
            def make():
                value = 'closure'
                class Item:
                    value = 'class'
                    def get(self, default=value):
                        return (default, value, self.value)
                return Item
            make()().get()
            """);

        Assert.Equal("('class', 'closure', 'class')", result.Repr());
    }

    [Fact]
    public void WithBindsInheritedSpecialMethodsDespiteInstanceShadowing()
    {
        var result = new PythonEngine().Evaluate("""
            events = []
            class Base:
                def __enter__(self): return self
                def __exit__(self, kind, error, trace):
                    events.append((kind is ValueError, error.args, trace is None))
                    return True
            class Manager(Base): pass
            manager = Manager()
            manager.__exit__ = None
            with manager as entered:
                events.append(entered is manager)
                raise ValueError('suppressed')
            events
            """);

        Assert.Equal("[True, (True, ('suppressed',), True)]", result.Repr());
    }

    [Theory]
    [InlineData("class Bad(42): pass", "TypeError")]
    [InlineData("class A: pass\nclass Bad(A, A): pass", "TypeError")]
    [InlineData("class A: pass\nclass B(A): pass\nclass Bad(A, B): pass", "TypeError")]
    [InlineData("class A: pass\nA(1)", "TypeError")]
    [InlineData("class A:\n    def __init__(self): return 1\nA()", "TypeError")]
    [InlineData("class A: pass\nA().missing", "AttributeError")]
    [InlineData("def f():\n    class A: return 42", "SyntaxError")]
    [InlineData("while False:\n    class A: break", "SyntaxError")]
    public void RejectsInvalidClassesOrObjectOperations(string source, string errorName)
    {
        Assert.Equal(errorName, Assert.Throws<PythonException>(() => new PythonEngine().Evaluate(source)).ErrorName);
    }
}
