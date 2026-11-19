using System;
using Jint;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Native;
using ValheimJS.Planning;
using ValheimJS.Scripting.Binding;
using ValheimJS.Scripting.Types;

namespace ValheimJS.Bindings;

/// <summary>
/// Native plan execution and (later) goal scheduling. Plans are scoped to the calling plugin and
/// released when it unloads.
/// </summary>
[JsBinder("planning", Phase = JsBindPhase.Gameplay, Order = 146,
    Description = "Native plan execution and goal scheduling mechanics.")]
internal sealed class PlanningBindings : ScopedBindings<string, PlanHost>
{
    private readonly Dictionary<string, int> _planIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _plannerIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, PlannerHost>> _planners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _utilityIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, UtilityHost>> _utilities = new(StringComparer.Ordinal);

    public PlanningBindings(JsBindingContext context) : base(context)
    {
    }

    [JsBinding("planCreate", Scoped = true,
        Description = "Creates a native plan. Steps are advanced by planTick; dispatch handles non-native steps.")]
    public string PlanCreate(string plugin, JsValue actor, JsValue steps, JsValue options, JsCallback dispatch)
    {
        if (!steps.IsArray())
        {
            throw new ArgumentException("planning.planCreate: steps must be an array.");
        }

        var id = "plan" + Next(_planIds, plugin);
        var plan = new PlanHost(
            plugin,
            id,
            actor,
            steps,
            PlanOptionsData.Parse(options),
            dispatch,
            new Dictionary<string, object?>(StringComparer.Ordinal));
        For(plugin)[id] = plan;
        return id;
    }

    [JsBinding("planTick", Scoped = true, ReturnKind = JsReturnKind.Native, Returns = "PlanTickResult",
        Description = "Advances a plan and returns its current status, reason and step index.")]
    public JsValue PlanTick(string plugin, string id, JsValue view, double now)
    {
        var plan = Require(plugin, id, "planning: plan");
        plan.Tick(view, now);
        return plan.Snapshot();
    }

    [JsBinding("planStatus", Scoped = true, Description = "Current plan status.")]
    public string PlanStatus(string plugin, string id) =>
        Require(plugin, id, "planning: plan").Status;

    [JsBinding("planReason", Scoped = true, Returns = "string | null",
        Description = "Terminal plan reason, or null.")]
    public string? PlanReason(string plugin, string id) =>
        Require(plugin, id, "planning: plan").Reason;

    [JsBinding("planStepIndex", Scoped = true, Description = "Zero-based step index.")]
    public double PlanStepIndex(string plugin, string id) =>
        Require(plugin, id, "planning: plan").StepIndex;

    [JsBinding("planCancel", Scoped = true, Description = "Cancels a running plan; terminal plans are unchanged.")]
    public void PlanCancel(string plugin, string id, [JsNullable] string? reason) =>
        Require(plugin, id, "planning: plan").Cancel(reason);

    [JsBinding("planRelease", Scoped = true, Description = "Cancels and drops a plan.")]
    public bool PlanRelease(string plugin, string id)
    {
        if (!Remove(plugin, id, out var plan))
        {
            return false;
        }

        plan.Cancel("released");
        return true;
    }

    [JsBinding("plannerCreate", Scoped = true,
        Description = "Creates a host-owned goal scheduler. Goal callbacks cross through dispatch.")]
    public string PlannerCreate(string plugin, JsValue actor, JsValue options, JsCallback dispatch)
    {
        var id = "planner" + Next(_plannerIds, plugin);
        var planner = new PlannerHost(
            plugin,
            id,
            actor,
            PlannerOptionsData.Parse(options),
            dispatch,
            behaviours: Context.Behaviours);
        Planners(plugin)[id] = planner;
        return id;
    }

    [JsBinding("plannerSetGoals", Scoped = true,
        Description = "Replaces goal metadata. Goal callbacks remain in JS and are addressed by id.")]
    public void PlannerSetGoals(string plugin, string id, JsValue goals)
    {
        var planner = RequirePlanner(plugin, id);
        if (!goals.IsArray())
        {
            throw new ArgumentException("planning.plannerSetGoals: goals must be an array.");
        }

        var parsed = new List<GoalMeta>();
        var array = goals.AsArray();
        for (var index = 0; index < (int)array.Length; index++)
        {
            if (array.Get(index) is not ObjectInstance goal)
            {
                throw new ArgumentException("planning.plannerSetGoals: every goal must be an object.");
            }

            parsed.Add(ParseGoal(goal));
        }

        planner.SetGoals(parsed);
    }

