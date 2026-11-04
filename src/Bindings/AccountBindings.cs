using ValheimJS.Networking;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Bindings;

[JsBinder("AccountService", Phase = JsBindPhase.Gameplay, Order = 150,
    Description = "Authoritative account queries and publisher-owned shared metadata. No account management API.")]
internal sealed class AccountBindings : JsBindings
{
    public AccountBindings(JsBindingContext context) : base(context) { }
    [JsBinding] public bool Available() => Context.Accounts.Available;
    [JsBinding(ReturnKind = JsReturnKind.Json, Returns = "Account | null")]
    public string Get(string id) => NetworkJson.Write(Context.Accounts.Get(id));
    [JsBinding(ReturnKind = JsReturnKind.Json, Returns = "Account | null")]
    public string FindByPlatform(string platformId) => NetworkJson.Write(Context.Accounts.FindByPlatform(platformId));
    [JsBinding(ReturnKind = JsReturnKind.Json, Returns = "Account | null")]
    public string FromPeer(string peerId) => NetworkJson.Write(Context.Accounts.FromPeer(peerId));
    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Json, Returns = "Record<string, unknown> | null")]
    public string? GetData(string plugin, string id) => Context.Accounts.ForPlugin(plugin).GetData(id);
    [JsBinding(Scoped = true, ReturnKind = JsReturnKind.Json, Returns = "Record<string, unknown> | null")]
    public string? ReadPublishedData(string plugin, string id, string publisher) => Context.Accounts.ForPlugin(plugin).ReadPublishedData(id, publisher);
    [JsBinding(Scoped = true)] public void SetData(string plugin, string id, string json) => Context.Accounts.ForPlugin(plugin).SetData(id, json);
    [JsBinding(Scoped = true)] public void DeleteData(string plugin, string id) => Context.Accounts.ForPlugin(plugin).DeleteData(id);
}
