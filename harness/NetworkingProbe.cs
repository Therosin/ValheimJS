using System.Text;
using ValheimJS.Bindings;
using ValheimJS.Core;
using ValheimJS.Networking;
using ValheimJS.Scripting;
using Network = ValheimJS.Core.Networking;

internal static class NetworkingProbe
{
    internal static int Run()
    {
        try
        {
            CheckEvents();
            CheckSync();
            CheckBindings();
            Console.WriteLine("PASS: native channels, owner cleanup, cancellation, main-thread completion, binary sync and real JS adapters");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine("FAIL: networking probe: " + exception); return 1; }
    }

    private static void CheckEvents()
    {
        var transport = new FakeTransport();
        var errors = new List<string>();
        using var network = new Network(transport, errors.Add);
        using var scope = network.OpenScope();
        var a = scope.Channel("shop", "a");
        var b = scope.Channel("shop", "b");
        var received = new List<string>();
        var first = a.On<string>("notice", (_, value) => received.Add("a:" + value));
        b.On<string>("notice", (_, value) => received.Add("b:" + value));
        b.Off(first);
        a.Emit<string>(42, "notice", "hello");
        Assert(received.SequenceEqual(new[] { "a:hello", "b:hello" }), "typed listeners or owner isolation");
        scope.Release("a");
        b.Emit<string>(42, "notice", "again");
        Assert(received.Last() == "b:again" && received.Count == 3, "owner release removed unrelated listener");
        b.Handle<int, int>("double", (_, value) => value * 2);
        string? reply = null;
        scope.Request("b", 42, "shop", "double", "21", 1000, (error, value) => { Assert(error == null, "request failed"); reply = value; });
        Assert(reply == null, "request completion escaped main-thread pump");
        network.Pump();
        Assert(reply == "42", "request/reply conversion");

        transport.DelayRequests = true;
        var called = false;
        scope.Request("pending", 42, "shop", "double", "1", 1000, (_, _) => called = true);
        var cancellation = transport.LastCancellation;
        scope.Release("pending");
        Assert(cancellation.IsCancellationRequested, "owner release did not cancel transport request");
        network.Pump();
        Assert(!called, "released owner received late completion");
        transport.DelayRequests = false;
        scope.Request("b", 42, "shop", "missing", "1", 1000, (error, _) => reply = error);
        network.Pump();
        Assert(reply == "No responder", "request error was lost");
        scope.Request("b", 42, "shop", "double", "1", 1000, (_, _) => throw new InvalidOperationException("bad callback"));
        network.Pump();
        Assert(errors.Any(error => error.Contains("bad callback")), "callback failure was not attributed");
        Throws(() => scope.Request("b", 42, "shop", "double", "1", 0, (_, _) => { }), "invalid timeout");
        scope.Dispose();
        Assert(transport.Registrations == 0, "scope disposal leaked listeners");
        Throws(() => scope.Emit(42, "shop", "event", "{}"), "disposed scope");
    }

