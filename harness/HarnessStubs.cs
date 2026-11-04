using System;
using System.Collections.Generic;
using Jint;
using ValheimJS.Scripting;
using ValheimJS.Harness.Fixtures;
using ValheimJS.Scripting.Binding;

namespace ValheimJS.Storage
{
    internal static class DocumentStore
    {
        internal static void Configure(string root) { }
    }
}

/// <summary>Representative binder used by the harness probe: renames, scoping, JSON, callbacks, errors.</summary>
[JsBinder("probe", Phase = JsBindPhase.Engine, Description = "Harness probe surface.")]
internal sealed class ProbeBindings : JsBindings
{
    internal static int Disposals;

    public ProbeBindings(JsBindingContext context) : base(context)
    {
    }

    [JsBinding(Description = "Adds two numbers.")]
    public int Add(int left, int right) => left + right;

    [JsBinding("who", Scoped = true, Description = "Returns the calling plugin.")]
    public string Owner(string plugin) => plugin;

    public int Hidden() => 42;

    [JsBinding]
    public int Twice(JsCallback callback) => (int)callback.Invoke(21).AsNumber() * 2;

    [JsBinding(ReturnKind = JsReturnKind.Json, Returns = "ProbeShape")]
    public string Shape() => "{\"ok\":true}";

    [JsBinding]
    public string CamelName() => "camel";

    [JsBinding]
    public void Fail() => throw new InvalidOperationException("boom");

    public override void Dispose() => Disposals++;
}

[JsBinder("probeServer", Phase = JsBindPhase.Engine, Order = 1, Availability = JsAvailability.Dedicated)]
internal sealed class ProbeServerBindings : JsBindings
{
    internal static int Constructions;
    public ProbeServerBindings(JsBindingContext context) : base(context)
    {
        Constructions++;
    }

    [JsBinding]
    public string ServerOnly() => "server";

    [JsBinding(Availability = JsAvailability.Client)]
    public string ClientOnly() => "client";
}

namespace ValheimJS.Harness.Fixtures
{
    [JsBinder("notBindings")]
    internal sealed class NotBindings
    {
        [JsBinding] public void Work() { }
    }

    [JsBinder("staticMethod")]
    internal sealed class StaticMethodBindings : JsBindings
    {
        public StaticMethodBindings(JsBindingContext context) : base(context) { }
        [JsBinding] public static void Work() { }
    }

    [JsBinder("badReturn")]
    internal sealed class BadReturnBindings : JsBindings
    {
        public BadReturnBindings(JsBindingContext context) : base(context) { }
        [JsBinding] public object Work() => new();
    }

    [JsBinder("badJson")]
    internal sealed class BadJsonBindings : JsBindings
    {
        public BadJsonBindings(JsBindingContext context) : base(context) { }
        [JsBinding(ReturnKind = JsReturnKind.Json)] public int Work() => 1;
    }

    [JsBinder("noMethods")]
    internal sealed class NoMethodsBindings : JsBindings
    {
        public NoMethodsBindings(JsBindingContext context) : base(context) { }
        public void Work() { }
    }

    [JsBinder("duplicateName")]
    internal sealed class DuplicateNameBindings : JsBindings
    {
        public DuplicateNameBindings(JsBindingContext context) : base(context) { }
        [JsBinding("same")] public void First() { }
        [JsBinding("same")] public void Second() { }
    }
}

namespace ValheimJS.Harness.Fixtures.Duplicate
{
    [JsBinder("dup")]
    internal sealed class FirstBindings : JsBindings
    {
        public FirstBindings(JsBindingContext context) : base(context) { }
        [JsBinding] public void Work() { }
    }

    [JsBinder("dup")]
    internal sealed class SecondBindings : JsBindings
    {
        public SecondBindings(JsBindingContext context) : base(context) { }
        [JsBinding] public void Work() { }
    }
}

internal static class BindingProbe
{
    internal static bool IsFixture(Type type) =>
        type.Namespace != null && type.Namespace.StartsWith("ValheimJS.Harness.Fixtures", StringComparison.Ordinal);

