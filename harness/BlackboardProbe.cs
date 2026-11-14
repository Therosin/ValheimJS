using System;
using System.Collections.Generic;
using System.Linq;
using ValheimJS.AI;

internal static class BlackboardProbe
{
    internal static int Run()
    {
        try
        {
            var board = new Blackboard();
            var changes = new List<BlackboardChange>();
            var signals = new List<BlackboardSignal>();
            board.Changed += changes.Add;
            board.Signaled += signals.Add;

            board.Set("health", 10);
            board.Set("health", 10);
            board.Set("health", 5);
            board.Emit("hit", 9);

            if (changes.Count != 2 || changes[1].OldValue?.ToString() != "10" || changes[1].GetValue<int>() != 5)
                throw new InvalidOperationException("change semantics drifted");
            if (signals.Count != 1 || signals[0].Name != "hit" || signals[0].GetData<int>() != 9)
                throw new InvalidOperationException("signal semantics drifted");

            var channel = board.Channels.Get("npc-1");
            channel.Set("alert", true);
            if (!channel.Contains("alert") || board.Contains("alert"))
                throw new InvalidOperationException("channel state leaked");
            if (channel.Snapshot().Count != 1)
                throw new InvalidOperationException("snapshot drifted");

            var rootSeen = 0;
            using (board.Use(ctx => rootSeen++))
            {
                channel.Set("alert", false);
                if (rootSeen != 1) throw new InvalidOperationException("root middleware missed channel operation");
            }

            board.UseInterceptor(ctx =>
            {
                if (ctx.IsChange("blocked")) ctx.Cancel();
                if (ctx.IsChange("clamped")) ctx.Replace(100);
            });
            board.Set("blocked", true);
            board.Set("clamped", 200);
            if (board.Contains("blocked") || board.GetOrDefault("clamped", 0) != 100)
                throw new InvalidOperationException("interceptor semantics drifted");

            if (!board.Channels.TryGet("npc-1", out var same) || !ReferenceEquals(same, channel))
                throw new InvalidOperationException("channel identity drifted");

            Console.WriteLine("PASS: reactive blackboard state, signals, channels, middleware and interceptors");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: blackboard probe: " + exception);
            return 1;
        }
    }
}
