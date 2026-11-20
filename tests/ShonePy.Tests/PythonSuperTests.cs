// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Tests;

public sealed class PythonSuperTests
{
    [Fact]
    public void ZeroArgumentSuperUsesTheDefiningClassAndCooperatesAcrossTheMro()
    {
        var result = new PythonEngine().Evaluate("""
            class Root:
                def __init__(self, *, value): self.value = value
                def order(self): return ['root', self.value]
            class Left(Root):
                def __init__(self, *, value): super().__init__(value=value)
                def order(receiver): return ['left'] + super().order()
            class Right(Root):
                def order(self): return ['right'] + super().order()
            class Diamond(Left, Right): pass
            Left = None
            obj = Diamond(value=42)
            obj.order = None
            method = super(Diamond, obj).order
            method()
            """);

        Assert.Equal("['left', 'right', 'root', 42]", result.Repr());
    }

    [Fact]
    public void BindsPropertiesAndClassMethodsAgainstTheActualReceiver()
    {
        var result = new PythonEngine().Evaluate("""
            class Base:
                label = 'original'
                @property
                def value(self): return 5
                @classmethod
                def name(cls): return cls.__name__
                @staticmethod
                def twice(value): return 2 * value
                def method(self): return self
            class Child(Base):
                @classmethod
                def name(cls): return super().name()
            class Leaf(Child): pass
            obj = Leaf()
            with_instance = super(Child, obj)
            with_class = super(Child, Leaf)
            Base.label = 'updated'
            (Leaf.name(), with_instance.value, with_class.value is Base.value,
                with_class.method(obj) is obj, with_instance.twice(6), with_instance.label,
                with_instance.__self__ is obj, with_class.__self_class__ is Leaf,
                with_instance.__thisclass__ is Child)
            """);

        Assert.Equal("('Leaf', 5, True, True, 12, 'updated', True, True, True)", result.Repr());
    }

    [Fact]
    public void PreservesSuperContextInResumedGeneratorMethods()
    {
        var result = new PythonEngine().Evaluate("""
            class Base:
                def value(self): return 17
            class Child(Base):
                def values(self):
                    yield 1
                    yield super().value()
            it = Child().values()
            (next(it), next(it))
            """);

        Assert.Equal("(1, 17)", result.Repr());
    }

    [Theory]
    [InlineData("super()", "RuntimeError")]
    [InlineData("def helper(arg): return super()\nhelper(A())", "RuntimeError")]
    [InlineData("class B(A):\n    @staticmethod\n    def helper(): return super()\nB.helper()", "RuntimeError")]
    [InlineData("super(3, A())", "TypeError")]
    [InlineData("super(A, object())", "TypeError")]
    [InlineData("super(A, object)", "TypeError")]
    [InlineData("super(A, A(), A())", "TypeError")]
    [InlineData("super(A, obj=A())", "TypeError")]
    [InlineData("super(A, A()).missing", "AttributeError")]
    public void RejectsInvalidSuperCalls(string source, string errorName)
    {
        var engine = new PythonEngine();
        engine.Evaluate("class A: pass");
        Assert.Equal(errorName, Assert.Throws<PythonException>(() => engine.Evaluate(source)).ErrorName);
    }

    [Theory]
    [InlineData("super(C0, obj)")]
    [InlineData("proxy.missing")]
    public void ChargesForMroScansWithoutLettingPythonCatchABudgetAbort(string expression)
    {
        var engine = new PythonEngine(new PythonOptions { MaximumSteps = 35 });
        engine.Evaluate("class C0: pass");
        for (int i = 1; i <= 50; i++) engine.Evaluate($"class C{i}(C{i - 1}): pass");
        engine.Evaluate("obj = C50()\nproxy = super(C50, obj)");

        var error = Assert.Throws<PythonException>(() => engine.Evaluate($"try:\n    {expression}\nexcept:\n    pass"));
        Assert.Equal("RuntimeError", error.ErrorName);
        Assert.Contains("maximum execution steps exceeded", error.Message);
    }
}
