using System;
using UnityEngine;
using ValheimJS.Core;
using ValheimJS.Entities;

namespace ValheimJS.World;

/// <summary>
/// Native body controller. Scripts set a destination or facing target; Valheim's BaseAI and
/// Pathfinding own the actual route, movement, physics and animation. This class supplies only
/// the intent layer, optional auto-engagement and the failure facts the host exposes back.
/// </summary>
/// <remarks>
/// Every explicit intent (go, look, face, clear) cancels auto-combat, so scripts always win.
/// While auto-combat is engaged the intent reports <see cref="NpcActionStatus.Paused"/> with
/// reason <c>combat</c> instead of being overwritten.
/// </remarks>
internal sealed class VjsNpcAI : BaseAI
{
    private const float ProgressSeconds = 5f;
    private const float ArriveSlop = 0.35f;
    private const float ReachCheckSeconds = 0.75f;
    private const float MoveTimeoutSeconds = 60f;
    private const float DirectFallbackDistance = 3f;

    private Vector3 _target;
    private float _stopDistance;
    private bool _run;
    private bool _moving;
    private bool _lookOnly;
    private string? _faceId;
    private NpcActionStatus _status = NpcActionStatus.Idle;
    private string? _reason;
    private float _progressAt;
    private float _reachCheckAt;
    private float _deadline;
    private bool _hasPath = true;
    private bool _jumped;
    private Character? _threat;
    private float _threatAt;
    private bool _engaged;
    private bool _nameApplied;

    /// <summary>Opt-in: the body fights only when its kind asks for it.</summary>
    internal bool AutoEngage { get; set; }

    internal NpcActionStatus Status =>
        _engaged && IntentActive ? NpcActionStatus.Paused : _status;

    internal string? Reason => _engaged && IntentActive ? "combat" : _reason;

    internal bool IsMoving => _moving && !_engaged;

    internal bool Engaged => _engaged;

    internal string? ThreatId =>
        _threat != null ? _threat.GetZDOID().ToString() : null;

    private bool IntentActive => _moving || _lookOnly;

    protected override void Awake()
    {
        base.Awake();
        ApplyName();
    }

    private void ApplyName()
    {
        if (_nameApplied || m_character == null)
        {
            return;
        }

        var zdo = GetComponent<ZNetView>()?.GetZDO();
        if (zdo == null)
        {
            return;
        }

        _nameApplied = true;
        var name = zdo.GetString(WorldKeys.Name, "");
        if (!string.IsNullOrWhiteSpace(name))
        {
            m_character.m_name = name;
        }
    }

    protected override void OnDamaged(float damage, Character attacker)
    {
        base.OnDamaged(damage, attacker);
        if (!AutoEngage || m_character == null || attacker == null || attacker.IsDead())
        {
            return;
        }

        // An untamed body never turns on the player who hit it.
        if (attacker.IsPlayer() && !m_character.IsTamed())
        {
            return;
        }

        _threat = attacker;
        _threatAt = Time.time;
        _engaged = true;
        SetAlerted(true);
    }

    /// <summary>Starts a navigation intent and cancels auto-combat.</summary>
    internal bool GoTo(Vector3 target, float stopDistance, bool run)
    {
        Disengage();
        _target = target;
        _stopDistance = Mathf.Max(0.05f, stopDistance);
        _run = run;
        _moving = true;
        _lookOnly = false;
        _faceId = null;
        _progressAt = Time.time;
        _reachCheckAt = 0f;
        _deadline = Time.time + MoveTimeoutSeconds;
        _hasPath = true;
        _jumped = false;
        _status = NpcActionStatus.Running;
        _reason = null;
        return true;
    }

    /// <summary>Stops movement without cancelling auto-combat.</summary>
    internal void StopCommand()
    {
        _moving = false;
        _lookOnly = false;
        _faceId = null;
        _status = NpcActionStatus.Idle;
        _reason = null;
        StopMoving();
    }

    /// <summary>Cancels auto-combat and stops the current intent.</summary>
    internal void ClearIntent()
    {
        Disengage();
        StopCommand();
    }

    internal void LookAtPoint(Vector3 point)
    {
        Disengage();
        _moving = false;
        _lookOnly = false;
        _faceId = null;
        LookAt(point);
        _status = NpcActionStatus.Arrived;
        _reason = null;
        StopMoving();
    }

    internal void Face(string handle)
    {
        Disengage();
        _moving = false;
        _lookOnly = true;
        _faceId = handle;
        _status = NpcActionStatus.Running;
        _reason = null;
        StopMoving();
    }