    [JsBinding("plannerTick", Scoped = true, Returns = "string | null",
        Description = "Selects, preempts and ticks the active goal. Returns the active goal id or null.")]
    public string? PlannerTick(string plugin, string id, JsValue view, double now) =>
        RequirePlanner(plugin, id).Tick(view, now);

    [JsBinding("plannerCandidates", Scoped = true, ReturnKind = JsReturnKind.Native,
        Returns = "{ goal: string; score: number }[]",
        Description = "Eligible, non-cooled goals with their current scores. Does not mutate selection.")]
    public JsValue PlannerCandidates(string plugin, string id, JsValue view, double now)
    {
        var planner = RequirePlanner(plugin, id);
        return JsPayload.Array(
            Engine,
            planner.Candidates(view, now).Select(candidate => (JsValue?)JsPayload.Object(Engine)
                .Set("goal", candidate.Id)
                .Set("score", candidate.Score)
                .Build()));
    }

    [JsBinding("plannerRequest", Scoped = true,
        Description = "Forces a known goal on the next eligible tick until an absolute clock value.")]
    public void PlannerRequest(string plugin, string id, string goalId, double until) =>
        RequirePlanner(plugin, id).Request(goalId, until);

    [JsBinding("plannerSetPlan", Scoped = true,
        Description = "Associates the active goal with a plan created by planCreate; null clears it.")]
    public void PlannerSetPlan(string plugin, string id, [JsNullable] string? planId)
    {
        var planner = RequirePlanner(plugin, id);
        if (planId == null)
        {
            planner.SetPlan(null, null);
            return;
        }

        var plan = Require(plugin, planId, "planning: plan");
        planner.SetPlan(planId, plan);
    }

    [JsBinding("utilityCreate", Scoped = true,
        Description = "Creates a host-owned utility policy over the same goal scheduler.")]
    public string UtilityCreate(string plugin, JsValue actor, JsValue options, JsCallback dispatch)
    {
        var id = "utility" + Next(_utilityIds, plugin);
        var utility = new UtilityHost(plugin, id, actor, options, dispatch, Context.Behaviours);
        Utilities(plugin)[id] = utility;
        return id;
    }

    [JsBinding("utilitySetGoals", Scoped = true,
        Description = "Replaces utility goal metadata. Descriptions and rules stay in JS callbacks.")]
    public void UtilitySetGoals(string plugin, string id, JsValue goals)
    {
        var utility = RequireUtility(plugin, id);
        if (!goals.IsArray())
        {
            throw new ArgumentException("planning.utilitySetGoals: goals must be an array.");
        }

        var parsed = new List<GoalMeta>();
        var array = goals.AsArray();
        for (var index = 0; index < (int)array.Length; index++)
        {
            if (array.Get(index) is not ObjectInstance goal)
            {
                throw new ArgumentException("planning.utilitySetGoals: every goal must be an object.");
            }

            parsed.Add(ParseGoal(goal));
        }

        utility.SetGoals(parsed);
    }

    [JsBinding("utilityTick", Scoped = true, Returns = "string | null",
        Description = "Selects, preempts and ticks the active utility goal.")]
    public string? UtilityTick(string plugin, string id, JsValue view, double now) =>
        RequireUtility(plugin, id).Tick(view, now);

    [JsBinding("utilityAvailable", Scoped = true, ReturnKind = JsReturnKind.Native,
        Returns = "{ goal: string; score: number }[]",
        Description = "Goals whose current prerequisites are satisfied.")]
    public JsValue UtilityAvailable(string plugin, string id, JsValue view, double now)
    {
        var utility = RequireUtility(plugin, id);
        return JsPayload.Array(
            Engine,
            utility.Available(view, now).Select(candidate => (JsValue?)JsPayload.Object(Engine)
                .Set("goal", candidate.Id)
                .Set("score", candidate.Score)
                .Build()));
    }

    [JsBinding("utilityStats", Scoped = true, ReturnKind = JsReturnKind.Native,
        Returns = "Record<string, number>",
        Description = "Normalized utility stats.")]
    public JsValue UtilityStats(string plugin, string id) =>
        JsPlain.FromPlain(Engine, RequireUtility(plugin, id).Stats());

    [JsBinding("utilityLastOutcome", Scoped = true, ReturnKind = JsReturnKind.Native,
        Returns = "Record<string, unknown> | null",
        Description = "The latest settled feedback context, or null.")]
    public JsValue UtilityLastOutcome(string plugin, string id) =>
        RequireUtility(plugin, id).LastOutcome();

