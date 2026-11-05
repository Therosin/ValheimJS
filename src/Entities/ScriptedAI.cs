using UnityEngine;

namespace ValheimJS.Entities;

/// <summary>
/// Executes <see cref="NpcWorld"/> intents through <see cref="BaseAI"/> movement primitives.
/// Driven by `MonoUpdaters.FixedUpdate` at ~20 Hz; no custom update loop. Stuck detection uses
/// distance progress over real time (BaseAI.MoveTo returns true on arrival *and* on giving up).
/// </summary>
internal sealed class ScriptedAI : BaseAI
{
    private int _generation = -1;
    private float _bestDistance = float.MaxValue;
    private float _progressAt;

    protected override void Awake() => base.Awake();

    public override bool UpdateAI(float dt)
    {
        if (!base.UpdateAI(dt))
        {
            return false;
        }

        var uid = m_nview.GetZDO()?.m_uid ?? ZDOID.None;
        if (uid.IsNone() || NpcWorld.Instance == null ||
            !NpcWorld.Instance.GetIntent(uid, out var intent, out var generation))
        {
            return true;
        }

        if (generation != _generation)
        {
            _generation = generation;
            _bestDistance = float.MaxValue;
            _progressAt = Time.realtimeSinceStartup;
        }

        if (intent.Kind == NpcIntentKind.Idle)
        {
            StopMoving();
            IdleMovement(dt);
            NpcWorld.Instance.Report(uid, true, null);
            return true;
        }

        var target = new Vector3((float)intent.X, (float)intent.Y, (float)intent.Z);
        var distance = Vector3.Distance(transform.position, target);
        if (distance <= 0.75f)
        {
            StopMoving();
            NpcWorld.Instance.Report(uid, true, null);
            return true;
        }

        MoveTo(dt, target, 0.5f, intent.Run);

        var now = Time.realtimeSinceStartup;
        if (distance <= _bestDistance - 0.5f)
        {
            _bestDistance = distance;
            _progressAt = now;
        }
        else if (now - _progressAt >= 5f)
        {
            StopMoving();
            NpcWorld.Instance.Report(uid, false, "nav-stuck");
        }

        return true;
    }
}
