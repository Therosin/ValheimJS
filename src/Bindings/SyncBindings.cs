using System;
using ValheimJS.Core;
using ValheimJS.Networking;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Bindings;

/// <summary>JSON/JS adaptation only; native sync owns codec, binary transport and subscriptions.</summary>
[JsBinder("sync", Phase = JsBindPhase.Gameplay, Order = 155,
    Description = "Explicit compact binary streams for transient non-ZDO state.")]
internal sealed class SyncBindings : JsBindings
{
    private readonly SyncScope _scope;
    public SyncBindings(JsBindingContext context) : base(context) => _scope = context.Networking.Sync.OpenScope();
    [JsBinding(Scoped = true, Description = "Receives decoded binary frames on a named stream; returns a subscription id.")]
    public int On(string plugin, string stream, JsCallback handler) => _scope.On(plugin, stream, (message, entities) => handler.Invoke(handler.Parse(NetworkJson.Write(entities)), handler.Parse(NetworkJson.Write(message))));
    [JsBinding(Scoped = true, Description = "Removes a binary stream subscription owned by this plugin.")]
    public void Off(string plugin, int id) => _scope.Off(plugin, id);
    [JsBinding(Description = "Sends one binary frame; entitiesJson is an array of SyncEntity. No automatic cadence or late-join replay.")]
    public void Send(long target, string stream, string entitiesJson) => _scope.Send(target, stream, NetworkJson.Read<SyncEntity[]>(entitiesJson));
    [JsBinding(Description = "Encodes entities as a base64 frame for storage or inspection; send() uses raw bytes on the wire.")]
    public string Encode(string entitiesJson) => Convert.ToBase64String(NetworkSync.Encode(NetworkJson.Read<SyncEntity[]>(entitiesJson)));
    [JsBinding(ReturnKind = JsReturnKind.Json, Returns = "SyncEntity[]", Description = "Decodes a base64 frame for inspection.")]
    public string Decode(string frame) => NetworkJson.Write(NetworkSync.Decode(Convert.FromBase64String(frame)));
    [JsBinding(ReturnKind = JsReturnKind.Json, Returns = "SyncEntity | null", Description = "Changed fields only, or null for an unchanged entity.")]
    public string Diff(string previousJson, string nextJson, float step) => NetworkJson.Write(NetworkSync.Diff(NetworkJson.Read<SyncEntity>(previousJson), NetworkJson.Read<SyncEntity>(nextJson), step));
    [JsBinding(Description = "Rounds a finite value to a positive precision step.")]
    public float Quantise(float value, float step) => NetworkSync.Quantise(value, step);
    public override void Release(string plugin) => _scope.Release(plugin);
    public override void Dispose() => _scope.Dispose();
}
