namespace Loupedeck.MeldMxKeypadPlugin;

using System;
using System.Linq;
using System.Threading.Tasks;

internal static class MeldIcons
{
    // Options+ also requests source artwork with PluginImageSize.None (0x0).
    // Rasterizing to the requested size produced null images on that path.
    // Return the encoded source; the host scales it into the icon template.
    public static BitmapImage Read(String icon) => PluginResources.ReadImage(icon + ".png");
}

public abstract class MeldCommand : PluginDynamicCommand
{
    private readonly String _icon;
    protected MeldCommand(String display, String description, String group, String icon)
        : base(displayName: display, description: description, groupName: group)
    {
        _icon = icon;
    }

    protected virtual String IconName() => _icon;
    // The package icon template controls caption visibility in Options+.
    protected override String GetCommandDisplayName(String parameter, PluginImageSize size) => String.Empty;
    protected override BitmapImage GetCommandImage(String parameter, PluginImageSize size) =>
        MeldIcons.Read(IconName());

    protected override void RunCommand(String parameter)
    {
        _ = ExecuteSafelyAsync();
    }

    private async Task ExecuteSafelyAsync()
    {
        try { await ExecuteAsync(); }
        catch (Exception error) { PluginLog.Warning($"{this.DisplayName}: {error.Message}"); }
    }

    protected abstract Task ExecuteAsync();
    private protected static MeldClient Meld => MeldClient.Instance;
}

public abstract class MeldToggleCommand : PluginMultistateDynamicCommand
{
    private readonly String _neutralIcon;
    private readonly String _inactiveIcon;
    private readonly String _activeIcon;

    protected MeldToggleCommand(String display, String description, String group,
        String neutralIcon, String inactiveIcon, String activeIcon,
        String unknownLabel, String inactiveLabel, String activeLabel)
        : base(display, description, group)
    {
        _neutralIcon = neutralIcon;
        _inactiveIcon = inactiveIcon;
        _activeIcon = activeIcon;
        this.AddState("unknown", unknownLabel, "Meld has not reported the current state");
        this.AddState("inactive", inactiveLabel, inactiveLabel);
        this.AddState("active", activeLabel, activeLabel);
    }

    protected override Boolean OnLoad()
    {
        Meld.Changed += SynchronizeState;
        SynchronizeState();
        return true;
    }

    protected override Boolean OnUnload()
    {
        Meld.Changed -= SynchronizeState;
        return true;
    }

    private void SynchronizeState()
    {
        var state = GetActiveState() switch { false => 1, true => 2, _ => 0 };
        if (!this.TryGetCurrentStateIndex(out var previous) || previous != state)
            this.SetCurrentState(state);
        this.ActionImageChanged();
    }

    protected override String GetCommandDisplayName(String parameter, Int32 state, PluginImageSize size) =>
        this.States[state].DisplayName;

    protected override BitmapImage GetCommandImage(String parameter, Int32 state, PluginImageSize size) =>
        MeldIcons.Read(state switch { 1 => _inactiveIcon, 2 => _activeIcon, _ => _neutralIcon });

    protected override void RunCommand(String parameter) => _ = ExecuteSafelyAsync();

    private async Task ExecuteSafelyAsync()
    {
        try
        {
            await ExecuteAsync();
            SynchronizeState();
        }
        catch (Exception error) { PluginLog.Warning($"{this.DisplayName}: {error.Message}"); }
    }

    protected abstract Boolean? GetActiveState();
    protected abstract Task ExecuteAsync();
    private protected static MeldClient Meld => MeldClient.Instance;
}

public sealed class ToggleStreamCommand : MeldToggleCommand
{
    public ToggleStreamCommand() : base("Toggle Stream", "Start or stop streaming", "Streaming",
        "stream", "stream-start", "stream-stop", "Stream", "Start Stream", "Stop Stream") { }
    protected override Boolean? GetActiveState() => Meld.State("isStreaming");
    protected override Task ExecuteAsync() => Meld.InvokeAsync("toggleStream");
}

