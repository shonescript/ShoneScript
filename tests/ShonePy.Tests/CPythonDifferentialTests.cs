// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ShonePy.Tests;

public sealed class CPythonDifferentialTests
{
    private const string ExecutableVariable = "SHONEPY_CPYTHON";

    private const string ReferenceDriver = """
        import ast
        import contextlib
        import io
        import json
        import sys

        if sys.implementation.name != 'cpython':
            raise RuntimeError('SHONEPY_CPYTHON must refer to CPython')

        if sys.argv[1] == 'coroutine' and (sys.version_info[:5] != (3, 15, 0, 'candidate', 3)
                                           or sys._git[2] != '8a8eb0b'):
            raise RuntimeError('Coroutine differential tests require CPython v3.15.0rc3 (8a8eb0b)')

        # ShonePy returns a trailing expression's value for a script. Capture it
        # in the same compiled module so parse errors still precede execution.
        source = sys.stdin.buffer.read().decode('utf-8')
        output = io.StringIO()
        namespace = {'__name__': '__main__'}
        result = exception = None
        try:
            with contextlib.redirect_stdout(output):
                module = ast.parse(source, '<differential>')
                if module.body and isinstance(module.body[-1], ast.Expr):
                    last = module.body[-1]
                    module.body[-1] = ast.copy_location(
                        ast.Assign(targets=[ast.Name(id='__shonepy_result__', ctx=ast.Store())],
                                   value=last.value), last)
                exec(compile(ast.fix_missing_locations(module), '<differential>', 'exec'), namespace)
                value = namespace.get('__shonepy_result__')
                if sys.argv[1] == 'coroutine':
                    import asyncio
                    value = asyncio.run(value)
                result = repr(value)
        except BaseException as error:
            exception = type(error).__name__
        print(json.dumps({'Stdout': output.getvalue(), 'Result': result, 'Exception': exception,
                          'Version': list(sys.version_info[:2])}))
        """;

