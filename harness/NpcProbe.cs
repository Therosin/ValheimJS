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
            Console.WriteLine("PASS: npc action lifecycle, wait deadlines, death stickiness, inventory payload and audibility");
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
        if (!NpcDefinition.TryCreate("villager", "Player", 500d, null, null, null, false,
                out var definition, out var error))
            throw new InvalidOperationException("definition rejected: " + error);
        if (definition.AutoEngage)
            throw new InvalidOperationException("auto engagement must be opt-in");

        if (!NpcDefinition.TryCreate("guard", "Player", 500d, null, null, null, true,
                out var guard, out _) || !guard.AutoEngage)
            throw new InvalidOperationException("auto engagement option was lost");
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
}