    [JsBinding("utilitySetStats", Scoped = true,
        Description = "Merges partial stats, clamps them and saves through the plugin callback.")]
    public void UtilitySetStats(string plugin, string id, JsValue stats) =>
        RequireUtility(plugin, id).SetStats(stats);

    [JsBinding("utilityRecord", Scoped = true,
        Description = "Records an observation while a utility goal is active.")]
    public bool UtilityRecord(string plugin, string id, string name, JsValue data, JsValue view) =>
        RequireUtility(plugin, id).Record(name, data, view);

    [JsBinding("utilityRequest", Scoped = true,
        Description = "Forces a known utility goal until an absolute clock value.")]
    public void UtilityRequest(string plugin, string id, string goalId, double until) =>
        RequireUtility(plugin, id).Request(goalId, until);

    [JsBinding("utilitySetPlan", Scoped = true,
        Description = "Associates the active utility goal with a native plan; null clears it.")]
    public void UtilitySetPlan(string plugin, string id, [JsNullable] string? planId)
    {
        var utility = RequireUtility(plugin, id);
        if (planId == null)
        {
            utility.SetPlan(null, null);
            return;
        }

        utility.SetPlan(planId, Require(plugin, planId, "planning: plan"));
    }

    [JsBinding("utilityCurrent", Scoped = true, Returns = "string | null",
        Description = "The active utility goal id, or null.")]
    public string? UtilityCurrent(string plugin, string id) =>
        RequireUtility(plugin, id).Planner.ActiveGoal;

    [JsBinding("utilityCurrentElapsedMs", Scoped = true,
        Description = "Milliseconds the active utility goal has been running.")]
    public double UtilityCurrentElapsedMs(string plugin, string id) =>
        RequireUtility(plugin, id).Planner.ActiveElapsedMs;

    [JsBinding("utilityReset", Scoped = true,
        Description = "Drops active state, plan, prerequisites and cooldowns.")]
    public void UtilityReset(string plugin, string id) =>
        RequireUtility(plugin, id).Reset();

    [JsBinding("utilityDispose", Scoped = true, Description = "Releases a utility host and its active plan.")]
    public bool UtilityDispose(string plugin, string id)
    {
        if (!_utilities.TryGetValue(plugin, out var utilities) || !utilities.Remove(id, out var utility))
        {
            return false;
        }

        utility.Dispose();
        return true;
    }

    [JsBinding("behaviourCompile", Scoped = true,
        Description = "Compiles a shared behaviour tree for planning goals; returns a tree id.")]
    public string BehaviourCompile(string plugin, string yaml, JsValue options) =>
        Context.Behaviours.Compile(plugin, yaml, options);

