using UnityEngine;
using ValheimJS.Scripting.Binding;
using ValheimJS.World;
using ValheimJS.World.Navigation;

namespace ValheimJS.Bindings;

/// <summary>Admin/testing access to the navigable volume and its in-world debug overlay.</summary>
[JsBinder("nav", Phase = JsBindPhase.Gameplay, Order = 143,
    Description = "Inspect the navigation volume and toggle its debug overlay.")]
internal sealed class NavBindings : JsBindings
{
    private GameObject? _debug;

    public NavBindings(JsBindingContext context) : base(context)
    {
    }

    [JsBinding(Scoped = true, Description = "Toggles the navigation debug overlay.")]
    public void Enable(string plugin, bool enabled)
    {
        NavDebugRenderer.Enabled = enabled;

        if (enabled)
        {
            EnsureDebug();
        }
        else if (_debug != null)
        {
            _debug.SetActive(false);
        }
    }

    [JsBinding(Scoped = true, Description = "Number of navigation zones loaded or cached.")]
    public int Zones(string plugin) => WorldManager.Instance?.NavVolume?.ZoneCount ?? 0;

    [JsBinding(Scoped = true, Description = "Number of navigation cells loaded.")]
    public int Cells(string plugin) => WorldManager.Instance?.NavVolume?.CellCount ?? 0;

    public override void Dispose()
    {
        NavDebugRenderer.Enabled = false;
        if (_debug != null)
        {
            Object.Destroy(_debug);
            _debug = null;
        }
    }

    private void EnsureDebug()
    {
        if (_debug == null)
        {
            _debug = new GameObject("ValheimJS_NavDebug");
            Object.DontDestroyOnLoad(_debug);
            _debug.AddComponent<NavDebugRenderer>();
        }

        _debug.SetActive(true);
    }
}
