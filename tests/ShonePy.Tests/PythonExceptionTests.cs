// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Tests;

public sealed class PythonExceptionTests
{
    [Fact]
    public void AssertEvaluatesMessageOnlyOnFailure()
    {
        var result = new PythonEngine().Evaluate("""
            assert True, missing
            args = None
            try:
                assert False, 42
            except AssertionError as problem:
                args = problem.args
            args
            """);

        Assert.Equal("(42,)", result.Repr());
    }

    [Fact]
    public void MatchesUnparenthesizedExceptionTypes()
    {
        var result = new PythonEngine().Evaluate("""
            answer = 0
            try:
                1 / 0
            except ValueError, ArithmeticError:
                answer = 42
            answer
            """);
        Assert.Equal("42", result.Repr());
    }

    [Fact]
    public void MatchesInOrderByClassIdentityAndInheritance()
    {
        var result = new PythonEngine().Evaluate("""
            errors = (TypeError, ArithmeticError)
            ZeroDivisionError = TypeError
            trace = []
            try:
                1 // 0
            except ValueError:
                trace.append('wrong')
            except errors as problem:
                trace.append(problem.__class__.__name__)
            except:
                trace.append('too late')
            else:
                trace.append('success')
            finally:
                trace.append('done')
            trace
            """);

        Assert.Equal("['ZeroDivisionError', 'done']", result.Repr());
    }

    [Fact]
    public void RunsElseOnlyAfterSuccessAndHandlesErrorsFromElseOutsideTheTry()
    {
        var result = new PythonEngine().Evaluate("""
            trace = []
            try:
                try:
                    trace.append('body')
                except ValueError:
                    trace.append('wrong')
                else:
                    trace.append('else')
                    raise ValueError('else failed')
                finally:
                    trace.append('finally')
            except ValueError as problem:
                trace.append(str(problem))
            trace
            """);

        Assert.Equal("['body', 'else', 'finally', 'else failed']", result.Repr());
    }

    [Fact]
    public void ConstructsAndReraisesExceptionInstancesAndClearsAliases()
    {
        var result = new PythonEngine().Evaluate("""
            trace = []
            original = ValueError('first', 2)
            problem = 'old value'
            def reraise():
                raise
            try:
                try:
                    raise original
                except ValueError as problem:
                    trace.append(problem is original)
                    trace.append(problem.args)
                    reraise()
            except ValueError as problem:
                trace.append(problem is original)
            try:
                problem
            except NameError:
                trace.append('cleared')
            trace
            """);

        Assert.Equal("[True, ('first', 2), True, 'cleared']", result.Repr());
    }

    [Fact]
    public void ImplicitContextUsesTheInnermostActiveExceptionAndSurvivesPropagation()
    {
        var result = new PythonEngine().Evaluate("""
            def fail():
                return {}['missing']
            try:
                raise ValueError('outer')
            except ValueError as outer:
                try:
                    try:
                        raise TypeError('inner')
                    except TypeError as inner:
                        saved = inner
                        fail()
                except KeyError as error:
                    result = (error.__context__ is saved, saved.__context__ is outer,
                              error.__cause__, error.__suppress_context__)
            try:
                raise RuntimeError
            except RuntimeError as unrelated:
                result += (unrelated.__context__,)
            result
            """);

        Assert.Equal("(True, True, None, False, None)", result.Repr());
    }

    [Fact]
    public void ExplicitCauseRetainsImplicitContextAndSurvivesABareReraise()
    {
        var result = new PythonEngine().Evaluate("""
            cause = LookupError('cause')
            original = ValueError('failure')
            try:
                raise KeyError('active')
            except KeyError as active:
                try:
                    try:
                        raise original from cause
                    except ValueError:
                        raise
                except ValueError as error:
                    result = (error is original, error.__context__ is active,
                              error.__cause__ is cause, error.__suppress_context__)
            try:
                raise ValueError from TypeError
            except ValueError as error:
                result += (error.__cause__.__class__ is TypeError, error.__cause__.args,
                           error.__context__, error.__suppress_context__)
            result
            """);

        Assert.Equal("(True, True, True, True, True, (), None, True)", result.Repr());
    }

    [Fact]
    public void FromNoneClearsAnExplicitCauseAndKeepsTheContextInFinally()
    {
        var result = new PythonEngine().Evaluate("""
            fresh = ValueError()
            result = (fresh.__context__, fresh.__cause__, fresh.__suppress_context__)
            try: raise fresh from LookupError
            except ValueError: pass
            try:
                try:
                    raise RuntimeError('pending')
                finally:
                    raise fresh from None
            except ValueError as error:
                result += (error is fresh, error.__context__.args, error.__cause__, error.__suppress_context__)
            result
            """);

        Assert.Equal("(None, None, False, True, ('pending',), None, True)", result.Repr());
    }

    [Fact]
    public void InvalidCauseDoesNotChangeTheRaisedInstanceAndOperandErrorsTakePriority()
    {
        var result = new PythonEngine().Evaluate("""
            original = ValueError()
            trace = []
            def value():
                trace.append('value')
                return 42
            def cause():
                trace.append('cause')
                return missing
            try: raise value() from cause()
            except NameError: pass
            try: raise KeyError
            except KeyError as active:
                try: raise original from 42
                except TypeError as error:
                    result = (trace, str(error), error.__context__ is active,
                              original.__cause__, original.__suppress_context__)
            result
            """);

        Assert.Equal("(['value', 'cause'], 'exception causes must derive from BaseException', True, None, False)", result.Repr());
    }

