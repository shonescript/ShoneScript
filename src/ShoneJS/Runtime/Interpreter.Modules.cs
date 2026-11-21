// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Parsing;

namespace ShoneJs.Runtime;

public sealed partial class Interpreter
{
    private readonly Dictionary<string, ModuleRecord> _modules = new(StringComparer.Ordinal);
    private ModuleRecord? _activeModule;

    private sealed class ModuleRecord(Uri url, ParsedModule code, Dictionary<string, Uri> resolutions)
    {
        public readonly Uri Url = url;
        public readonly ParsedModule Code = code;
        public readonly Dictionary<string, Uri> Resolutions = resolutions;
        public readonly Dictionary<string, ModuleRecord> Dependencies = new(StringComparer.Ordinal);
        public readonly Dictionary<string, ModuleImport> Imports = code.Imports.ToDictionary(import => import.Local, StringComparer.Ordinal);
        public readonly Dictionary<string, ModuleExport> Exports = code.Exports.Where(export => export.Exported is not null)
            .ToDictionary(export => export.Exported!, StringComparer.Ordinal);
        public EnvironmentRecord? Scope;
        public ModuleNamespace? Namespace;
        public JsObject Meta = null!;
        public bool Linked;
        public JsValue? Evaluation;
    }

    private sealed record ExportBinding(ModuleRecord Record, string? Name)
    {
        public JsValue Read() => Name is null ? JsValue.From(Record.Namespace!) : Record.Scope!.Get(Name);
    }

    private sealed class ModuleNamespace(SortedDictionary<string, ExportBinding> bindings) : JsObject
    {
        public override bool IsExtensible => false;
        public override bool HasProperty(string key) => bindings.ContainsKey(key);
        public override bool Delete(string key) => !bindings.ContainsKey(key);
        public override IEnumerable<string> OwnPropertyKeys() => bindings.Keys;
        public override bool Set(string key, JsValue value, JsValue receiver) => false;
        public override PropertyDescriptor? GetOwnProperty(string key) => bindings.TryGetValue(key, out var binding)
            ? PropertyDescriptor.Data(binding.Read(), writable: true, enumerable: true) : null;
        public override bool DefineOwnProperty(string key, PropertyDescriptor descriptor) => GetOwnProperty(key) is { } current &&
            descriptor.Configurable != true && descriptor.Enumerable != false && !descriptor.IsAccessorDescriptor &&
            descriptor.Writable != false && (descriptor.Value is not { } value || AbstractOperations.SameValue(value, current.Value!.Value));
    }