    [CPythonTheory]
    [InlineData("integer arithmetic", "(-7 // 3, -7 % 3, 2 ** 80)")]
    [InlineData("immutable bytes", "data = B'a' br'\\n' b'\\xff'\nprint(data)\n(data[::-1], bytes([97, 255]), bytes('café', encoding='UTF_8'), list(data), b'\\xff' in data, 255 in data, {data: 42}[bytes(list(data))])")]
    [InlineData("cannot mix bytes and text literals", "print('before')\nb'a' 'b'", "SyntaxError")]
    [InlineData("out of range bytes element", "bytes([256])", "ValueError")]
    [InlineData("Unicode and slices", "print('café 😀')\n('a😀bc'[::-2], [0, 1, 2, 3, 4][1:4:2])")]
    [InlineData("list slice assignment", "a = [0, 1, 2, 3]\nb = a\na[2:1] = iter([4, 5])\na[::-1] = a\na[1:3] += (6,)\nb += b\n(a is b, b)")]
    [InlineData("lazy big integer range", "r = range(2 ** 80, 2 ** 81, 3)\n(r[-1], list(r[1:8:2]), (2 ** 80 + 9) in r, range(2, 3) == range(2, 4, 9))")]
    [InlineData("wrong extended slice length", "a = [1]\na[::2 ** 80] = [2, 3]", "ValueError")]
    [InlineData("starred packing and unpacking", "def f():\n    a, *rest, (z, *end) = iter([1, 2, 3, (4, 5)])\n    return [a, *rest, z, *end]\n(f(), [a for a, *tail in [(1, 2), (3,)]])")]
    [InlineData("multiple starred assignment targets", "print('before')\na, *b, *c = [1]", "SyntaxError")]
    [InlineData("set operations", "a = {x % 3 for x in range(8)}\nb = a\na ^= {1, 4}\n(a is b, sorted(b), True in {1}, {2} < b)")]
    [InlineData("comprehensions", "{x: x * x for x in range(6) if x % 2}")]
    [InlineData("generator expression evaluation order and cursor sharing", """
        def mark(label, value):
            print(label, value)
            return value
        def source():
            yield mark('source', 2)
            yield mark('source', 3)
        def get_outer():
            print('outer')
            return outer
        outer = source()
        bias = 10
        g = (mark('element', x + y + bias) for x in get_outer()
             if mark('filter', x) for y in mark('inner', range(x)))
        print('constructed')
        print(next(g))
        bias = 20
        print(next(outer))
        (iter(g) is g, list(g), next(g, -1), list(x * x for x in range(3)))
        """)]
    [InlineData("generator expression rejects noniterable outer source immediately", "(missing for x in 42)\nprint('constructed')", "TypeError")]
    [InlineData("generator expression must be the sole unparenthesized call argument", "print('before')\nlist(x for x in [1],)", "SyntaxError")]
    [InlineData("lazy generators, delegation and return values", """
        def child(start):
            print(start)
            sent = yield start
            yield sent
            return 42
        def parent():
            yield from range(2)
            return (yield from child(10))
        g = parent()
        first = next(g)
        for value in g: break
        rest = [next(g), next(g)]
        try: next(g)
        except StopIteration as error: result = error.value
        (iter(g) is g, first, value, rest, result, next(g, 99))
        """)]
    [InlineData("escaping StopIteration in a generator", "def g():\n    yield 1\n    raise StopIteration(2)\ngen = g()\nprint(next(gen))\nnext(gen)", "RuntimeError")]
    [InlineData("structural pattern matching", """
        def classify(match):
            match match:
                case (['first', value, *_] | ['last', *_, value]) as whole if value < 0:
                    return 'negative', whole
                case [_, *_]: return 'sequence', value
                case {'a': True, **rest}: return 'mapping', rest
                case _: return 'other'
        [classify(case) for case in [('first', 5, 9), ['last', 9, -5], {'a': True, 'b': 42}, {'a': 1}, b'first']]
        """)]
    [InlineData("class patterns and inheritance", """
        class Point:
            __match_args__ = ('x', 'y')
            def __init__(self, x, y): self.x, self.y = x, y
        class Tagged(Point):
            def __init__(self, x, y, tag):
                self.x, self.y, self.tag = x, y, tag
        def classify(value):
            match value:
                case Point(x, y, tag='ok'): return x, y
                case Point(x=x, y=y): return x, y
                case _: return None
        [classify(Tagged(1, 2, 'ok')), classify(Point(3, 4)), classify((1, 2))]
        """)]
    [InlineData("static and class methods", """
        class Base:
            @staticmethod
            def add(x, *, y): return x + y
            @classmethod
            def owner(cls): return cls.__name__
        class Child(Base): pass
        item = Child()
        print(item.add(19, y=23), item.owner(), Base.owner())
        item.owner = 'shadow'
        (item.owner, Child.owner.__self__ is Child, Child.add is Base.add)
        """)]
    [InlineData("inherited property accessors and precedence", """
        events = []
        class Base:
            @property
            def value(self):
                'value doc'
                events.append('get')
                return self.raw
            @value.setter
            def value(self, value):
                events.append('set')
                self.raw = value
            @value.deleter
            def value(self):
                events.append('delete')
                del self.raw
        class Child(Base): value = None
        item = Child()
        item.value = 'shadow'
        del Child.value
        item.value = 40
        item.value += 2
        print(item.value)
        del item.value
        print(Child.value.__doc__)
        del Base.value
        (item.value, events)
        """)]
    [InlineData("read only property assignment", "class Item:\n    @property\n    def value(self): return 42\nitem = Item()\nprint(item.value)\nitem.value = -1", "AttributeError")]
    [InlineData("read only property deletion", "class Item:\n    @property\n    def value(self): return 42\nitem = Item()\nprint(item.value)\ndel item.value", "AttributeError")]
    [InlineData("invalid class pattern arguments", """
        class Item:
            __match_args__ = ('x',)
            def __init__(self): self.x = 1
        item = Item()
        match item:
            case Item(_, _): pass
        """, "TypeError")]
    [InlineData("inconsistent match alternatives", "print('before')\nmatch 1:\n    case [left, 1] | [1, right]: pass", "SyntaxError")]
    [InlineData("argument binding", "def f(a, /, b=2, *items, c, **extra):\n    return a + b + items[0] + c + extra['x']\nf(1, 2, 3, c=4, x=32)")]
    [InlineData("exception handling", "trace = []\ntry:\n    1 // 0\nexcept ArithmeticError as error:\n    trace.append(error.__class__.__name__)\nfinally:\n    trace.append('done')\ntrace")]
    [InlineData("implicit exception context", """
        try: raise ValueError('active')
        except ValueError as active:
            try: {}['key']
            except KeyError as error:
                result = (error.__context__ is active, error.__context__.args, error.__cause__, error.__suppress_context__)
        result
        """)]
    [InlineData("explicit exception causes and suppression", """
        cause = TypeError('explicit')
        try: raise KeyError('active')
        except KeyError as active:
            try: raise ValueError from cause
            except ValueError as error:
                result = (error.__context__ is active, error.__cause__ is cause, error.__suppress_context__)
            try: raise ValueError from None
            except ValueError as error:
                result += (error.__context__ is active, error.__cause__, error.__suppress_context__)
        try: raise ValueError from TypeError
        except ValueError as error:
            result += (error.__context__, error.__cause__.__class__ is TypeError, error.__cause__.args, error.__suppress_context__)
        result
        """)]
    [InlineData("invalid explicit exception cause", """
        try: raise ValueError from 42
        except TypeError as error:
            result = (str(error), error.__context__, error.__cause__, error.__suppress_context__)
        result
        """)]
    [InlineData("stdout without a value", "for x in range(2):\n    print(x, x + 1)")]
    [InlineData("runtime error after stdout", "print('before')\n{}['missing']", "KeyError")]
    [InlineData("syntax error before stdout", "print('before')\ndef broken(: pass", "SyntaxError")]
    [InlineData("math", "import math\n(math.sqrt(1764), math.sqrt(2.25), math.floor(2 ** 80 + 1), math.ceil(-1.5))")]
    [InlineData("float formatting", "import math\n(-0.0, 1e16, 1.2345678901234568e16, 1e-5, math.inf, -math.inf, math.nan)")]
    [InlineData("math domain error", "import math\nmath.sqrt(-1)", "ValueError")]
    [InlineData("exact integer math", "import math\nn = 2 ** 80\n(math.gcd(), math.lcm(), math.gcd(-6 * n, 9 * n), math.lcm(-6 * n, 9 * n), math.isqrt(n * n - 1), math.factorial(40), math.factorial(True))")]
    [InlineData("integer combinations and permutations", "import math\nn = 2 ** 80\n(math.comb(n, 3), math.perm(n, 3), math.comb(n, n-2), math.comb(2, 3), math.perm(2, 3), math.perm(10, None))")]
    [InlineData("negative selection count", "import math\nmath.perm(3, -1)", "ValueError")]
    [InlineData("integer square root domain", "import math\nmath.isqrt(-1)", "ValueError")]
    [InlineData("integer math validates every argument", "import math\nmath.lcm(0, 1.0)", "TypeError")]
    [InlineData("json", "import json\njson.loads(json.dumps({'answer': 2 ** 80, 'ok': True, 'items': [None, 'café']}))")]
    [InlineData("regex", "import re\nm = re.search('([a-z]+)-(?P<digits>\\\\d+)', 'id=abc-42!')\n(m.group(), m.group('digits'), m.start())")]
    [InlineData("datetime", "from datetime import date, timedelta\nd = date(2024, 2, 29)\n(d.weekday(), (d + timedelta(days=1)).isoformat())")]
    [InlineData("collections", "from collections import Counter\nc = Counter('cabba')\n(c.most_common(2), c['z'], c.total())")]
    [InlineData("itertools", "from itertools import count, islice\nlist(islice(count(2), 1, 8, 3))")]
    [InlineData("functools", "from functools import reduce\ndef add(a, b): return a + b\nreduce(add, [1, 2, 3], 10)")]
    [InlineData("typing", "from typing import cast, assert_type\nx = [42]\n(cast('list', x) is x, assert_type(x, 'list') is x)")]
    [InlineData("class namespace and bound methods", """
        def factory():
            seed = 10
            class Counter:
                seed = 3
                def __init__(self, *, start=seed): self.count = start
                def add(self, step):
                    self.count += step + seed
                    return self.count
            return Counter
        Counter = factory()
        counter = Counter()
        step = counter.add
        (step(step=2), Counter.add(counter, 1), Counter.seed, counter.__class__ is Counter)
        """)]
    [InlineData("diamond inheritance and context management", """
        class Root:
            label = 'root'
            def __enter__(self): return self.label
            def __exit__(self, kind, error, traceback): return kind is ValueError
        class Left(Root): pass
        class Right(Root): label = 'right'
        class Manager(Left, Right): pass
        manager = Manager()
        manager.__exit__ = None
        with manager as label:
            raise ValueError('suppressed')
        (label, [cls.__name__ for cls in Manager.__mro__])
        """)]
    [InlineData("inconsistent class MRO", "class A: pass\nclass B(A): pass\nclass C(A, B): pass", "TypeError")]
    [InlineData("function and class decorator evaluation and application", """
        def mark(event, value):
            print(event)
            return value
        def decorator(label):
            def wrap(value):
                return mark('apply ' + label, (label, value))
            return mark('eval ' + label, wrap)
        @decorator('outer function')
        @decorator('inner function')
        def function(value=mark('default', 42)): return value
        @decorator('outer class')
        @decorator('inner class')
        class Item(mark('base', object)):
            value = mark('body', 17)
        (function[0], function[1][0], function[1][1](), Item[0], Item[1][0], Item[1][1].value)
        """)]
    public async Task MatchesCPython(string scenario, string source, string? exception = null)
    {
        var expected = await RunCPython(source);
        Assert.True(expected.Exception == exception,
            $"{scenario}: CPython returned exception {expected.Exception ?? "<none>"}, expected {exception ?? "<none>"}.");
        var actual = RunShonePy(source);
        Assert.Equal(expected.Exception, actual.Exception);
        Assert.Equal(expected.Result, actual.Result);
        Assert.Equal(expected.Stdout, actual.Stdout);
    }

