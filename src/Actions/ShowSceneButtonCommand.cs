namespace Loupedeck.MeldStudioControlsPlugin;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

// The native list/multistate path retains a saved button alias on device image
// requests. The Action Editor renderer discards its scene parameter dictionary.
public sealed class ShowSceneButtonCommand : PluginMultistateDynamicCommand
{
    private readonly Object _stateLock = new();
    private readonly HashSet<String> _selections = new(StringComparer.Ordinal);
    private readonly SceneProfileSelections _profiles = new();
    private String _pickerSignature;

    public ShowSceneButtonCommand() : base("Show Scene", "Choose and show a scene from your Meld session", "Scenes")
    {
        this.Name = typeof(ShowSceneButtonCommand).FullName;
        this.MakeProfileAction("list;Meld scene");
        this.AddState("unknown", "Scene unavailable", "Meld has not reported the selected scene");
        this.AddState("inactive", "Scene not shown", "The selected scene is not currently shown");
        this.AddState("active", "Scene shown", "The selected scene is currently shown");
    }

    protected override PluginActionParameter[] GetParameters()
    {
        MeldClient.Instance.Start();
        var scenes = MeldClient.Instance.Scenes();
        if (scenes.Count == 0)
            return new[] { new PluginActionParameter(this, "meld-unavailable",
                MeldClient.Instance.IsConnected ? "No scenes in the current Meld session"
                    : "Waiting for Meld Studio — enable WebSocket Server",
                "Open a Meld session and reopen this list", "Scenes") };
        return scenes.Select(scene => new PluginActionParameter(this, MeldSelection.Key(scene),
            scene.Name, $"Scene {scene.Index + 1}", "Scenes")).ToArray();
    }

    private MeldItem Resolve(String selection) =>
        MeldSelection.Resolve(MeldClient.Instance.Scenes(), _profiles.Resolve(selection));

    private Int32 SynchronizeState(String selection)
    {
        if (selection == null) return 0;
        lock (_stateLock)
        {
            _selections.Add(selection);
            var state = Resolve(selection)?.Current switch { true => 2, false => 1, _ => 0 };
            this.SetCurrentState(selection, state);
            return state;
        }
    }

    protected override Boolean OnLoad()
    {
        MeldClient.Instance.Changed += Refresh;
        _profiles.Changed += Refresh;
        _profiles.Start();
        Refresh();
        return true;
    }

    protected override Boolean OnUnload()
    {
        MeldClient.Instance.Changed -= Refresh;
        _profiles.Changed -= Refresh;
        _profiles.Stop();
        return true;
    }

    private void Refresh()
    {
        var scenes = MeldClient.Instance.Scenes();
        String[] selections;
        lock (_stateLock)
        {
            foreach (var key in scenes.Select(MeldSelection.Key)) _selections.Add(key);
            foreach (var alias in _profiles.Aliases) _selections.Add(alias);
            selections = _selections.ToArray();
        }
        foreach (var selection in selections) SynchronizeState(selection);
        this.ActionImageChanged();
        var signature = JsonSerializer.Serialize(new
        {
            Connected = MeldClient.Instance.IsConnected,
            Items = scenes.Select(x => new { x.Id, x.Name, x.Index })
        });
        if (_pickerSignature == signature) return;
        _pickerSignature = signature;
        this.ParametersChanged();
    }

    // The image contains exactly one title. Suppress the host's extra caption.
    protected override String GetCommandDisplayName(String parameter, PluginImageSize size)
    {
        SynchronizeState(parameter);
        return String.Empty;
    }

    protected override String GetCommandDisplayName(String parameter, Int32 state, PluginImageSize size) => String.Empty;

    protected override BitmapImage GetCommandImage(String parameter, PluginImageSize size) =>
        Render(parameter, SynchronizeState(parameter));

    protected override BitmapImage GetCommandImage(String parameter, Int32 state, PluginImageSize size)
    {
        SynchronizeState(parameter);
        return Render(parameter, state);
    }

    private BitmapImage Render(String parameter, Int32 state)
    {
        var selection = _profiles.Resolve(parameter);
        var selected = MeldSelection.Resolve(MeldClient.Instance.Scenes(), selection);
        var label = selected?.Name ?? (MeldSelection.TryRead(selection, out var savedName, out _) ? savedName : "Show Scene");
        using var bitmap = new BitmapBuilder(128, 128);
        bitmap.Clear(state == 2 ? 0xffc4e5f5u : 0xff111823u);
        using var glyph = MeldIcons.Read("scene");
        bitmap.DrawImage(glyph, 31, 10, 66, 66);
        var fontSize = label.Length <= 14 ? 20 : label.Length <= 28 ? 16 : 14;
        using var caption = new BitmapBuilder(116, 50);
        caption.Clear(0x00000000u);
        caption.DrawText(label, state == 2 ? new BitmapColor(0xff17354au) : BitmapColor.White,
            fontSize, fontSize - 2, 3);
        using var captionImage = caption.ToImage();
        bitmap.DrawImage(captionImage, 6, 68);
        return bitmap.ToImage();
    }

    protected override void RunCommand(String parameter)
    {
        var selected = Resolve(parameter);
        if (selected != null) _ = ShowSafelyAsync(selected.Id);
    }

    private static async Task ShowSafelyAsync(String sceneId)
    {
        try { await MeldClient.Instance.InvokeAsync("showScene", sceneId); }
        catch (Exception error) { PluginLog.Warning($"Show Scene: {error.Message}"); }
    }
}