public sealed class ToggleRecordingCommand : MeldToggleCommand
{
    public ToggleRecordingCommand() : base("Toggle Recording", "Start or stop recording", "Recording",
        "record", "record-start", "record-stop", "Recording", "Start Recording", "Stop Recording") { }
    protected override Boolean? GetActiveState() => Meld.State("isRecording");
    protected override Task ExecuteAsync() => Meld.InvokeAsync("toggleRecord");
}

public sealed class ToggleMicrophoneCommand : MeldToggleCommand
{
    public ToggleMicrophoneCommand() : base("Toggle Microphone", "Mute or unmute the Mic / Microphone audio track", "Audio",
        "mic", "mic-muted", "mic-live", "Microphone", "Unmute Microphone", "Mute Microphone") { }
    private static MeldItem Microphone()
    {
        var items = Meld.Items();
        var matches = items.Where(x => x.Type == "track" &&
            (MeldSelection.SameName(x.Name, "Mic") || MeldSelection.SameName(x.Name, "Microphone"))).ToArray();
        var global = matches.Where(x => String.IsNullOrWhiteSpace(x.Parent)).ToArray();
        if (global.Length > 0) return global.Length == 1 ? global[0] : null;
        var live = items.Where(x => x.Type == "scene" && x.Current == true).ToArray();
        if (live.Length != 1) return null;
        var local = matches.Where(x => MeldSelection.BelongsToScene(x, live[0], items)).ToArray();
        return local.Length == 1 ? local[0] : null;
    }
    protected override Boolean? GetActiveState()
    {
        return Microphone()?.Muted is Boolean muted ? !muted : null;
    }
    protected override Task ExecuteAsync()
    {
        var track = Microphone() ?? throw new InvalidOperationException(
            "Name exactly one global audio track Mic or Microphone, or one in the current live scene. Matching ignores case and surrounding spaces.");
        return Meld.InvokeAsync("toggleMute", track.Id);
    }
}

public sealed class ToggleVirtualCameraCommand : MeldToggleCommand
{
    public ToggleVirtualCameraCommand() : base("Toggle Virtual Camera", "Start or stop the virtual camera", "Streaming",
        "virtual-camera", "virtual-camera-start", "virtual-camera-stop", "Virtual Camera",
        "Start Virtual Camera", "Stop Virtual Camera") { }
    // Do not guess the initial state on Meld versions that don't publish it.
    protected override Boolean? GetActiveState() => Meld.State("isVirtualCameraRunning") ??
        Meld.State("isVirtualCameraOn") ?? Meld.State("isVirtualCameraActive") ?? Meld.State("isVirtualCameraEnabled");
    protected override Task ExecuteAsync() => Meld.InvokeAsync("sendCommand", "meld.toggleVirtualCameraAction");
}

public sealed class SaveClipCommand : MeldCommand
{
    public SaveClipCommand() : base("Save Clip", "Save a replay clip", "Recording", "clip") { }
    protected override Task ExecuteAsync() => Meld.InvokeAsync("sendCommand", "meld.recordClip");
}

public sealed class ScreenshotCommand : MeldCommand
{
    public ScreenshotCommand() : base("Screenshot", "Save a screenshot", "Recording", "screenshot") { }
    protected override Task ExecuteAsync() => Meld.InvokeAsync("sendCommand", "meld.screenshot");
}

public sealed class ShowSceneCommand : ActionEditorCommand
{
    private const String SceneControl = "Scene";
    private static MeldItem ResolveScene(String key) => MeldSelection.Resolve(MeldClient.Instance.Scenes(), key);

