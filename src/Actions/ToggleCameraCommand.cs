namespace Loupedeck.MeldMxKeypadPlugin;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// Use the same native multistate image path as Toggle Microphone. Options+
// renders list assignments using a saved alias; resolve it to the picker value
// before reading Meld, and publish state under both forms of the selection.
public sealed class ToggleCameraCommand : PluginMultistateDynamicCommand
{
    private const String Automatic = "automatic";
    private readonly Object _stateLock = new();
    private readonly HashSet<String> _selections = new(StringComparer.Ordinal);
    private readonly CameraProfileSelections _profiles = new();
    private String _pickerSignature;
    private CancellationTokenSource _pollCancellation = new();

    public ToggleCameraCommand() : base("Toggle Camera",
        "Show or hide the chosen camera in the scene currently shown in Meld", "Scenes")
    {
        this.Name = typeof(ToggleCameraCommand).FullName;
        this.MakeProfileAction("list;Camera / capture layer");
        this.AddState("unknown", "Camera unavailable", "Camera state is unavailable");
        this.AddState("inactive", "Camera hidden", "The selected camera layer is hidden");
        this.AddState("active", "Camera visible", "The selected camera layer is visible");
    }

    protected override PluginActionParameter[] GetParameters()
    {
        MeldClient.Instance.Start();
        var (selected, layers) = CurrentSceneLayers(MeldClient.Instance.Items());
        if (selected == null || layers.Length == 0)
        {
            var status = !MeldClient.Instance.IsConnected ? "Waiting for Meld — enable WebSocket Server" :
                selected == null ? "No scene is currently shown in Meld" : $"No camera/capture layers in {selected.Name}";
            return new[] { new PluginActionParameter(this, "unavailable", status,
                "Show a scene containing a camera or capture layer in Meld", "Scenes") };
        }
        var choices = new List<PluginActionParameter>
        {
            new(this, Automatic, "Automatic — Camera / Webcam", "Uses exactly one capture layer with that name", "Scenes")
        };
        choices.AddRange(layers.OrderBy(x => x.Index).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(layer => new PluginActionParameter(this, MeldSelection.Key(layer), layer.Name,
                $"{selected.Name} · layer {layer.Index + 1}", "Scenes")));
        return choices.ToArray();
    }

    private static (MeldItem Scene, MeldItem[] Layers) CurrentSceneLayers(IReadOnlyList<MeldItem> items)
    {
        var matches = items.Where(x => x.Type == "scene" && x.Current == true).ToArray();
        var scene = matches.Length == 1 ? matches[0] : null;
        if (scene == null) return (null, Array.Empty<MeldItem>());
        return (scene, items.Where(x => x.Type == "layer" && !x.HasNonCaptureSource &&
            MeldSelection.BelongsToScene(x, scene, items)).ToArray());
    }

    private (MeldItem Scene, MeldItem Camera) Resolve(String layerKey)
    {
        layerKey = _profiles.Resolve(layerKey);
        if (String.IsNullOrWhiteSpace(layerKey)) return (null, null);
        var (scene, layers) = CurrentSceneLayers(MeldClient.Instance.Items());
        if (scene == null) return (null, null);
        if (!MeldSelection.SameName(layerKey, Automatic)) return (scene, MeldSelection.Resolve(layers, layerKey));
        var matches = layers.Where(x => MeldSelection.SameName(x.Name, "Camera") ||
            MeldSelection.SameName(x.Name, "Webcam")).ToArray();
        return (scene, matches.Length == 1 ? matches[0] : null);
    }

    private Int32 SynchronizeState(String selection)
    {
        if (selection == null) return 0;
        lock (_stateLock)
        {
            _selections.Add(selection);
            var state = Resolve(selection).Camera?.Visible switch { true => 2, false => 1, _ => 0 };
            this.SetCurrentState(selection, state);
            return state;
        }
    }

    protected override Boolean OnLoad()
    {
        if (_pollCancellation.IsCancellationRequested)
        {
            _pollCancellation.Dispose();
            _pollCancellation = new CancellationTokenSource();
        }
        MeldClient.Instance.Changed += Refresh;
        _profiles.Changed += Refresh;
        _profiles.Start();
        Refresh();
        return true;
    }

    protected override Boolean OnUnload()
    {
        _pollCancellation.Cancel();
        MeldClient.Instance.Changed -= Refresh;
        _profiles.Changed -= Refresh;
        _profiles.Stop();
        return true;
    }

    private void Refresh()
    {
        var items = MeldClient.Instance.Items();
        String[] selections;
        lock (_stateLock)
        {
            foreach (var key in CurrentSceneLayers(items).Layers.Select(MeldSelection.Key).Prepend(Automatic))
                _selections.Add(key);
            foreach (var alias in _profiles.Aliases) _selections.Add(alias);
            selections = _selections.ToArray();
        }
        foreach (var selection in selections) SynchronizeState(selection);
        this.ActionImageChanged();
        var signature = JsonSerializer.Serialize(new
        {
            Connected = MeldClient.Instance.IsConnected,
            Items = items.Where(x => x.Type is "scene" or "layer" or "group")
                .Select(x => new { x.Id, x.Name, x.Type, x.Parent, x.Index, x.Current, x.HasNonCaptureSource })
        });
        if (_pickerSignature == signature) return;
        _pickerSignature = signature;
        this.ParametersChanged();
    }

    protected override String GetCommandDisplayName(String parameter, PluginImageSize size)
    {
        SynchronizeState(parameter);
        return String.Empty;
    }

    protected override String GetCommandDisplayName(String parameter, Int32 state, PluginImageSize size) => String.Empty;

    protected override BitmapImage GetCommandImage(String parameter, PluginImageSize size) =>
        Icon(SynchronizeState(parameter));

    protected override BitmapImage GetCommandImage(String parameter, Int32 state, PluginImageSize size)
    {
        SynchronizeState(parameter);
        return Icon(state);
    }

    private static BitmapImage Icon(Int32 state) => MeldIcons.Read(state switch
    {
        2 => "camera-visible",
        1 => "camera-hidden",
        _ => "camera"
    });

    protected override void RunCommand(String parameter)
    {
        var (scene, camera) = Resolve(parameter);
        if (camera == null) return;
        SynchronizeState(parameter);
        _ = ToggleSafelyAsync(scene.Id, camera.Id, camera.Visible, _pollCancellation.Token);
    }

    private static async Task ToggleSafelyAsync(String sceneId, String cameraId, Boolean? previousVisibility,
        CancellationToken cancellation)
    {
        using var polling = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        try
        {
            await MeldClient.Instance.InvokeAsync("toggleLayer", sceneId, cameraId);
            polling.CancelAfter(TimeSpan.FromMilliseconds(1500));
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(100, polling.Token);
                await MeldClient.Instance.RefreshSessionAsync(polling.Token);
                var visible = MeldClient.Instance.Items().FirstOrDefault(x =>
                    String.Equals(x.Id, cameraId, StringComparison.OrdinalIgnoreCase))?.Visible;
                if (visible.HasValue && visible != previousVisibility) return;
            }
        }
        catch (OperationCanceledException) when (polling.IsCancellationRequested) { }
        catch (Exception error) { PluginLog.Warning($"Toggle Camera: {error.Message}"); }
    }
}
