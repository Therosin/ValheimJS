using System.Collections.Generic;
using Jint.Native;
using ValheimJS.Scripting.Binding;
using ValheimJS.Scripting.Types;

namespace ValheimJS.Bindings;

/// <summary>
/// Player surface. The native <see cref="Player"/> type is registered once; methods return
/// per-plugin JS instances from the type registry. `online` exists only on the dedicated server,
/// `local` only on a client; `get` resolves a single known player anywhere.
/// </summary>
[JsBinder("players", Phase = JsBindPhase.Gameplay, Order = 140,
    Description = "Players: local identity, lookups and the authoritative online roster.")]
internal sealed class PlayerBindings : JsBindings
{
    private JsNativeType? _playerType;

    public PlayerBindings(JsBindingContext context) : base(context)
    {
    }

    public override void Install(JsTypeRegistry types) =>
        _playerType = types.Register<ValheimJS.Entities.Player>();

    [JsBinding(ReturnKind = JsReturnKind.Native, Returns = "ValheimPlayer | null",
        Availability = JsAvailability.Client,
        Description = "This process's local player, or null on a dedicated server.")]
    public JsValue Local() => ToScript(ValheimJS.Entities.PlayerDirectory.Local());

    [JsBinding(ReturnKind = JsReturnKind.Native, Returns = "ValheimPlayer | null",
        Description = "One connected player by routed uid or platform id, or null.")]
    public JsValue Get(string id) => ToScript(ValheimJS.Entities.PlayerDirectory.Get(id));

    [JsBinding(ReturnKind = JsReturnKind.Native, Returns = "ValheimPlayer[]",
        Availability = JsAvailability.Server,
        Description = "Connected players with a spawned character; live authority only.")]
    public JsValue Online()
    {
        var values = new List<JsValue>();
        foreach (var player in ValheimJS.Entities.PlayerDirectory.Online())
        {
            values.Add(ToScript(player));
        }

        return JsPayload.Array(Engine, values);
    }

    [JsBinding(Description = "True once the player's client reports its world is actually playable.")]
    public bool Ready(string id) => ValheimJS.Entities.PlayerDirectory.Get(id)?.Ready == true;

    private JsValue ToScript(ValheimJS.Entities.Player? player)
    {
        if (player == null || _playerType == null)
        {
            return JsValue.Null;
        }

        return Context.Types.Instance(_playerType, player);
    }
}
