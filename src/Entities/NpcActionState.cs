using System;
using System.Collections.Generic;

namespace ValheimJS.Entities;

/// <summary>
/// Intent lifecycle for one NPC: the current action, its outcome and the terminal events that
/// consumers must observe. Pure state so it can be exercised without Unity.
/// </summary>
/// <remarks>
/// Rules:
/// <list type="bullet">
/// <item>Issuing a new action cancels an in-flight action and emits <see cref="NpcActionStatus.Cancelled"/> for it.</item>
/// <item>Every issue produces at most one terminal event.</item>
/// <item>Death is sticky: an in-flight action fails once with reason <c>dead</c> and later status
/// transitions are ignored until the body is alive again.</item>
/// <item>Waiting uses an absolute deadline in the caller's time domain so it survives a restart.</item>
/// </list>
/// </remarks>
internal sealed class NpcActionState
{
    private readonly List<NpcActionView> _settlements = new();
    private NpcAction _action = NpcAction.Idle;
    private NpcActionStatus _status = NpcActionStatus.Idle;
    private string? _reason;
    private double? _progress;
    private long _sequence;
    private bool _settled;
    private bool _dead;
    private float _startDistance;
    private float _waitUntil;

    internal NpcActionState()
    {
    }

    /// <summary>Restores a persisted intent. The restored outcome is treated as already reported.</summary>
    internal NpcActionState(
        NpcAction action,
        NpcActionStatus status,
        string? reason,
        double? progress,
        float startDistance,
        float waitUntil)
    {
        _action = action;
        _status = status;
        _reason = reason;
        _progress = progress;
        _startDistance = Math.Max(0f, startDistance);
        _waitUntil = waitUntil;
        _settled = status is NpcActionStatus.Arrived or NpcActionStatus.Failed or NpcActionStatus.Cancelled;
        _dead = status == NpcActionStatus.Failed && string.Equals(reason, "dead", StringComparison.Ordinal);
    }

    internal long Sequence => _sequence;

    internal NpcAction Action => _action;

    internal NpcActionStatus Status => _status;

    internal string? Reason => _reason;

    internal double? Progress => _progress;

    internal bool Dead => _dead;

    internal bool IsInFlight => _status is NpcActionStatus.Running or NpcActionStatus.Paused;

    internal bool IsTerminal => _status is NpcActionStatus.Arrived or NpcActionStatus.Failed or NpcActionStatus.Cancelled;

    internal float WaitUntil => _waitUntil;

    internal float StartDistance => _startDistance;

    /// <summary>Starts a new intent, cancelling the previous one when it was still in flight.</summary>
    internal void Issue(NpcAction action, float now, float startDistance)
    {
        if (IsInFlight)
        {
            Settle(NpcActionStatus.Cancelled, "superseded");
        }

        _sequence++;
        _action = action;
        _reason = null;
        _progress = null;
        _settled = false;
        _startDistance = Math.Max(0f, startDistance);
        _waitUntil = 0f;

        switch (action.Kind)
        {
            case NpcActionKind.MoveTo:
                _status = NpcActionStatus.Running;
                _progress = 0d;
                break;
            case NpcActionKind.Wait:
                _status = NpcActionStatus.Running;
                _waitUntil = (float)(now + action.Seconds);
                if (action.Seconds <= 0d)
                {
                    Settle(NpcActionStatus.Arrived, null);
                }

                break;
            case NpcActionKind.Idle:
            case NpcActionKind.Stop:
                _status = NpcActionStatus.Idle;
                break;
            default:
                _status = NpcActionStatus.Running;
                break;
        }
    }

    /// <summary>Records a terminal outcome once per issued action.</summary>
    internal bool Settle(NpcActionStatus status, string? reason)
    {
        if (status is not (NpcActionStatus.Arrived or NpcActionStatus.Failed or NpcActionStatus.Cancelled))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "settlement must be terminal");
        }

        if (_dead && status != NpcActionStatus.Failed)
        {
            return false;
        }

        if (_settled)
        {
            return false;
        }

        _settled = true;
        _status = status;
        _reason = reason;
        if (status == NpcActionStatus.Arrived && _action.Kind is NpcActionKind.MoveTo or NpcActionKind.Pick)
        {
            _progress = 1d;
        }

        if (_settlements.Count >= 64)
        {
            _settlements.RemoveAt(0);
        }

        _settlements.Add(View());
        return true;
    }

    /// <summary>Pauses an in-flight intent without settling it (body lost, combat override).</summary>
    internal void Pause(string reason)
    {
        if (!IsInFlight)
        {
            return;
        }

        _status = NpcActionStatus.Paused;
        _reason = reason;
    }

    /// <summary>Resumes a paused intent; ignored once the action has settled.</summary>
    internal void Resume()
    {
        if (_status != NpcActionStatus.Paused)
        {
            return;
        }

        _status = NpcActionStatus.Running;
        _reason = null;
    }

    /// <summary>Updates movement progress from the distance still to travel.</summary>
    internal void SetDistance(float remaining)
    {
        if (_action.Kind != NpcActionKind.MoveTo || !IsInFlight)
        {
            return;
        }

        if (_startDistance <= 0.05f)
        {
            _progress = null;
            return;
        }

        _progress = Math.Clamp(1d - remaining / _startDistance, 0d, 1d);
    }

    /// <summary>Completes a wait once its absolute deadline has passed.</summary>
    internal bool TickWait(float gameNow)
    {
        return _action.Kind == NpcActionKind.Wait && IsInFlight && gameNow >= _waitUntil &&
               Settle(NpcActionStatus.Arrived, null);
    }

    /// <summary>Fails an in-flight intent once when the body dies and blocks later outcomes.</summary>
    internal void MarkDead()
    {
        _dead = true;
        if (IsInFlight)
        {
            Settle(NpcActionStatus.Failed, "dead");
        }
    }

    internal void ClearDead() => _dead = false;

    internal IReadOnlyList<NpcActionView> TakeSettlements()
    {
        if (_settlements.Count == 0)
        {
            return Array.Empty<NpcActionView>();
        }

        var result = _settlements.ToArray();
        _settlements.Clear();
        return result;
    }

    internal NpcActionView View() => new()
    {
        Seq = _sequence,
        Kind = ActionName(_action.Kind),
        Status = StatusName(_status),
        Reason = _reason,
        X = _action.Kind is NpcActionKind.MoveTo or NpcActionKind.LookAt ? _action.X : null,
        Y = _action.Kind is NpcActionKind.MoveTo or NpcActionKind.LookAt ? _action.Y : null,
        Z = _action.Kind is NpcActionKind.MoveTo or NpcActionKind.LookAt ? _action.Z : null,
        TargetId = _action.Kind is NpcActionKind.Face or NpcActionKind.Pick ? _action.TargetId : null,
        Progress = _progress
    };

    internal static string ActionName(NpcActionKind kind) => kind switch
    {
        NpcActionKind.MoveTo => "moveTo",
        NpcActionKind.Wait => "wait",
        NpcActionKind.LookAt => "lookAt",
        NpcActionKind.Face => "face",
        NpcActionKind.Pick => "pick",
        NpcActionKind.Stop => "stop",
        _ => "idle"
    };

    internal static string StatusName(NpcActionStatus status) => status switch
    {
        NpcActionStatus.Running => "running",
        NpcActionStatus.Paused => "paused",
        NpcActionStatus.Arrived => "arrived",
        NpcActionStatus.Failed => "failed",
        NpcActionStatus.Cancelled => "cancelled",
        _ => "idle"
    };
}
