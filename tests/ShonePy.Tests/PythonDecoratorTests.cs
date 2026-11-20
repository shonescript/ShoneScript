// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Tests;

public sealed class PythonDecoratorTests
{
    [Fact]
    public void EvaluatesFromTopToBottomAndAppliesInReverseAfterFunctionDefaults()
    {
        var result = new PythonEngine().Evaluate("""
            events = []
            def mark(value):
                events.append(value)
                return value
            def decorator(label):
                mark('eval ' + label)
                def apply(function):
                    mark('apply ' + label)
                    def wrapper():
                        return label + function() + label
                    return wrapper
                return apply

            @decorator('A')
            @decorator('B')
            def decorated(text=mark('default')):
                return text
            (decorated(), events)
            """);

        Assert.Equal("('ABdefaultBA', ['eval A', 'eval B', 'default', 'apply B', 'apply A'])", result.Repr());
    }

    [Fact]
    public void DecoratesAConstructedClassAndBindsTheReplacement()
    {
        var result = new PythonEngine().Evaluate("""
            events = []
            class Base: pass
            def base():
                events.append('base')
                return Base
            def choose():
                events.append('eval decorator')
                def replace(cls):
                    events.append((cls.value, cls.__bases__[0] is Base))
                    return cls()
                return replace

            @choose()  # The replacement need not be callable.
            # Comment and blank lines can separate decorators from the definition.

            class Item(base()):
                events.append('body')
                value = 42
            (Item.value, Item.__class__.__name__, events)
            """);

        Assert.Equal("(42, 'Item', ['eval decorator', 'base', 'body', (42, True)])", result.Repr());
    }

    [Fact]
    public void ResolvesMethodDecoratorsInTheClassNamespaceAndBindsTheWrapper()
    {
        var result = new PythonEngine().Evaluate("""
            def double(function):
                def wrapper(self):
                    return function(self) * 2
                return wrapper

            class Item:
                decorators = [double]
                @decorators[0]
                def value(self):
                    return 21
            Item().value()
            """);

        Assert.Equal("42", result.Repr());
    }

    [Fact]
    public void LeavesPreviousBindingsIntactWhenEvaluationOrApplicationFails()
    {
        var result = new PythonEngine().Evaluate("""
            events = []
            def fail():
                raise ValueError('eval failed')
            def apply():
                events.append('eval outer')
                def outer(value):
                    events.append('apply outer')
                    return value
                return outer
            def reject(value):
                events.append(item)
                raise TypeError('apply failed')

            f = 'old function'
            try:
                @fail()
                @apply()
                def f(x=events.append('default')): pass
            except ValueError:
                events.append(f)

            item = 'old class'
            try:
                @apply()
                @reject
                class item:
                    events.append('body')
            except TypeError:
                events.append(item)
            events
            """);

        Assert.Equal("['old function', 'eval outer', 'body', 'old class', 'old class']", result.Repr());
    }

    [Fact]
    public void RejectsNonCallableDecorator()
    {
        var error = Assert.Throws<PythonException>(() => new PythonEngine().Evaluate("""
            @42
            def f(): pass
            """));

        Assert.Equal("TypeError", error.ErrorName);
    }

    [Theory]
    [InlineData("@decorate")]
    [InlineData("@\ndef f(): pass")]
    [InlineData("@decorate def f(): pass")]
    [InlineData("@decorate\nvalue = 42")]
    [InlineData("if True: @decorate\n    def f(): pass")]
    public void RejectsMalformedDecoratorSyntax(string source)
    {
        var error = Assert.Throws<PythonException>(() => new PythonEngine().Evaluate(source));
        Assert.Equal("SyntaxError", error.ErrorName);
    }
}