    [CPythonTheory]
    [InlineData("rows = [[], [2, 1], [3]]\n([*range(y) for x in rows if x for y in x if y != 1], sorted({*x for x in rows}), list(*x or [0] for x in rows))")]
    [InlineData("{**row if row else {3: 'fallback'} for row in [{1: 'first', 2: 'second'}, {}, {1: 'last'}]}")]
    [InlineData("""
        class Mapping:
            def keys(self):
                print('first key')
                yield 1
                print('second key')
                yield 2
            def __getitem__(self, key):
                print('value', key)
                return key * 10
        {**row for row in [Mapping(), {1: 100}]}
        """)]
    [InlineData("""
        class Cursor:
            def __init__(self): self.index = 0
            def __iter__(self):
                print('iter')
                return self
            def __next__(self):
                self.index += 1
                print('pull', self.index)
                if self.index > 2: raise StopIteration
                return self.index
        def mark(row):
            print('head')
            return row
        g = (*mark(row) for row in [Cursor(), [3]])
        print('created')
        print(next(g))
        print('resumed')
        list(g)
        """)]
    [InlineData("""
        class Row:
            def __iter__(self):
                print('iter')
                return iter([1])
            def __len__(self):
                print('len')
                raise TypeError
            def __length_hint__(self):
                print('hint')
                return 1
        ([*row for row in [Row()]], sorted({*row for row in [Row()]}), list(*row for row in [Row()]))
        """)]
    [InlineData("rows = [[1, 2]]\ng = (*row for row in rows)\nfirst = next(g)\nrows[0].append(3)\n(first, list(g))")]
    [InlineData("def bad(): raise StopIteration('head')\ng = (*bad() for row in [1])\nnext(g)", "RuntimeError")]
    [InlineData("[*x for x in [1]]", "TypeError")]
    [InlineData("{*x for x in [[[1]]]}", "TypeError")]
    [InlineData("{**row for row in [[(1, 2)]]}", "TypeError")]
    [InlineData("list(*x for x in [[1]],)", "SyntaxError")]
    [InlineData("list(**x for x in [{}])", "SyntaxError")]
    [InlineData("[*x or [] for x in [[1]]]", "SyntaxError")]
    public async Task MatchesUnpackingComprehensionsOnPython315OrNewer(string source, string? exception = null)
    {
        var expected = await RunCPython(source);
        var actual = RunShonePy(source);
        Assert.Equal(exception, actual.Exception);
        if (expected.Version is [3, < 15])
        {
            Assert.Equal("SyntaxError", expected.Exception);
            Assert.Empty(expected.Stdout);
            return;
        }

        Assert.Equal(exception, expected.Exception);
        Assert.Equal(expected.Result, actual.Result);
        Assert.Equal(expected.Stdout, actual.Stdout);
    }