    public static int Run()
    {
        try
        {
            CheckDiscovery();
            CheckDeclarations();
            CheckInvocation();
            Console.WriteLine("PASS: binding discovery, declarations, invocation, scoping, callbacks and disposal");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception.Message);
            return 1;
        }
    }

    private static void CheckDiscovery()
    {
        var catalog = JsBindingDiscovery.Discover(typeof(JsHost).Assembly, type => !IsFixture(type));
        Assert(catalog.IsValid, "catalog errors: " + catalog.DescribeErrors());
        Assert(catalog.Binders.Count == 3, $"expected 3 probe binders, found {catalog.Binders.Count}");

        var probe = catalog.Binders.Find(binder => binder.Namespace == "probe")
            ?? throw new Exception("probe binder missing");
        Assert(probe.Methods.Exists(method => method.Name == "who" && method.SourceName == "Owner"), "renamed method missing");
        Assert(probe.Methods.Exists(method => method.Name == "camelName"), "camel-case default missing");
        Assert(!probe.Methods.Exists(method => method.Name == "hidden"), "unattributed method leaked");
        Assert(probe.Methods.Find(method => method.Name == "who")!.Scoped, "scoped flag lost");
        Assert(probe.Methods.Find(method => method.Name == "twice")!.Parameters[0].Type.Kind == JsTypeKind.Callback, "callback type lost");

        ExpectError<NotBindings>("must derive from JsBindings");
        ExpectError<StaticMethodBindings>("instance methods");
        ExpectError<BadReturnBindings>("no JavaScript mapping");
        ExpectError<BadJsonBindings>("requires a string");
        ExpectError<NoMethodsBindings>("declares no [JsBinding] methods");
        ExpectError<DuplicateNameBindings>("already declared");

        var duplicates = JsBindingDiscovery.Discover(
            typeof(JsHost).Assembly,
            type => type.Namespace == "ValheimJS.Harness.Fixtures.Duplicate");
        Assert(!duplicates.IsValid && duplicates.DescribeErrors().Contains("already claimed"), "duplicate namespace accepted");
    }

    private static void CheckDeclarations()
    {
        var catalog = JsBindingDiscovery.Discover(typeof(JsHost).Assembly, type => !IsFixture(type));
        var text = JsBindingDeclarations.Write(catalog);

        Assert(text.Contains("export interface ProbeApi"), "probe declaration missing");
        Assert(text.Contains("who(): string;"), "renamed method not declared");
        Assert(text.Contains("twice(callback: (...args: unknown[]) => unknown): number;"), "callback signature wrong");
        Assert(text.Contains("@availability dedicated server only"), "availability not documented");
        Assert(text.Contains("import type { ProbeShape } from \"./types\";"), "declared return type not imported");
        Assert(!text.Contains("Hidden"), "unattributed method declared");
    }

    private static void CheckInvocation()
    {
        ProbeBindings.Disposals = 0;
        var context = new JsBindingContext(
            ProcessSide.Server, "data", "config", "plugins", "logs",
            name => "data/" + name, name => "config/" + name, (_, _) => { });

        var catalog = JsBindingDiscovery.Discover(typeof(JsHost).Assembly, type => !IsFixture(type));

        using (var installer = new JsBindingInstaller(catalog, context))
        using (var engine = new Engine())
        {
            var api = installer.BuildApi(engine, "owner");
            engine.SetValue("__api", api);
            var json = engine.Evaluate(
                "JSON.stringify([" +
                "__api.probe.add(2,3)," +
                "__api.probe.who()," +
                "typeof __api.probe.hidden," +
                "__api.probe.twice(function (value) { return value + 1; })," +
                "__api.probe.shape().ok," +
                "__api.probe.camelName()," +
                "typeof __api.probeServer.clientOnly," +
                "__api.probeServer.serverOnly()" +
                "])").ToString();

            Assert(json == "[5,\"owner\",\"undefined\",44,true,\"camel\",\"undefined\",\"server\"]",
                "unexpected invocation result: " + json);

            var rejected = false;
            try
            {
                engine.Evaluate("__api.probe.fail()");
            }
            catch (Exception exception)
            {
                rejected = exception.ToString().Contains("probe.fail");
            }

            Assert(rejected, "binding failure was not attributed to the method");
        }

        Assert(ProbeBindings.Disposals == 1, "binder disposal did not run exactly once");
    }

    private static void ExpectError<T>(string fragment) where T : class
    {
        JsBindingDiscovery.DiscoverBinder(typeof(T), out var errors);
        Assert(errors.Count > 0 && string.Join("\n", errors).Contains(fragment),
            $"{typeof(T).Name} error did not mention '{fragment}': {string.Join(" | ", errors)}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
}

internal static class ListExtensions
{
    public static T? Find<T>(this System.Collections.Generic.IReadOnlyList<T> list, Func<T, bool> predicate)
        where T : class
    {
        foreach (var item in list)
        {
            if (predicate(item))
            {
                return item;
            }
        }

        return null;
    }

    public static bool Exists<T>(this System.Collections.Generic.IReadOnlyList<T> list, Func<T, bool> predicate)
        where T : class => Find(list, predicate) != null;
}
