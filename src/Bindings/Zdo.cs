using System;
using ValheimJS.Scripting.Binding;
using System.Reflection;

namespace ValheimJS.Bindings;

/// <summary>
/// World object (ZDO) counters. Scripts see <c>host.api.zdo.*</c>.
/// The live object table is private to ZDOMan, so it is read through a cached field —
/// reflection happens once, not per call.
/// </summary>
[JsBinding("Zdo", JsBindPhase.Gameplay, order: 120)]
internal static class ZdoBindings
{
    private static readonly FieldInfo? ObjectsField = typeof(ZDOMan).GetField(
        "m_objectsByID",
        BindingFlags.Instance | BindingFlags.NonPublic);

    public static void Bind(JsBindingContext context)
    {
        context.Add("zdo.count", new Func<int>(Count));
        context.Add("zdo.available", new Func<bool>(() => ObjectsField != null));
    }

    private static int Count()
    {
        var manager = ZDOMan.instance;
        if (manager == null || ObjectsField == null)
        {
            return 0;
        }

        return ObjectsField.GetValue(manager) is System.Collections.ICollection objects ? objects.Count : 0;
    }
}
