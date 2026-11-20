// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Tests;

public sealed class PythonDescriptorTests
{
    [Fact]
    public void BindsStaticAndClassMethodsAcrossInheritanceAndAllowsInstanceShadowing()
    {
        var result = new PythonEngine().Evaluate("""
            class Base:
                @staticmethod
                def calc(*, value): return value + 1
                @classmethod
                def who(cls, *, suffix): return cls.__name__ + suffix
            class Child(Base): pass
            child = Child()
            original = child.who
            output = (Base.calc is Child.calc, child.calc(value=3), Base.who(suffix='!'), original(suffix='?'))
            child.calc = 'static shadow'
            child.who = 'class shadow'
            (output, child.calc, child.who, original.__self__ is Child, original.__func__ is Base.who.__func__)
            """);

        Assert.Equal("((True, 4, 'Base!', 'Child?'), 'static shadow', 'class shadow', True, True)", result.Repr());
    }

    [Fact]
    public void PropertiesReadWriteAndDeleteBeforeConsultingTheInstance()
    {
        var result = new PythonEngine().Evaluate("""
            events = []
            class Base:
                @property
                def value(self):
                    'counter value'
                    events.append('get')
                    return self.backing
                @value.setter
                def value(self, item):
                    events.append('set')
                    self.backing = item
                @value.deleter
                def value(self):
                    events.append('delete')
                    del self.backing
            class Child(Base):
                value = 0
            child = Child()
            child.value = 'shadow'
            child.backing = 40
            del Child.value
            assert Child.value is Base.value
            child.value += 2
            before = child.value
            del child.value
            del Base.value
            (before, child.value, events)
            """);

        Assert.Equal("(42, 'shadow', ['get', 'set', 'get', 'delete'])", result.Repr());
    }

    [Fact]
    public void PropertyCopiesKeepOtherAccessorsAndReplaceAnInferredDocstring()
    {
        var result = new PythonEngine().Evaluate("""
            def read(self):
                'first'
                return 1
            def other(self):
                'second'
                return 2
            def write(self, value): self.changed = value
            original = property(fget=read, fset=write)
            changed = original.getter(other)
            class Item:
                value = changed
            item = Item()
            item.value = 42
            (original is changed, original.fget is read, changed.fset is write, changed.fdel,
             original.__doc__, changed.__doc__, item.value, item.changed)
            """);

        Assert.Equal("(False, True, True, None, 'first', 'second', 2, 42)", result.Repr());
    }

    [Fact]
    public void SpecialMethodLookupBindsDescriptorsAndIgnoresInstanceEntries()
    {
        var result = new PythonEngine().Evaluate("""
            trace = []
            class Base:
                @staticmethod
                def __init__(): trace.append('init')
                @classmethod
                def __enter__(cls): return cls.__name__
                @staticmethod
                def __exit__(kind, error, traceback): trace.append(kind is None)
            class Child(Base): pass
            item = Child()
            item.__enter__ = 'shadow'
            with item as name:
                trace.append(name)
            trace
            """);

        Assert.Equal("['init', 'Child', True]", result.Repr());
    }

    [Theory]
    [InlineData("class C: x = property()\nC().x", "AttributeError")]
    [InlineData("class C: x = property()\nc = C()\nc.x = 1", "AttributeError")]
    [InlineData("class C: x = property()\nc = C()\ndel c.x", "AttributeError")]
    [InlineData("class C: x = property(42)\nC().x", "TypeError")]
    [InlineData("class C: x = staticmethod(42)\nC().x()", "TypeError")]
    [InlineData("classmethod()", "TypeError")]
    [InlineData("property(None, fget=None)", "TypeError")]
    public void InvalidDescriptorOperationsRaisePythonExceptions(string source, string errorName)
    {
        Assert.Equal(errorName, Assert.Throws<PythonException>(() => new PythonEngine().Evaluate(source)).ErrorName);
    }
}