    [CPythonTheory]
    [InlineData("try:\n    1 / 0\nexcept ValueError, ArithmeticError:\n    print(42)")]
    public async Task MatchesUnparenthesizedExceptionTypesOnRecentCPython(string source)
    {
        var expected = await RunCPython(source);
        if (expected.Version is { Length: >= 2 } version && version[0] == 3 && version[1] < 14)
        {
            Assert.Equal("SyntaxError", expected.Exception);
            return;
        }

        var actual = RunShonePy(source);
        Assert.Null(expected.Exception);
        Assert.Equal(expected.Exception, actual.Exception);
        Assert.Equal(expected.Result, actual.Result);
        Assert.Equal(expected.Stdout, actual.Stdout);
    }

    [CPythonTheory]
    [InlineData("""
        async def child(value):
            print('child', value)
            return value + 1
        async def parent():
            deferred = child(5)
            print('parent')
            first = await deferred
            try: await deferred
            except RuntimeError as error: print(str(error))
            return first * await child(6)
        parent()
        """)]
    [InlineData("""
        async def child(): raise StopIteration(42)
        async def parent():
            try: return await child()
            except RuntimeError as error:
                return (str(error), error.__cause__.value, error.__context__ is error.__cause__)
        parent()
        """)]
    [InlineData("""
        class Stream:
            def __init__(self): self.i = 0
            def __aiter__(self): return self
            async def __anext__(self):
                print('next', self.i)
                self.i += 1
                if self.i > 3: raise StopAsyncIteration
                return (self.i, self.i * 10)
        async def main():
            result = []
            stream = aiter(Stream())
            async for i, value in stream:
                if i == 2: continue
                result.append(value)
            else: result.append(await anext(stream, -1))
            async for i, value in Stream(): break
            else: result.append('break else')
            return result
        main()
        """)]
    [InlineData("""
        class Stream:
            def __aiter__(self): return self
            async def __anext__(self): return 1
        async def main():
            async for item in Stream(): raise StopAsyncIteration('loop body')
        main()
        """, "StopAsyncIteration")]
    [InlineData("""
        class Manager:
            def __init__(self, name, suppress=False):
                self.name = name
                self.suppress = suppress
            @property
            def __aenter__(self):
                print('bind enter', self.name)
                return self.enter
            async def enter(self):
                print('enter', self.name)
                return self.name
            @property
            def __aexit__(self):
                print('bind exit', self.name)
                return self.exit
            async def exit(self, kind, error, traceback):
                print('exit', self.name, kind is ValueError, error is original)
                return self.suppress
        original = ValueError('body failed')
        async def main():
            async with Manager('outer') as outer, Manager('inner', True) as inner:
                raise original
            return (outer, inner)
        main()
        """)]
    [InlineData("""
        class Stream:
            def __init__(self, values): self.values = iter(values)
            def __aiter__(self):
                print('iter')
                return self
            async def __anext__(self):
                print('next')
                try: return next(self.values)
                except StopIteration: raise StopAsyncIteration
        async def mark(label, value):
            print(label, value)
            return value
        async def main():
            x = Stream([(1, 2), (3, 4)])
            pairs = [(await mark('head', a), b, n) async for a, b in x
                     if await mark('filter', b > 2) for n in [10, 20]]
            values = {await mark('set', n) for a in [1, 2] async for n in Stream([a, a + 1])}
            mapping = {await mark('key', a): await mark('value', b)
                       async for a, b in Stream([(1, 2), (1, 3)])}
            outer = [a async for a in Stream([8]) if a == 8]
            shadow = [outer async for outer in Stream(outer)]
            return (pairs, sorted(values), mapping, outer, shadow, x.values is not None)
        main()
        """)]
    [InlineData("""
        class Stream:
            def __init__(self, values): self.values = iter(values)
            def __aiter__(self): return self
            async def __anext__(self):
                try: return next(self.values)
                except StopIteration: raise StopAsyncIteration
        async def work(value): return value
        async def main():
            nested = [[y async for y in Stream(await work([x]))] for x in await work([1, 2])]
            unpacked = [*await work(row) async for row in Stream(nested)]
            unique = {*row async for row in Stream(nested)}
            mapping = {**await work({x: x + 1}) async for x in Stream(unpacked)}
            generator = (y for y in [x async for x in Stream(unpacked)])
            return (nested, unpacked, sorted(unique), mapping, list(generator))
        main()
        """)]
    [InlineData("""
        class Stream:
            def __aiter__(self): return self
            async def __anext__(self): return 1
        async def fail(): raise StopAsyncIteration('filter')
        async def main(): return [x async for x in Stream() if await fail()]
        main()
        """, "StopAsyncIteration")]
    [InlineData("""
        class Stream:
            def __aiter__(self): raise StopAsyncIteration('acquire')
        async def main(): return [x async for x in Stream()]
        main()
        """, "StopAsyncIteration")]
    [InlineData("""
        class Stream:
            def __aiter__(self): return self
            def __anext__(self): return 1
        async def main(): return [x async for x in Stream()]
        main()
        """, "TypeError")]
    [InlineData("[x async for x in stream]", "SyntaxError")]
    [InlineData("async def child(): return await 42\nchild()", "TypeError")]
    [InlineData("await child()", "SyntaxError")]
    [InlineData("async def main():\n    def sync(): return await main()\nmain()", "SyntaxError")]
    public async Task MatchesManagedCoroutinesOnPinnedCPython(string source, string? exception = null)
    {
        var expected = await RunCPython(source, runCoroutine: true);
        var actual = RunShonePy(source, runCoroutine: true);
        Assert.Equal(exception, expected.Exception);
        Assert.Equal(expected.Exception, actual.Exception);
        Assert.Equal(expected.Result, actual.Result);
        Assert.Equal(expected.Stdout, actual.Stdout);
    }