    public override bool UpdateAI(float dt)
    {
        // Clients only mirror the body from the ZDO. If a client temporarily wins ZDO ownership
        // its controller must not fight the server by stopping or repathing the body.
        if (!GameEnvironment.IsServer())
        {
            return false;
        }

        ApplyName();
        if (!base.UpdateAI(dt))
        {
            return false;
        }

        if (_engaged && TickCombat(dt))
        {
            return true;
        }

        if (_lookOnly)
        {
            TickFace();
            return true;
        }

        if (!_moving)
        {
            StopMoving();
            return true;
        }

        TickMove(dt);
        return true;
    }

    private bool TickCombat(float dt)
    {
        var threat = _threat;
        if (threat == null || threat.IsDead())
        {
            Disengage();
            return false;
        }

        var distance = Vector3.Distance(transform.position, threat.transform.position);
        if (distance > 30f || Time.time - _threatAt > 20f)
        {
            Disengage();
            return false;
        }

        var humanoid = m_character as Humanoid;
        var weapon = humanoid?.GetCurrentWeapon();
        if (weapon == null && humanoid != null)
        {
            humanoid.EquipBestWeapon(threat, null, null, null);
            weapon = humanoid.GetCurrentWeapon();
        }

        if (weapon == null)
        {
            Flee(dt, threat.transform.position);
            return true;
        }

        var range = Mathf.Max(2f, weapon.m_shared.m_aiAttackRange);
        if (distance > range)
        {
            MoveTo(dt, threat.transform.position, range * 0.8f, distance > 8f);
        }
        else
        {
            StopMoving();
            LookAt(threat.GetTopPoint());
            if (CanUseAttack(weapon) && !m_character.InAttack() &&
                m_character.GetTimeSinceLastAttack() >= 0.5f)
            {
                m_character.StartAttack(threat, false);
            }
        }

        if (CanSeeTarget(threat))
        {
            _threatAt = Time.time;
        }

        return true;
    }

    private void Disengage()
    {
        if (!_engaged && _threat == null)
        {
            return;
        }

        _engaged = false;
        _threat = null;
        SetAlerted(false);
    }

    private void TickMove(float dt)
    {
        var distance = Horizontal(transform.position, _target);
        if (distance <= _stopDistance + ArriveSlop)
        {
            Arrive();
            return;
        }

        var now = Time.time;
        if (now >= _deadline)
        {
            Fail("nav-timeout");
            return;
        }

        if (Pathfinding.instance == null)
        {
            // No navmesh at all: straight-line movement is the only option; the intent deadline bounds it.
            MoveDirect();
            TrackProgress(now);
            return;
        }

        if (now >= _reachCheckAt)
        {
            _reachCheckAt = now + ReachCheckSeconds;
            _hasPath = HavePath(_target);
        }

        if (!_hasPath)
        {
            if (distance > DirectFallbackDistance)
            {
                Fail("nav-unreachable");
                return;
            }

            // Short hops into gaps the navmesh does not cover still work by walking.
            MoveDirect();
            TrackProgress(now);
            return;
        }

        if (MoveTo(dt, _target, _stopDistance, _run))
        {
            if (Horizontal(transform.position, _target) <= _stopDistance + ArriveSlop + 0.5f)
            {
                Arrive();
                return;
            }

            Fail("nav-incomplete");
            return;
        }

        TrackProgress(now);
    }

    private void MoveDirect()
    {
        var delta = _target - transform.position;
        delta.y = 0f;
        if (delta.sqrMagnitude <= 0.0001f)
        {
            return;
        }

        MoveTowards(delta.normalized, _run);
    }

    private void TrackProgress(float now)
    {
        var velocity = m_character.GetVelocity();
        velocity.y = 0f;
        if (velocity.sqrMagnitude > 0.25f)
        {
            _progressAt = now;
            _jumped = false;
            return;
        }

        if (now - _progressAt < ProgressSeconds)
        {
            return;
        }

        if (!_jumped)
        {
            _jumped = true;
            _progressAt = now;
            m_character.Jump(false);
            return;
        }

        Fail("nav-stuck");
    }

    private void TickFace()
    {
        if (string.IsNullOrWhiteSpace(_faceId) || !WorldManager.TryParseHandle(_faceId, out var id))
        {
            Fail("target-missing");
            return;
        }

        var zdo = ZDOMan.instance?.GetZDO(id);
        if (zdo == null)
        {
            Fail("target-missing");
            return;
        }

        LookAt(zdo.GetPosition());
        _status = NpcActionStatus.Arrived;
        _reason = null;
        _lookOnly = false;
        _faceId = null;
    }

    private void Arrive()
    {
        _moving = false;
        _status = NpcActionStatus.Arrived;
        _reason = null;
        StopMoving();
    }

    private void Fail(string reason)
    {
        _moving = false;
        _status = NpcActionStatus.Failed;
        _reason = reason;
        StopMoving();
    }

    private static float Horizontal(Vector3 left, Vector3 right)
    {
        var dx = left.x - right.x;
        var dz = left.z - right.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