    [Fact]
    public void ExplicitlyRaisingAnExistingErrorPreventsCyclesInItsContextChain()
    {
        var result = new PythonEngine().Evaluate("""
            a = ValueError()
            b = TypeError()
            try:
                raise a
            except ValueError:
                try:
                    raise b
                except TypeError:
                    try:
                        raise a
                    except ValueError:
                        try: raise a from a
                        except ValueError as error:
                            result = (error.__context__ is b, b.__context__,
                                      error.__cause__ is a, error.__suppress_context__)
            result
            """);

        Assert.Equal("(True, None, True, True)", result.Repr());
    }

    [Fact]
    public void DictionaryKeyErrorExposesTheOriginalKey()
    {
        var result = new PythonEngine().Evaluate("""
            try: {}['missing']
            except KeyError as problem:
                value = (problem.args, str(problem))
            value
            """);

        Assert.Equal("(('missing',), '\\'missing\\'')", result.Repr());
    }

    [Fact]
    public void RejectsInvalidRaiseAndHandlerTypes()
    {
        var engine = new PythonEngine();
        Assert.Equal("TypeError", Assert.Throws<PythonException>(() => engine.Evaluate("raise 42")).ErrorName);
        Assert.Equal("RuntimeError", Assert.Throws<PythonException>(() => engine.Evaluate("raise")).ErrorName);
        var error = Assert.Throws<PythonException>(() => engine.Evaluate("""
            try: raise ValueError
            except (ValueError, 42): pass
            """));
        Assert.Equal("TypeError", error.ErrorName);
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public void FinallyUnwindsReturnBreakAndContinue()
    {
        var result = new PythonEngine().Evaluate("""
            trace = []
            def run():
                for i in [1, 2]:
                    try:
                        if i == 1: continue
                        break
                    except:
                        trace.append('wrong')
                    else:
                        trace.append('also wrong')
                    finally:
                        trace.append(i)
                else:
                    trace.append('loop else')
                try:
                    return 3
                finally:
                    trace.append(4)
            trace.append(run())
            trace
            """);

        Assert.Equal("[1, 2, 4, 3]", result.Repr());
    }

    [Fact]
    public void FinallyCanOverridePendingReturnOrException()
    {
        var result = new PythonEngine().Evaluate("""
            def first():
                try: return 1
                finally: return 2
            def second():
                try: raise KeyError('ignored')
                finally: return 3
            (first(), second())
            """);

        Assert.Equal("(2, 3)", result.Repr());
    }

    [Fact]
    public void FinallyCanReraiseAPropagatingExceptionAtItsOriginalLocation()
    {
        var engine = new PythonEngine();
        var error = Assert.Throws<PythonException>(() => engine.Evaluate("""
            try:
                raise ValueError('detail')
            finally:
                raise
            """, "attempt.py"));

        Assert.Equal("ValueError", error.ErrorName);
        Assert.Equal("detail", error.Message);
        Assert.Equal((2, 5, "attempt.py"), (error.Line, error.Column, error.FileName));
    }

    [Fact]
    public void DiscoversAliasAndAssignmentsAcrossTrySuitesWhenBindingLocals()
    {
        var result = new PythonEngine().Evaluate("""
            alias = 'global'
            value = 'global'
            def run():
                try:
                    alias
                except UnboundLocalError as alias:
                    handled = True
                finally:
                    value = 'local'
                try:
                    alias
                except UnboundLocalError:
                    return (handled, value)
            run()
            """);

        Assert.Equal("(True, 'local')", result.Repr());
    }

    [Theory]
    [InlineData("try: pass")]
    [InlineData("try: pass\nelse: pass")]
    [InlineData("try: pass\nexcept: pass\nexcept ValueError: pass")]
    [InlineData("try: pass\nfinally: pass\nexcept: pass")]
    [InlineData("try: pass\nexcept* Exception: pass")]
    [InlineData("raise from None")]
    [InlineData("raise ValueError from")]
    [InlineData("raise ValueError from None from None")]
    public void RejectsInvalidOrUnsupportedSyntax(string source)
    {
        var error = Assert.Throws<PythonException>(() => new PythonEngine().ParseScript(source));
        Assert.Equal("SyntaxError", error.ErrorName);
    }

    [Fact]
    public void StepBudgetCannotBeCaughtOrOverriddenByFinally()
    {
        using var output = new StringWriter();
        var engine = new PythonEngine(new PythonOptions { MaximumSteps = 60, Output = output });
        var error = Assert.Throws<PythonException>(() => engine.Evaluate("""
            def run():
                try:
                    while True: pass
                except RuntimeError:
                    print('caught')
                finally:
                    return 42
            run()
            """, "budget.py"));

        Assert.Equal("RuntimeError", error.ErrorName);
        Assert.Equal("maximum execution steps exceeded", error.Message);
        Assert.Equal("budget.py", error.FileName);
        Assert.Empty(output.ToString());
        Assert.Equal("42", engine.Evaluate("42").ToString());
    }

    [Fact]
    public void CallDepthBudgetCannotBeCaughtOrOverriddenByFinally()
    {
        var engine = new PythonEngine(new PythonOptions { MaximumCallDepth = 3 });
        var error = Assert.Throws<PythonException>(() => engine.Evaluate("""
            def run():
                try: run()
                except: pass
                finally: return 42
            run()
            """));

        Assert.Equal("RecursionError", error.ErrorName);
        Assert.Equal("maximum recursion depth exceeded", error.Message);
    }
}