    private static Observation RunShonePy(string source, bool runCoroutine = false)
    {
        using var output = new StringWriter { NewLine = "\n" };
        var engine = new PythonEngine(new PythonOptions { Output = output });
        try
        {
            var result = engine.Evaluate(source);
            if (runCoroutine) result = engine.RunCoroutine(result);
            return new Observation(output.ToString(), result.Repr(), null);
        }
        catch (PythonException error)
        {
            return new Observation(output.ToString(), null, error.ErrorName);
        }
    }

    private static async Task<Observation> RunCPython(string source, bool runCoroutine = false)
    {
        var executable = Environment.GetEnvironmentVariable(ExecutableVariable);
        Assert.True(!string.IsNullOrWhiteSpace(executable) && Path.IsPathFullyQualified(executable) && File.Exists(executable),
            $"Set {ExecutableVariable} to the full path of a CPython executable. Current value: {executable}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable!)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                ArgumentList = { "-I", "-X", "utf8", "-c", ReferenceDriver, runCoroutine ? "coroutine" : "script" },
            },
        };
        try
        {
            Assert.True(process.Start(), $"Could not start {ExecutableVariable}={executable}.");
        }
        catch (Win32Exception error)
        {
            throw new InvalidOperationException($"Could not start {ExecutableVariable}={executable}.", error);
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.StandardInput.WriteAsync(source.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw new TimeoutException($"CPython did not finish within 15 seconds ({executable}).");
        }

        Assert.True(process.ExitCode == 0, $"CPython exited with code {process.ExitCode} ({executable}).\n{await stderr}");
        var observation = JsonSerializer.Deserialize<Observation>(await stdout);
        Assert.NotNull(observation);
        return observation;
    }

    private sealed record Observation(string Stdout, string? Result, string? Exception, int[]? Version = null);

    private sealed class CPythonTheoryAttribute : TheoryAttribute
    {
        public CPythonTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ExecutableVariable)))
                Skip = $"Set {ExecutableVariable} to the full path of a CPython executable to run differential tests.";
        }
    }
}
