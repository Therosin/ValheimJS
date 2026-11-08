using UnityEngine;

namespace ValheimJS.World;

/// <summary>
/// Inert AI marker for the client HUD. EnemyHud assumes every hostile character has a BaseAI;
/// speech/combat AI is intentionally absent because the server owns simulation.
/// </summary>
internal sealed class NpcVisualAI : BaseAI
{
    public override bool UpdateAI(float dt) => false;
}

/// <summary>
/// Client-side presentation for a server-simulated NPC. The server writes position via the ZDO;
/// this component only feeds the animator from the replicated speed and repairs large gaps when
/// the engine's non-owner interpolation is unavailable.
/// </summary>
internal sealed class NpcPresenter : MonoBehaviour
{
    private static readonly int ForwardSpeed = ZSyncAnimation.GetHash("forward_speed");

    private ZNetView? _view;
    private ZSyncAnimation? _animation;

    private void Awake()
    {
        _view = GetComponent<ZNetView>();
        _animation = GetComponent<ZSyncAnimation>();
    }

    private void LateUpdate()
    {
        if (_view == null || _animation == null)
        {
            return;
        }

        var zdo = _view.GetZDO();
        if (zdo == null || _view.IsOwner())
        {
            return;
        }

        _animation.SetFloat(ForwardSpeed, zdo.GetFloat(WorldKeys.Speed, 0f));

        var target = zdo.GetPosition();
        if (Vector3.Distance(transform.position, target) > 1.5f)
        {
            transform.position = target;
            transform.rotation = zdo.GetRotation();
        }
    }
}
