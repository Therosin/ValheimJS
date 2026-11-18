using System;
using ValheimJS.Entities;

internal static class NpcProbe
{
    internal static int Run()
    {
        try
        {
            ActionLifecycle();
            DeadStickiness();
            WaitDeadline();
            InventoryPayload();
            DefinitionDefaults();
            Audibility();
            FindQuery();
            Console.WriteLine("PASS: npc action lifecycle, wait deadlines, death stickiness, inventory payload, audibility and find queries");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: npc probe: " + exception);
            return 1;
        }
    }

    private static void ActionLifecycle()
    {
        var state = new NpcActionState();
        state.Issue(NpcAction.MoveTo(10, 0, 0, false), 0f, 10f);
        if (!state.IsInFlight || state.Sequence != 1)
            throw new InvalidOperationException("moveTo did not start");

        state.SetDistance(5f);
        if (state.Progress is not { } progress || Math.Abs(progress - 0.5d) > 0.001d)
            throw new InvalidOperationException("move progress drifted");

        state.Issue(NpcAction.LookAt(0, 0, 0), 1f, 0f);
        var settled = state.TakeSettlements();
        if (settled.Count != 1 || settled[0].Seq != 1 || settled[0].Status != "cancelled" ||
            settled[0].Reason != "superseded")
            throw new InvalidOperationException("superseded action did not report cancelled");

        state.Settle(NpcActionStatus.Arrived, null);
        state.Settle(NpcActionStatus.Arrived, null);
        settled = state.TakeSettlements();
        if (settled.Count != 1 || settled[0].Seq != 2 || settled[0].Status != "arrived")
            throw new InvalidOperationException("terminal outcome was not reported exactly once");

        state.Issue(NpcAction.LookAt(1, 0, 0), 2f, 0f);
        state.Settle(NpcActionStatus.Arrived, null);
        state.Issue(NpcAction.LookAt(2, 0, 0), 3f, 0f);
        state.Settle(NpcActionStatus.Arrived, null);
        settled = state.TakeSettlements();
        if (settled.Count != 2 || settled[0].Seq == settled[1].Seq)
            throw new InvalidOperationException("consecutive settled actions were collapsed");

        var restored = new NpcActionState(NpcAction.MoveTo(1, 0, 0, false), NpcActionStatus.Arrived,
            null, 1d, 10f, 0f);
        if (restored.TakeSettlements().Count != 0)
            throw new InvalidOperationException("restored terminal action replayed its outcome");
    }

    private static void DeadStickiness()
    {
        var state = new NpcActionState();
        state.Issue(NpcAction.MoveTo(5, 0, 0, false), 0f, 5f);
        state.MarkDead();
        state.MarkDead();
        state.Settle(NpcActionStatus.Arrived, null);
        var settled = state.TakeSettlements();
        if (settled.Count != 1 || settled[0].Status != "failed" || settled[0].Reason != "dead")
            throw new InvalidOperationException("death did not settle the intent once");

        state.ClearDead();
        state.Issue(NpcAction.LookAt(0, 0, 0), 1f, 0f);
        state.Settle(NpcActionStatus.Arrived, null);
        if (state.TakeSettlements().Count != 1)
            throw new InvalidOperationException("action did not resume after death cleared");
    }

    private static void WaitDeadline()
    {
        var state = new NpcActionState();
        state.Issue(NpcAction.Wait(5), 100f, 0f);
        if (state.TickWait(104.9f))
            throw new InvalidOperationException("wait completed early");
        if (!state.TickWait(105f))
            throw new InvalidOperationException("wait did not complete at its deadline");
        if (state.TakeSettlements().Count != 1)
            throw new InvalidOperationException("wait outcome missing");

        var instant = new NpcActionState();
        instant.Issue(NpcAction.Wait(0), 10f, 0f);
        var settled = instant.TakeSettlements();
        if (settled.Count != 1 || settled[0].Status != "arrived")
            throw new InvalidOperationException("zero-second wait did not settle immediately");
    }

    private static void InventoryPayload()
    {
        var payload = new NpcInventoryPayload();
        payload.Items.Add(new NpcItemRecord
        {
            PrefabHash = 1234, Stack = 3, Quality = 2, Durability = 7.5f, X = 4, Y = 1
        });
        payload.Equipment.Add(new NpcEquipmentRecord
        {
            Slot = "helmet", PrefabHash = 99, X = 0, Y = 0
        });

        var text = payload.Encode();
        if (!NpcInventoryPayload.TryDecode(text, out var decoded, out var error))
            throw new InvalidOperationException("payload did not decode: " + error);
        if (decoded.Skipped != 0 || decoded.Items.Count != 1 || decoded.Equipment.Count != 1)
            throw new InvalidOperationException("payload record counts drifted");
        var item = decoded.Items[0];
        if (item.PrefabHash != 1234 || item.Stack != 3 || item.Quality != 2 ||
            Math.Abs(item.Durability - 7.5f) > 0.001f || item.X != 4 || item.Y != 1)
            throw new InvalidOperationException("payload item drifted");
        var equipment = decoded.Equipment[0];
        if (equipment.Slot != "helmet" || equipment.PrefabHash != 99)
            throw new InvalidOperationException("payload equipment drifted");

        if (NpcInventoryPayload.TryDecode("2;I,1,1,1,1,0,0", out _, out _))
            throw new InvalidOperationException("unknown payload version was accepted");

        if (!NpcInventoryPayload.TryDecode("1;I,bad;Z,7", out var damaged, out _) || damaged.Skipped != 1)
            throw new InvalidOperationException("malformed record was not counted");

        if (!NpcInventoryPayload.TryDecode("1;Z,7", out var unknown, out _) || unknown.Skipped != 0)
            throw new InvalidOperationException("unknown record type was not ignored");
    }