    [JsBinding("behaviourLoad", Scoped = true,
        Description = "Compiles a behaviour YAML file confined to this plugin's folder.")]
    public string BehaviourLoad(string plugin, string relativePath, JsValue options)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("planning.behaviourLoad: path is required.");
        }

        var root = Path.GetFullPath(Context.PluginDirectoryFor(plugin));
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("planning.behaviourLoad: path escapes the plugin folder.");
        }

        return Context.Behaviours.Compile(plugin, File.ReadAllText(path), options);
    }

    [JsBinding("plannerCurrent", Scoped = true, Returns = "string | null",
        Description = "The active goal id, or null.")]
    public string? PlannerCurrent(string plugin, string id) =>
        RequirePlanner(plugin, id).ActiveGoal;

    [JsBinding("plannerCurrentElapsedMs", Scoped = true,
        Description = "Milliseconds the active goal has been running.")]
    public double PlannerCurrentElapsedMs(string plugin, string id) =>
        RequirePlanner(plugin, id).ActiveElapsedMs;

    [JsBinding("plannerReset", Scoped = true, Description = "Drops active state, plan and cooldowns; goals stay installed.")]
    public void PlannerReset(string plugin, string id) =>
        RequirePlanner(plugin, id).Reset();

    [JsBinding("plannerDispose", Scoped = true, Description = "Releases a planner and its active plan.")]
    public bool PlannerDispose(string plugin, string id)
    {
        if (!_planners.TryGetValue(plugin, out var planners) || !planners.Remove(id, out var planner))
        {
            return false;
        }

        planner.Dispose();
        return true;
    }

    private UtilityHost RequireUtility(string plugin, string id)
    {
        if (_utilities.TryGetValue(plugin, out var utilities) && utilities.TryGetValue(id, out var utility))
        {
            return utility;
        }

        throw new InvalidOperationException($"planning: utility {id} does not exist for plugin '{plugin}'.");
    }

    private Dictionary<string, UtilityHost> Utilities(string plugin)
    {
        if (!_utilities.TryGetValue(plugin, out var utilities))
        {
            utilities = new Dictionary<string, UtilityHost>(StringComparer.Ordinal);
            _utilities[plugin] = utilities;
        }

        return utilities;
    }

    private PlannerHost RequirePlanner(string plugin, string id)
    {
        if (_planners.TryGetValue(plugin, out var planners) && planners.TryGetValue(id, out var planner))
        {
            return planner;
        }

        throw new InvalidOperationException($"planning: planner {id} does not exist for plugin '{plugin}'.");
    }

    private Dictionary<string, PlannerHost> Planners(string plugin)
    {
        if (!_planners.TryGetValue(plugin, out var planners))
        {
            planners = new Dictionary<string, PlannerHost>(StringComparer.Ordinal);
            _planners[plugin] = planners;
        }

        return planners;
    }

    private GoalMeta ParseGoal(ObjectInstance value)
    {
        var id = value.Get("id");
        if (!id.IsString() || string.IsNullOrWhiteSpace(id.AsString()))
        {
            throw new ArgumentException("planning.plannerSetGoals: goal id must be a non-empty string.");
        }

        var goal = new GoalMeta
        {
            Id = id.AsString(),
            CooldownMs = ReadNumber(value, "cooldownMs") ?? 0,
            Hysteresis = ReadNumber(value, "hysteresis") ?? 0,
            CommitMs = ReadNumber(value, "commitMs") ?? 0
        };

        var urgent = value.Get("urgent");
        goal.Urgent = urgent.IsBoolean() && urgent.AsBoolean();
        var hasTick = value.Get("hasTick");
        goal.HasTick = (hasTick.IsBoolean() && hasTick.AsBoolean()) || value.Get("tick") is Function;

        if (value.Get("behaviour") is ObjectInstance behaviour)
        {
            var tree = behaviour.Get("tree");
            if (tree.IsString())
            {
                goal.BehaviourTree = tree.AsString();
            }

            goal.BehaviourAction = Callback(behaviour, "action", "planning: behaviour action");
            goal.BehaviourCondition = Callback(behaviour, "condition", "planning: behaviour condition");
            goal.BehaviourExec = Callback(behaviour, "exec", "planning: behaviour exec");
            goal.BehaviourRandom = Callback(behaviour, "random", "planning: behaviour random");
            var state = behaviour.Get("state");
            if (state is Function stateFactory)
            {
                goal.BehaviourStateFactory = new JsCallback(Engine, stateFactory, "planning: behaviour state");
            }
            else
            {
                goal.BehaviourState = state;
            }

            var jumps = behaviour.Get("maxJumpsPerTick");
            if (jumps.IsNumber())
            {
                goal.BehaviourJumps = (int)Math.Clamp(jumps.AsNumber(), 1, 10000);
            }
        }

        return goal;
    }

    private JsCallback? Callback(ObjectInstance options, string name, string label)
    {
        var value = options.Get(name);
        if (value.IsUndefined() || value.IsNull())
        {
            return null;
        }

        return value is Function function
            ? new JsCallback(Engine, function, label)
            : throw new ArgumentException($"{label} must be a function.");
    }

    private static double? ReadNumber(ObjectInstance value, string name)
    {
        var number = value.Get(name);
        if (number.IsUndefined() || number.IsNull())
        {
            return null;
        }

        if (!number.IsNumber())
        {
            throw new ArgumentException($"planning: goal field '{name}' must be a number.");
        }

        var result = number.AsNumber();
        return double.IsNaN(result) || double.IsInfinity(result) ? null : result;
    }

    protected override void OnRelease(PlanHost value) => value.Cancel("plugin-released");

    public override void Release(string plugin)
    {
        if (_planners.Remove(plugin, out var planners))
        {
            foreach (var planner in planners.Values)
            {
                planner.Dispose();
            }
        }

        if (_utilities.Remove(plugin, out var utilities))
        {
            foreach (var utility in utilities.Values)
            {
                utility.Dispose();
            }
        }

        base.Release(plugin);
        _planIds.Remove(plugin);
        _plannerIds.Remove(plugin);
        _utilityIds.Remove(plugin);
    }

    private static int Next(Dictionary<string, int> counters, string plugin)
    {
        counters.TryGetValue(plugin, out var value);
        value++;
        counters[plugin] = value;
        return value;
    }
}