    internal void RegisterModule(Uri url, ParsedModule module, IReadOnlyDictionary<string, Uri>? resolutions)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(module);
        if (!url.IsAbsoluteUri) throw new ArgumentException("A module URL must be absolute", nameof(url));
        if (!_modules.ContainsKey(ModuleKey(url)))
            _modules.Add(ModuleKey(url), new ModuleRecord(url, module, resolutions is null ? [] : new(resolutions, StringComparer.Ordinal)));
    }

    // Uri.Equals ignores the fragment; ECMAScript/HTML module-map URL identity does not.
    private static string ModuleKey(Uri url) => url.AbsoluteUri;

    internal JsValue EvaluateModule(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri) throw new ArgumentException("A module URL must be absolute", nameof(url));
        try
        {
            var root = FindRegistered(url);
            if (root.Evaluation is { } evaluated) return evaluated;
            LinkModule(root);
            return EvaluateLinkedModule(root, new HashSet<ModuleRecord>());
        }
        catch (JsException error)
        {
            AttachErrorPrototype(error);
            var failed = _promises.Create(out _, out var reject);
            reject(error.Value);
            return failed;
        }
    }

    private ModuleRecord FindRegistered(Uri url) => _modules.TryGetValue(ModuleKey(url), out var record)
        ? record : throw JsException.TypeError($"Module '{url.AbsoluteUri}' has not been registered");

    private ModuleRecord FindDependency(ModuleRecord record, string specifier)
    {
        if (!record.Resolutions.TryGetValue(specifier, out var resolved))
        {
            if (specifier.StartsWith("./", StringComparison.Ordinal) || specifier.StartsWith("../", StringComparison.Ordinal) || specifier.StartsWith('/'))
                Uri.TryCreate(record.Url, specifier, out resolved);
            else Uri.TryCreate(specifier, UriKind.Absolute, out resolved);
        }
        if (resolved is not { IsAbsoluteUri: true }) throw JsException.TypeError($"Cannot resolve module specifier '{specifier}'");
        return FindRegistered(resolved);
    }

    private void LinkModule(ModuleRecord root)
    {
        if (root.Linked) return;
        List<ModuleRecord> pending = [];
        var visited = new HashSet<ModuleRecord>();
        void Collect(ModuleRecord record)
        {
            ConsumeStep();
            if (record.Linked || !visited.Add(record)) return;
            pending.Add(record);
            foreach (var specifier in record.Code.RequestedModules)
            {
                var target = FindDependency(record, specifier);
                record.Dependencies[specifier] = target;
                Collect(target);
            }
        }

        var previous = CaptureContext();
        try
        {
            Collect(root);
            foreach (var record in pending)
            {
                ConsumeStep();
                record.Scope = new EnvironmentRecord(_global);
                record.Meta = new JsObject();
                record.Meta.DefineOwnProperty("url", PropertyDescriptor.Data(JsValue.From(record.Url.AbsoluteUri), writable: true, enumerable: true, configurable: true));
                EnterModule(record);
                PrepareScope(record.Code.Statements, _env, functionScope: true);
            }
            foreach (var record in pending)
            {
                ConsumeStep();
                foreach (var import in record.Code.Imports)
                {
                    var target = record.Dependencies[import.Specifier];
                    var binding = import.Namespace ? new ExportBinding(target, null) : RequireExport(target, import.Imported);
                    record.Scope!.CreateImport(import.Local, binding.Read);
                }
                foreach (var export in record.Code.Exports)
                    if (export.Exported is { } name && export.Specifier is not null) RequireExport(record, name);
                var namespaceBindings = new SortedDictionary<string, ExportBinding>(StringComparer.Ordinal);
                foreach (var name in ExportNames(record, new HashSet<ModuleRecord>()))
                {
                    var binding = ResolveExport(record, name, [], out _);
                    if (binding is not null) namespaceBindings.Add(name, binding);
                }
                record.Namespace = new ModuleNamespace(namespaceBindings);
            }
            foreach (var record in pending) record.Linked = true;
        }
        catch
        {
            foreach (var record in pending)
            {
                record.Scope = null;
                record.Namespace = null;
                record.Dependencies.Clear();
            }
            throw;
        }
        finally { RestoreContext(previous); }
    }

    private void EnterModule(ModuleRecord record)
    {
        _env = _varEnv = record.Scope!;
        _strict = true;
        _this = JsValue.Undefined;
        _activeModule = record;
    }

    private ExportBinding RequireExport(ModuleRecord record, string name) => ResolveExport(record, name, [], out var ambiguous)
        ?? throw new JsException(JsValue.From(new JsError("SyntaxError",
            ambiguous ? $"Module '{record.Url}' has an ambiguous export '{name}'" : $"Module '{record.Url}' does not export '{name}'")));

    private ExportBinding? ResolveExport(ModuleRecord record, string name, HashSet<(ModuleRecord, string)> visited, out bool ambiguous)
    {
        ConsumeStep();
        ambiguous = false;
        if (!visited.Add((record, name))) return null;
        if (record.Exports.TryGetValue(name, out var export))
        {
            if (export.Specifier is { } source)
            {
                var target = record.Dependencies[source];
                return export.Namespace ? new ExportBinding(target, null) : ResolveExport(target, export.Imported!, visited, out ambiguous);
            }
            if (!record.Imports.TryGetValue(export.Local!, out var import)) return new ExportBinding(record, export.Local);
            var dependency = record.Dependencies[import.Specifier];
            return import.Namespace ? new ExportBinding(dependency, null) : ResolveExport(dependency, import.Imported, visited, out ambiguous);
        }
        if (name == "default") return null;
        ExportBinding? result = null;
        foreach (var star in record.Code.Exports)
        {
            if (star.Exported is not null) continue;
            var candidate = ResolveExport(record.Dependencies[star.Specifier!], name, visited, out ambiguous);
            if (ambiguous) return null;
            if (candidate is null) continue;
            if (result is not null && result != candidate) { ambiguous = true; return null; }
            result = candidate;
        }
        return result;
    }

    private IEnumerable<string> ExportNames(ModuleRecord record, HashSet<ModuleRecord> visited)
    {
        ConsumeStep();
        if (!visited.Add(record)) return [];
        var names = record.Exports.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var star in record.Code.Exports)
            if (star.Exported is null)
                foreach (var name in ExportNames(record.Dependencies[star.Specifier!], visited))
                    if (name != "default") names.Add(name);
        return names;
    }

    private JsValue EvaluateLinkedModule(ModuleRecord record, HashSet<ModuleRecord> ancestors)
    {
        ConsumeStep();
        if (record.Evaluation is { } existing) return existing;
        var promise = _promises.Create(out var resolve, out var reject, assimilateThenables: false);
        record.Evaluation = promise;
        CompleteAsync(EvaluateBody(), resolve, reject);
        return promise;

        async JsOperation<JsValue> EvaluateBody()
        {
            var dependencies = new List<JsValue>();
            ancestors.Add(record);
            try
            {
                foreach (var target in record.Dependencies.Values.Distinct())
                {
                    if (ancestors.Contains(target)) continue;
                    var evaluation = EvaluateLinkedModule(target, ancestors);
                    if (_promises.TryGetSettled(evaluation, out var settled, out var rejected))
                    {
                        if (rejected) throw new JsException(settled);
                    }
                    else dependencies.Add(evaluation);
                }
            }
            finally { ancestors.Remove(record); }
            if (dependencies.Count != 0)
            {
                // Subscribe to all pending dependencies so an early rejection need not wait for an unrelated module.
                var remaining = dependencies.Count;
                var all = _promises.Create(out var ready, out var failed);
                foreach (var evaluation in dependencies)
                    _promises.Observe(evaluation, _ => { if (--remaining == 0) ready(JsValue.Undefined); }, failed);
                await Suspend(all, awaitEvenIfSettled: false);
            }
            var previous = CaptureContext();
            EnterModule(record);
            JsOperation<JsValue> body;
            try { body = ExecListAsync(record.Code.Statements); }
            finally { RestoreContext(previous); }
            await body;
            return JsValue.From(record.Namespace!);
        }
    }
}
