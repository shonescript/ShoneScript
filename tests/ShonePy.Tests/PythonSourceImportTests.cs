// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Tests;

public sealed class PythonSourceImportTests
{
    [Fact]
    public void ImportsModuleFromExplicitSearchRootAndCachesIt()
    {
        using var root = new TempRoot();
        root.Write("sample.py", "value = 40\n");
        var options = new PythonOptions();
        options.SearchPaths.Add(root.Path);
        var engine = new PythonEngine(options);
        Assert.Equal("True", engine.Evaluate("import sample\nfirst = sample\nimport sample\nfirst is sample").ToString());
    }

    [Fact]
    public void ImportsPackagesAndRelativeModules()
    {
        using var root = new TempRoot();
        root.Write("pkg/__init__.py", "base = 20\n");
        root.Write("pkg/answer.py", "from .helper import value\nresult = value + 22\n");
        root.Write("pkg/helper.py", "value = 20\n");
        var options = new PythonOptions();
        options.SearchPaths.Add(root.Path);
        Assert.Equal("42", new PythonEngine(options).Evaluate("from pkg import answer\nanswer.result").ToString());
    }

    [Fact]
    public void DirectSubmoduleImportInitializesTheParentFirst()
    {
        using var root = new TempRoot();
        root.Write("pkg/__init__.py", "base = 40\n");
        root.Write("pkg/child.py", "from . import base\nanswer = base + 2\n");
        var options = new PythonOptions();
        options.SearchPaths.Add(root.Path);
        Assert.Equal("42", new PythonEngine(options).Evaluate("import pkg.child\npkg.child.answer").ToString());
    }

    [Fact]
    public void CircularImportsSeeNamesInitializedBeforeImport()
    {
        using var root = new TempRoot();
        root.Write("left.py", "value = 40\nimport right\nresult = right.answer\n");
        root.Write("right.py", "import left\nanswer = left.value + 2\n");
        var options = new PythonOptions();
        options.SearchPaths.Add(root.Path);
        Assert.Equal("42", new PythonEngine(options).Evaluate("import left\nleft.result").ToString());
    }

    [Fact]
    public void FailedInitializationIsRemovedFromCache()
    {
        using var root = new TempRoot();
        root.Write("broken.py", "raise_error = missing_name\n");
        var options = new PythonOptions();
        options.SearchPaths.Add(root.Path);
        var engine = new PythonEngine(options);
        Assert.Throws<PythonException>(() => engine.Evaluate("import broken"));
        root.Write("broken.py", "value = 42\n");
        Assert.Equal("42", engine.Evaluate("import broken\nbroken.value").ToString());
    }

    [Fact]
    public void FromImportPreservesExceptionsFromSubmoduleInitialization()
    {
        using var root = new TempRoot();
        root.Write("pkg/__init__.py", "pass\n");
        root.Write("pkg/broken.py", "raise ValueError('submodule failed')\n");
        var options = new PythonOptions();
        options.SearchPaths.Add(root.Path);
        var engine = new PythonEngine(options);
        var error = Assert.Throws<PythonException>(() => engine.Evaluate("from pkg import broken"));

        Assert.Equal("ValueError", error.ErrorName);
        Assert.Equal("submodule failed", error.Message);
        Assert.Equal(System.IO.Path.Combine(root.Path, "pkg", "broken.py"), error.FileName);
    }

    [Fact]
    public void FromImportCannotConvertStepBudgetToAnImportError()
    {
        using var root = new TempRoot();
        root.Write("pkg/__init__.py", "pass\n");
        root.Write("pkg/broken.py", "while True: pass\n");
        var options = new PythonOptions { MaximumSteps = 50 };
        options.SearchPaths.Add(root.Path);
        var engine = new PythonEngine(options);
        var error = Assert.Throws<PythonException>(() => engine.Evaluate("try: from pkg import broken\nexcept: pass"));

        Assert.Equal("RuntimeError", error.ErrorName);
        Assert.Equal("maximum execution steps exceeded", error.Message);
        Assert.Equal(System.IO.Path.Combine(root.Path, "pkg", "broken.py"), error.FileName);
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shonepy-" + Guid.NewGuid().ToString("N"));
        public void Write(string relativePath, string source)
        {
            string file = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file, source);
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