    private static void CheckSync()
    {
        var entities = new[] { new SyncEntity { Id = 1, X = 1.5f, Y = 2.25f, Z = -3.75f, RotY = .5f }, new SyncEntity { Id = 2, Flags = 3 }, new SyncEntity { Id = 3, X = .01f } };
        const string fixture = "AQADAAEPP8AAAEAQAADAcAAAPwAAAAACEAMAAwE8I9cK";
        var frame = NetworkSync.Encode(entities);
        Assert(Convert.ToBase64String(frame) == fixture, "C# codec differs from the independently captured TS wire fixture");
        var decoded = NetworkSync.Decode(frame);
        Assert(decoded.Length == 3 && decoded[0].X == 1.5f && decoded[0].RotY == .5f && decoded[1].Flags == 3 && !decoded[2].Y.HasValue, "partial frame round-trip");
        Assert(NetworkSync.Encode(Enumerable.Range(0, 100).Select(id => new SyncEntity { Id = id, X = id, Y = 1, Z = -id }).ToArray()).Length == 1503, "compact binary size");
        var before = new SyncEntity { Id = 7, X = 10, Y = 2 };
        Assert(NetworkSync.Diff(before, new SyncEntity { Id = 7, X = 10.004f, Y = 2 }) == null, "sub-threshold delta");
        Assert(NetworkSync.Diff(before, new SyncEntity { Id = 7, X = 10.4f, Y = 2 })!.Y == null, "unchanged fields retained");
        Throws(() => NetworkSync.Decode(frame[..^1]), "truncated frame");
        Throws(() => NetworkSync.Decode(frame.Concat(new byte[] { 0 }).ToArray()), "trailing bytes");
        Throws(() => NetworkSync.Decode(new byte[] { 1, 0, 1, 0, 1, 32 }), "unknown mask");
        Throws(() => NetworkSync.Encode(new[] { new SyncEntity { Id = 65536 } }), "out of range id");
        Throws(() => NetworkSync.Encode(new[] { new SyncEntity { Id = 1, X = float.NaN } }), "non-finite field");
        Throws(() => NetworkSync.Encode(new[] { new SyncEntity { Id = 1 }, new SyncEntity { Id = 1 } }), "duplicate ids");
        Throws(() => NetworkSync.Diff(before, new SyncEntity { Id = 8 }), "different entity ids");
        Throws(() => NetworkSync.Quantise(1, 0), "invalid precision");
        var transport = new FakeTransport();
        using var sync = new NetworkSync(transport);
        using var scope = sync.OpenScope();
        SyncEntity[]? received = null;
        var id = scope.On("a", "positions", (_, data) => received = data);
        scope.Off("b", id);
        scope.Send(42, "positions", entities);
        Assert(received?.Length == 3 && transport.LastPayload!.SequenceEqual(frame), "sync did not send native bytes");
        scope.Release("a");
        Assert(transport.Registrations == 0, "sync owner release leaked listener");
    }

