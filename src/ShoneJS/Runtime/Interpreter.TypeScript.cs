// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Parsing;

namespace ShoneJs.Runtime;

public sealed partial class Interpreter
{
    private JsValue ExecuteEnum(EnumStmt declaration)
    {
        var outer = _env;
        var members = new EnvironmentRecord(outer);
        var value = new JsObject { Prototype = _objectPrototype };
        outer.Initialize(declaration.Name, JsValue.From(value));
        _env = members;
        try
        {
            var next = 0d;
            foreach (var member in declaration.Members)
            {
                var item = member.Initializer is null ? JsValue.From(next) : Eval(member.Initializer);
                DefineEnumProperty(value, member.Name, item);
                if (item.Type == JsType.Number)
                {
                    DefineEnumProperty(value, item.ToJsString(), JsValue.From(member.Name));
                    next = item.ToNumber() + 1;
                }
                if (members.HasOwn(member.Name)) members.Set(member.Name, item);
                else members.Declare(member.Name, item);
            }
        }
        finally { _env = outer; }
        return JsValue.Undefined;
    }

    private static void DefineEnumProperty(JsObject target, string name, JsValue value) =>
        target.DefineOwnProperty(name, PropertyDescriptor.Data(value, writable: true, enumerable: true, configurable: true));
}
