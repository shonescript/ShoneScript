// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Runtime;

internal sealed class PySuper
{
    private readonly PyClass _startClass;
    private readonly PyValue _receiver;
    private readonly PyClass _receiverClass;
    private readonly int _lookupStart;

    internal PySuper(PyClass startClass, PyValue receiver, Action chargeStep)
    {
        _startClass = startClass;
        _receiver = receiver;
        _receiverClass = receiver.Data switch
        {
            PyInstance instance => instance.Class,
            PyClass cls => cls,
            _ => throw InvalidReceiver()
        };
        for (int i = 0; i < _receiverClass.Mro.Count; i++)
        {
            chargeStep();
            if (_receiverClass.Mro[i] != startClass) continue;
            _lookupStart = i + 1;
            return;
        }
        throw InvalidReceiver();
    }

    internal PyValue GetAttribute(string name, Action chargeStep)
    {
        if (_receiverClass.TryLookup(name, out var member, _lookupStart, chargeStep))
            return _receiverClass.BindMember(member, _receiver.Data as PyInstance);
        return name switch
        {
            "__thisclass__" => PyValue.From(_startClass),
            "__self__" => _receiver,
            "__self_class__" => PyValue.From(_receiverClass),
            _ => throw new PythonException("AttributeError", $"'super' object has no attribute '{name}'")
        };
    }

    private static PythonException InvalidReceiver() =>
        new("TypeError", "super(type, obj): obj must be an instance or subtype of type");

    public override string ToString() => $"<super: <class '{_startClass.Name}'>, <{_receiverClass.Name} object>>";
}