    private static void CheckBindings()
    {
        var root = Path.Combine(Path.GetTempPath(), "valheimjs-network-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "plugins", "probe");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "manifest.ini"), "name=probe\napiVersion=1\n");
            File.WriteAllText(Path.Combine(directory, "shared.js"), """
                (function () {
                    var ctx = globalThis.__vjs_context;
                    var net = ctx.host.api.net, sync = ctx.host.api.sync;
                    globalThis.__vjs_plugin = { __vjsPlugin:'object', value: {
                        setup: function () {
                            net.on('probe','event',false,function (data,meta) {
                                if (globalThis.__vjs_context !== ctx) throw Error('incoming callback lost context');
                                ctx.state.notice = data.value; ctx.state.sender = meta.sender;
                            });
                            net.on('probe','double',true,function (data) { return data * 2; });
                            net.on('probe','async',true,function () { return Promise.resolve(1); });
                            sync.on('positions',function (entities) {
                                if (globalThis.__vjs_context !== ctx) throw Error('sync callback lost context');
                                ctx.state.frame = entities;
                            });
                        },
                        request: function () {
                            net.request(42,'probe','double','21',1000,function (error,reply) {
                                if (globalThis.__vjs_context !== ctx) throw Error('reply lost context');
                                ctx.state.reply = JSON.parse(reply);
                            });
                        },
                        pending: function () { net.request(42,'probe','double','1',1000,function () { throw Error('late callback'); }); },
                        inspect: function () { return ctx.state; },
                        codec: function () {
                            var frame = sync.encode('[{"id":1,"x":1.5}]');
                            return [sync.decode(frame)[0].x, sync.diff('{"id":1,"x":1}', '{"id":1,"x":1}',0.01)];
                        }
                    }};
                })();
                """);
            var transport = new FakeTransport();
            using var network = new Network(transport);
            var log = new ProbeLog();
            var host = new JsHost(root, ProcessSide.Client, log, bindingFilter: type => type == typeof(NetBindings) || type == typeof(SyncBindings), networking: network);
            try
            {
                host.Load();
                Assert(!host.HasErrors, string.Join(" | ", host.Errors));
                transport.Deliver("probe", "event", Encoding.UTF8.GetBytes("{\"value\":7}"), false);
                using (var sender = network.Sync.OpenScope()) sender.Send(42, "positions", new[] { new SyncEntity { Id = 1, X = 1.5f } });
                host.DispatchTo("probe", "request", "{}"); network.Pump();
                var state = host.DispatchTo("probe", "inspect", "{}")!;
                Assert(state.Contains("\"notice\":7") && state.Contains("\"sender\":42") && state.Contains("\"reply\":42") && state.Contains("\"frame\""), "real binding callbacks/metadata/context failed: " + state);
                Assert(host.DispatchTo("probe", "codec", "{}") == "[1.5,null]", "real sync codec binding failed");
                Throws(() => transport.Deliver("probe", "async", Encoding.UTF8.GetBytes("{}"), true), "async responder accepted");
                transport.DelayRequests = true;
                host.DispatchTo("probe", "pending", "{}");
                host.Reload(); network.Pump();
                Assert(!host.HasErrors && transport.Registrations == 4, "reload did not release native registrations");
                Assert(!log.Errors.Any(error => error.Contains("late callback")), "callback ran after owner unload");
            }
            finally { host.Dispose(); }
            Assert(transport.Registrations == 0, "host disposal leaked native registrations");
        }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("valheimjs-network-", StringComparison.Ordinal)) Directory.Delete(root, true);
        }
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws(Action action, string message) { try { action(); } catch { return; } throw new Exception("Expected failure: " + message); }
    private sealed class ProbeLog : IJsLog
    {
        internal List<string> Errors = new();
        public void Write(string level, string plugin, string message) { if (level == "error") Errors.Add(message); }
    }
    private sealed class FakeTransport : IRpcTransport
    {
        private readonly Dictionary<(string, string), List<(bool Responds, Func<NetworkMessage, byte[], byte[]> Handler)>> _handlers = new();
        internal bool DelayRequests;
        internal CancellationToken LastCancellation;
        internal byte[]? LastPayload;
        internal int Registrations => _handlers.Values.Sum(list => list.Count);
        public bool Available => true;
        public long Everybody => 0;
        public long ServerPeer => 42;
        public IDisposable Subscribe(string channel, string eventName, bool responds, Func<NetworkMessage, byte[], byte[]> handler)
        {
            var key = (channel, eventName);
            if (!_handlers.TryGetValue(key, out var list)) _handlers[key] = list = new();
            if (responds && list.Any(item => item.Responds)) throw new InvalidOperationException("Duplicate responder");
            var entry = (responds, handler); list.Add(entry);
            return new Registration(() => list.Remove(entry));
        }
        public void Notify(long peer, string channel, string eventName, byte[] payload) { LastPayload = payload; Deliver(channel, eventName, payload, false); }
        internal byte[] Deliver(string channel, string eventName, byte[] payload, bool response)
        {
            if (_handlers.TryGetValue((channel, eventName), out var list))
                foreach (var item in list.ToArray())
                    if (item.Responds == response)
                    {
                        var result = item.Handler(new NetworkMessage(42, channel, eventName), payload);
                        if (response) return result;
                    }
            if (response) throw new InvalidOperationException("No responder");
            return Array.Empty<byte>();
        }
        public Task<byte[]> Request(long peer, string channel, string eventName, byte[] payload, TimeSpan timeout, CancellationToken cancellationToken)
        {
            LastCancellation = cancellationToken;
            if (DelayRequests)
            {
                var pending = new TaskCompletionSource<byte[]>();
                cancellationToken.Register(() => pending.TrySetCanceled());
                return pending.Task;
            }
            try { return Task.FromResult(Deliver(channel, eventName, payload, true)); }
            catch (Exception exception) { return Task.FromException<byte[]>(exception); }
        }
        private sealed class Registration : IDisposable
        {
            private Action? _dispose;
            internal Registration(Action dispose) => _dispose = dispose;
            public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
        }
    }
}