    public ShowSceneCommand()
    {
        this.Name = "ShowScene";
        this.DisplayName = "Show Scene";
        this.Description = "Choose a scene from your current Meld session";
        this.GroupName = "Scenes";
        this.ActionEditor.AddControlEx(new ActionEditorListbox(SceneControl, "Meld scene"));
        this.ActionEditor.ListboxItemsRequested += (_, args) =>
        {
            if (!String.Equals(args.ControlName, SceneControl, StringComparison.OrdinalIgnoreCase)) return;
            MeldClient.Instance.Start();
            var scenes = MeldClient.Instance.Scenes();
            if (scenes.Count == 0)
            {
                args.AddItem("meld-unavailable", MeldClient.Instance.IsConnected
                    ? "No scenes in the current Meld session"
                    : "Waiting for Meld Studio — enable WebSocket Server", "Open a Meld session and reopen this list");
                return;
            }
            foreach (var scene in scenes)
                args.AddItem(MeldSelection.Key(scene), scene.Name, $"Scene {scene.Index + 1}");
        };
        this.ActionEditor.ControlValueChanged += (_, args) =>
        {
            if (!String.Equals(args.ControlName, SceneControl, StringComparison.OrdinalIgnoreCase)) return;
            var selected = ResolveScene(args.ActionEditorState.GetControlValue(SceneControl));
            if (selected != null) args.ActionEditorState.SetDisplayName(selected.Name);
        };
        this.ActionEditor.Started += (_, _) =>
        {
            MeldClient.Instance.Start();
            this.ActionEditor.ListboxItemsChanged(SceneControl);
        };
    }

    protected override Boolean OnLoad()
    {
        MeldClient.Instance.Changed += RefreshScenes;
        return true;
    }

    protected override Boolean OnUnload()
    {
        MeldClient.Instance.Changed -= RefreshScenes;
        return true;
    }

    private void RefreshScenes()
    {
        this.ActionEditor.ListboxItemsChanged(SceneControl);
        if (this.Plugin != null) this.ActionImageChanged();
    }

    protected override String GetCommandDisplayName(ActionEditorActionParameters parameters)
    {
        if (parameters != null && parameters.TryGetString(SceneControl, out var key))
            return ResolveScene(key)?.Name ?? (MeldSelection.TryRead(key, out var name, out _) ? name : "Show Scene");
        return "Show Scene";
    }

    protected override BitmapImage GetCommandImage(ActionEditorActionParameters parameters, Int32 width, Int32 height)
    {
        if (parameters == null || !parameters.TryGetString(SceneControl, out var selection) ||
            !MeldSelection.TryRead(selection, out _, out _))
        {
            // Options+ builds its editable icon from this UNCONFIGURED image,
            // then adds the selected scene name in its own caption region.
            // Baking a title/background here nests a whole button above that
            // caption. Return only a padded transparent symbol on this path.
            using var symbol = new BitmapBuilder(128, 128);
            symbol.Clear(0x00000000u);
            using var sceneGlyph = MeldIcons.Read("scene");
            symbol.DrawImage(sceneGlyph, 12, 18, 104, 104);
            return symbol.ToImage();
        }
        var selected = parameters != null && parameters.TryGetString(SceneControl, out var key) ? ResolveScene(key) : null;
        var label = GetCommandDisplayName(parameters);
        // Supply a complete, fixed-resolution image even for unsized requests.
        // Per-action .ict overrides would freeze this runtime background.
        using var bitmap = new BitmapBuilder(128, 128);
        bitmap.Clear(selected?.Current == true ? 0xff493079u : 0xff111823u);
        using var glyph = MeldIcons.Read("scene");
        bitmap.DrawImage(glyph, 31, 10, 66, 66);
        var fontSize = label.Length <= 14 ? 20 : label.Length <= 28 ? 16 : 14;
        // The positioned DrawText overload changed between SDK 6.1 and 6.4.
        // Its full-surface overload remains binary compatible. A separate
        // caption surface also keeps long labels out of the icon's area.
        using var caption = new BitmapBuilder(116, 50);
        caption.Clear(0x00000000u);
        caption.DrawText(label, BitmapColor.White, fontSize, fontSize - 2, 3);
        using var captionImage = caption.ToImage();
        bitmap.DrawImage(captionImage, 6, 68);
        return bitmap.ToImage();
    }

    protected override Boolean RunCommand(ActionEditorActionParameters parameters)
    {
        if (parameters == null || !parameters.TryGetString(SceneControl, out var key)) return false;
        var selected = ResolveScene(key);
        if (selected == null) return false;
        _ = ShowSafelyAsync(selected.Id);
        return true;
    }

    private static async Task ShowSafelyAsync(String sceneId)
    {
        try { await MeldClient.Instance.InvokeAsync("showScene", sceneId); }
        catch (Exception error) { PluginLog.Warning($"Show Scene: {error.Message}"); }
    }
}
