using System;
using System.Threading;
using Jint;
using Jint.Native;
using ValheimJS.Scripting.Binding;
using ValheimJS.Scripting.Types;

/// <summary>Verifies the native facade layer: classes, payloads and cadence.</summary>
internal static class TypeProbe
{
    internal static int Run()
    {
        try
        {
            Payload();
            Classes();
            PluginScopes();
            TickerContract();
            Console.WriteLine("PASS: native classes, payloads and ticker");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: type probe: " + exception);
            return 1;
        }
    }

    private static void Payload()
    {
        using var engine = new Engine();
        var payload = JsPayload.Object(engine)
            .Set("id", "x")
            .Set("value", 3)
            .Set("ready", true)
            .Set("missing", (string?)null)
            .Set("nearby", JsPayload.Array(engine, new[]
            {
                JsPayload.Object(engine).Set("distance", 2.5).Build()
            }))
            .Build();

        engine.SetValue("payload", payload);
        Assert(engine.Evaluate("payload.id").AsString() == "x", "payload string");
        Assert(engine.Evaluate("payload.value").AsNumber() == 3, "payload number");
        Assert(engine.Evaluate("payload.ready").AsBoolean(), "payload boolean");
        Assert(engine.Evaluate("payload.missing").IsNull(), "payload null");
        Assert(engine.Evaluate("payload.nearby.length").AsNumber() == 1, "payload array length");
        Assert(engine.Evaluate("payload.nearby[0].distance").AsNumber() == 2.5, "payload nested value");
        Assert(engine.Evaluate("typeof payload.id").AsString() == "string", "payload is a plain JS object");
    }

    private static void Classes()
    {
        using var engine = new Engine();
        SetPlugin(engine, "alpha");
        var registry = new JsTypeRegistry(engine);

        var built = registry.Define<BuiltThing>("ValheimBuilt")
            .Construct(arguments => new BuiltThing(arguments.Length > 0 ? arguments[0].ToString() : "built"))
            .Getter("id", target => ((BuiltThing)target).Id)
            .Getter("value", target => ((BuiltThing)target).Value)
            .Method("bump", (target, arguments) =>
            {
                var thing = (BuiltThing)target;
                thing.Value += (int)arguments[0].AsNumber();
                return JsValue.FromObject(engine, thing.Value);
            })
            .Build();

        engine.SetValue("ValheimBuilt", built.Constructor);

        var attributed = registry.Register<AttributedThing>();
        engine.SetValue("attributed", registry.Instance(attributed, new AttributedThing { Id = "a:1", Value = 4 }));

        Assert(True(engine, "Object.getPrototypeOf(new ValheimBuilt('d:1')) === ValheimBuilt.prototype"), "instance prototype");
        Assert(True(engine, "(new ValheimBuilt('b:1')).id === 'b:1'"), "constructor handle");
        Assert(True(engine, "(new ValheimBuilt('b:1')).value === 0"), "constructor initial value");
        Assert(True(engine, "(function () { var x = new ValheimBuilt('b:2'); x.bump(3); return x.value; })() === 3"), "method invocation");
        Assert(True(engine, "attributed.id === 'a:1'"), "attributed getter");
        Assert(True(engine, "(function () { attributed.bump(2); return attributed.value; })() === 6"), "attributed method");
        Assert(True(engine, "typeof attributed.secret === 'undefined'"), "[JsIgnore] leaked a member");
        Assert(True(engine, "(function () { class Sub extends ValheimBuilt {}; var s = new Sub('s:1'); return s instanceof ValheimBuilt; })()"), "subclass instance prototype");
        Assert(True(engine, "(function () { class Sub extends ValheimBuilt {}; var s = new Sub('s:1'); return s.id === 's:1'; })()"), "subclass super() construction");
        Assert(True(engine, "(new ValheimBuilt('i:1')) instanceof ValheimBuilt"), "instanceof");
        Assert(registry.Find("ValheimBuilt") != null && registry.Find("ValheimAttributedThing") != null, "registered names");

        var type = registry.Find("ValheimBuilt")!;
        var handle = Thrown();
        var first = registry.Instance(type, handle);
        Assert(ReferenceEquals(first, registry.Instance(type, handle)), "instance identity is stable per handle");
    }

    private static void PluginScopes()
    {
        using var engine = new Engine();
        var registry = new JsTypeRegistry(engine);
        var type = registry.Define<BuiltThing>("ScopedThing")
            .Getter("id", target => ((BuiltThing)target).Id)
            .Build();

        var handle = new BuiltThing("scoped:1");
        SetPlugin(engine, "alpha");
        var alpha = registry.Instance(type, handle);
        engine.SetValue("thing", alpha);
        Assert(engine.Evaluate("thing.id").AsString() == "scoped:1", "scoped read");
        Assert(registry.Handle(alpha) == handle, "native handle recovery from JS value");

        SetPlugin(engine, "beta");
        var beta = registry.Instance(type, handle);
        Assert(!ReferenceEquals(alpha, beta), "plugins get their own instance objects");

        SetPlugin(engine, "alpha");
        registry.Release("alpha");
        Assert(!ReferenceEquals(alpha, registry.Instance(type, handle)), "release dropped the cached instance");
    }

    private static void TickerContract()
    {
        var ticker = new Ticker(5);
        Assert(!ticker.Due(), "first tick only starts the clock");
        Thread.Sleep(12);
        Assert(ticker.Due(), "interval elapsed");
        Assert(!ticker.Due(), "at most once per window");
        Assert(ticker.Sample() >= 0, "sample returns elapsed milliseconds");
    }

    private static object Thrown() => new BuiltThing("identity");

    private static bool True(Engine engine, string code) => engine.Evaluate(code).AsBoolean();

    private static void SetPlugin(Engine engine, string name)
    {
        var context = new JsObject(engine) { Prototype = engine.Intrinsics.Object.PrototypeObject };
        context.FastSetProperty("name", new Jint.Runtime.Descriptors.PropertyDescriptor(JsValue.FromObject(engine, name), Jint.Runtime.Descriptors.PropertyFlag.Configurable));
        engine.SetValue("__vjs_context", context);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

[JsType("ValheimAttributedThing")]
internal sealed class AttributedThing
{
    public string Id { get; set; } = "";

    public int Value { get; set; }

    public int Bump(int by) => Value += by;

    [JsIgnore]
    public string Secret { get; set; } = "hidden";
}

internal sealed class BuiltThing
{
    internal BuiltThing(string id)
    {
        Id = id;
    }

    internal string Id { get; }

    internal int Value { get; set; }
}
