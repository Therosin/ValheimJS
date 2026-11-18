using System;
using ValheimJS.World;

internal static class ResourceProbe
{
    internal static int Run()
    {
        try
        {
            if (ResourceIntents.TryReserve("", "a", 1000))
                throw new InvalidOperationException("blank handle was reserved");
            if (!ResourceIntents.TryReserve("1:2", "a", 1000))
                throw new InvalidOperationException("first owner could not reserve");
            if (ResourceIntents.TryReserve("1:2", "b", 1000))
                throw new InvalidOperationException("second owner stole a live reservation");
            if (!ResourceIntents.TryReserve("1:2", "a", 1000))
                throw new InvalidOperationException("owner could not renew its reservation");
            ResourceIntents.Release("1:2", "b");
            if (ResourceIntents.TryReserve("1:2", "b", 1000))
                throw new InvalidOperationException("non-owner release removed the reservation");
            ResourceIntents.Release("1:2", "a");
            if (!ResourceIntents.TryReserve("1:2", "a", 1))
                throw new InvalidOperationException("owner could not reserve after release");
            System.Threading.Thread.Sleep(180);
            if (!ResourceIntents.TryReserve("1:2", "b", 1000))
                throw new InvalidOperationException("expired reservation blocked another owner");
            Console.WriteLine("PASS: resource reservation intent ownership, release and expiry");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: resource probe: " + exception);
            return 1;
        }
    }
}
