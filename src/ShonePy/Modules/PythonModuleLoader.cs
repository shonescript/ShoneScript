// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShonePy.Parsing;

namespace ShonePy.Modules;

internal sealed class PythonModuleLoader
{
    private readonly IList<string> _roots;
    private sealed record LoadedModule(PyDict Namespace, string? PackageDirectory);

    private readonly Dictionary<string, LoadedModule> _cache = new(StringComparer.Ordinal);
    private readonly Func<ModuleSyntax, PyDict, PyDict> _execute;

    public PythonModuleLoader(IList<string> roots, Func<ModuleSyntax, PyDict, PyDict> execute)
    {
        _roots = roots;
        _execute = execute;
    }

    public PyDict Import(string name)
    {
        if (name.Split('.').Any(part => part.Length == 0 ||
            !(char.IsLetter(part[0]) || part[0] == '_') ||
            part.Any(ch => !char.IsLetterOrDigit(ch) && ch != '_')))
            throw new PythonException("ModuleNotFoundError", $"No module named '{name}'");

        if (_cache.TryGetValue(name, out var cached)) return cached.Namespace;
        int separator = name.LastIndexOf('.');
        string leaf = name[(separator + 1)..];
        PyDict? parent = null;
        IEnumerable<string> directories = _roots;
        if (separator >= 0)
        {
            string parentName = name[..separator];
            parent = Import(parentName);
            // The parent's initializer may already have imported this child.
            if (_cache.TryGetValue(name, out cached)) return cached.Namespace;
            if (_cache[parentName].PackageDirectory is not { } parentDirectory)
                throw new PythonException("ModuleNotFoundError", $"No module named '{name}'; '{parentName}' is not a package");
            directories = [parentDirectory];
        }

        foreach (string root in directories)
        {
            string directory = Path.GetFullPath(root);
            string packageDirectory = Path.Combine(directory, leaf);
            string initializer = Path.Combine(packageDirectory, "__init__.py");
            bool package = File.Exists(initializer);
            string sourcePath = package ? initializer : Path.Combine(directory, leaf + ".py");
            if (!File.Exists(sourcePath)) continue;
            var module = new PyDict();
            _cache[name] = new(module, package ? packageDirectory : null);
            module[PyValue.From("__name__")] = PyValue.From(name);
            module[PyValue.From("__file__")] = PyValue.From(sourcePath);
            module[PyValue.From("__package__")] = PyValue.From(package ? name : name.Contains('.') ? name[..name.LastIndexOf('.')] : string.Empty);
            try
            {
                string source = File.ReadAllText(sourcePath);
                _execute(Parser.Parse(source, sourcePath), module);
                if (parent is not null) parent[PyValue.From(leaf)] = PyValue.From(module);
                return module;
            }
            catch
            {
                _cache.Remove(name);
                throw;
            }
        }
        throw new PythonException("ModuleNotFoundError", $"No module named '{name}'");
    }
}
