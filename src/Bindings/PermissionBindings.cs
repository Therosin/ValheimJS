using ValheimJS.Scripting.Binding;

namespace ValheimJS.Bindings;

[JsBinder("PermissionService", Phase = JsBindPhase.Gameplay, Order = 151,
    Description = "Live account role and permission queries. Policy editing is host-owned.")]
internal sealed class PermissionBindings : JsBindings
{
    public PermissionBindings(JsBindingContext context) : base(context) { }
    [JsBinding] public bool Available() => Context.Permissions.Available;
    [JsBinding] public bool HasRole(string id, string role) => Context.Permissions.HasRole(id, role);
    [JsBinding] public bool HasPermission(string id, string permission) => Context.Permissions.HasPermission(id, permission);
}