    private static void DefinitionDefaults()
    {
        if (!NpcDefinition.TryCreate("villager", "Player", 500d, null, null, null, false, true,
                out var definition, out var error))
            throw new InvalidOperationException("definition rejected: " + error);
        if (definition.AutoEngage)
            throw new InvalidOperationException("auto engagement must be opt-in");
        if (!definition.AutoOpenDoors)
            throw new InvalidOperationException("door opening should default on");

        if (!NpcDefinition.TryCreate("guard", "Player", 500d, null, null, null, true, false,
                out var guard, out _) || !guard.AutoEngage || guard.AutoOpenDoors)
            throw new InvalidOperationException("definition options were lost");
    }

    private static void FindQuery()
    {
        var tree = new ValheimJS.Entities.NpcSensedEntity
        {
            Id = "1:1",
            Kind = "tree",
            Prefab = "Beech1",
            X = 4d,
            Z = 0d,
            InRange = true,
            Ready = true,
            Usable = true
        };
        tree.Tags.Add("tree");
        tree.Tags.Add("wood");
        tree.Verbs.Add("harvest");
        tree.Requires.Add("chop");

        var rock = new ValheimJS.Entities.NpcSensedEntity
        {
            Id = "1:2",
            Kind = "rock",
            Prefab = "Rock_4",
            X = 8d,
            Z = 0d,
            InRange = true,
            Ready = true,
            Usable = true
        };
        rock.Tags.Add("rock");
        rock.Tags.Add("stone");
        rock.Verbs.Add("harvest");
        rock.Requires.Add("pickaxe");

        var query = ValheimJS.Entities.NpcFindQuery.FromPlain(new Dictionary<string, object?>
        {
            ["where"] = new Dictionary<string, object?>
            {
                ["tags"] = new Dictionary<string, object?> { ["anyOf"] = new List<object?> { "wood", "sapling" } },
                ["distance"] = new Dictionary<string, object?> { ["lte"] = 10d },
                ["usable"] = true
            },
            ["orderBy"] = "distance",
            ["take"] = 1
        }, "tree");

        Assert(query.Allows(tree, 0d, 0d), "find query rejected a matching tree");
        Assert(!query.Allows(rock, 0d, 0d), "find query accepted a rock for a wood/tree query");

        var all = ValheimJS.Entities.NpcFindQuery.FromPlain(new Dictionary<string, object?>
        {
            ["where"] = new Dictionary<string, object?> { ["verbs"] = new List<object?> { "harvest" } },
            ["orderBy"] = "distance"
        });
        Assert(all.Allows(tree, 0d, 0d) && all.Allows(rock, 0d, 0d), "verb query rejected a harvest target");

        var either = ValheimJS.Entities.NpcFindQuery.FromPlain(new Dictionary<string, object?>
        {
            ["where"] = new Dictionary<string, object?>
            {
                ["or"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["kind"] = "tree" },
                    new Dictionary<string, object?> { ["kind"] = "rock" }
                }
            }
        });
        Assert(either.Allows(tree, 0d, 0d) && either.Allows(rock, 0d, 0d), "or group rejected a match");

        var withoutStone = ValheimJS.Entities.NpcFindQuery.FromPlain(new Dictionary<string, object?>
        {
            ["where"] = new Dictionary<string, object?>
            {
                ["and"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["tags"] = new Dictionary<string, object?> { ["anyOf"] = new List<object?> { "wood", "stone" } }
                    },
                    new Dictionary<string, object?>
                    {
                        ["not"] = new Dictionary<string, object?> { ["kind"] = "rock" }
                    }
                }
            }
        });
        Assert(withoutStone.Allows(tree, 0d, 0d), "not group rejected a tree");
        Assert(!withoutStone.Allows(rock, 0d, 0d), "not group accepted a rock");

        Throws<ArgumentException>(() => ValheimJS.Entities.NpcFindQuery.FromPlain(new Dictionary<string, object?>
        {
            ["where"] = new Dictionary<string, object?> { ["colour"] = "red" }
        }), "unknown where field");
    }

    private static void Audibility()
    {
        if (!NpcAudibility.IsAudible(10f, 15f, 20f, false))
            throw new InvalidOperationException("noise inside range should be audible");
        if (NpcAudibility.IsAudible(19f, 15f, 20f, false))
            throw new InvalidOperationException("distance beyond the noise radius must not be audible");
        if (NpcAudibility.IsAudible(13f, 20f, 20f, true))
            throw new InvalidOperationException("interior hearing must cap at 12 m");
        if (!NpcAudibility.IsAudible(11f, 20f, 20f, true))
            throw new InvalidOperationException("interior hearing inside the cap must work");
        if (NpcAudibility.IsAudible(5f, 0f, 20f, false))
            throw new InvalidOperationException("silent targets must not be audible");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException("expected " + typeof(TException).Name + ": " + message);
    }
}
