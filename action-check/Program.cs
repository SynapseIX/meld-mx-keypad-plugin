using System.Reflection;
using System.Security.Cryptography;
using Loupedeck;
using Loupedeck.MeldStudioControlsPlugin;
using Loupedeck.Service;
using SkiaSharp;

// Runs on an isolated machine: the mock occupies Meld's local port 13376.
// Uses the actual Logitech image decoder, package resolver and icon renderer.
var root = Path.GetFullPath(args.Length > 0 ? args[0] : "src/package");
var output = Path.GetFullPath(args.Length > 1 ? args[1] : "test-results/rendered");
Directory.CreateDirectory(output);
var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
object Call(object instance, string name, params object[] values) => instance.GetType()
    .GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(instance, values);
async Task Until(Func<bool> condition, string message) {
    for (var i = 0; i < 100; i++) { if (condition()) { checks++; return; } await Task.Delay(50); }
    throw new Exception(message);
}
byte[] ImageBytes(BitmapImage image, string label) {
    Check(image != null, label + " returned no image");
    using (image) {
        var bytes = image.ToArray();
        using var decoded = SKBitmap.Decode(bytes);
        Check(decoded != null && decoded.Width > 0 && decoded.Height > 0, label + " is not a valid bitmap");
        Check(decoded.Pixels.Count(p => Math.Max(p.Red, Math.Max(p.Green, p.Blue)) > 70 && p.Alpha > 50) > 60,
            label + " is blank");
        return bytes;
    }
}

var plugin = new MeldStudioControlsPlugin(); // initializes embedded resource lookup
var packagedAssembly = Path.Combine(root, "bin", "MeldStudioControlsPlugin.dll");
if (File.Exists(packagedAssembly)) Check(Hash(File.ReadAllBytes(packagedAssembly)) ==
    Hash(File.ReadAllBytes(typeof(MeldStudioControlsPlugin).Assembly.Location)), "Package contains a different plugin assembly from the one under test");
var toggles = new PluginMultistateDynamicCommand[] { new ToggleStreamCommand(), new ToggleRecordingCommand(),
    new ToggleMicrophoneCommand(), new ToggleVirtualCameraCommand() };
var singles = new PluginDynamicCommand[] { new SaveClipCommand(), new ScreenshotCommand() };
var scene = new ShowSceneCommand();
var sceneCollection = plugin.ActionEditorCommands;
sceneCollection.AddAction(scene);
var camera = new ToggleCameraCommand();
Call(plugin.DynamicCommands, "AddAction", camera);
Call(plugin, "AddAction", camera, false);
const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
var profileBridge=camera.GetType().GetField("_profiles",members)!;
profileBridge.SetValue(camera,Activator.CreateInstance(profileBridge.FieldType,members,null,
    new object[]{Path.Combine(output,"Applications")},null));
typeof(Plugin).GetProperty("NativeApi")!.SetValue(plugin, DispatchProxy.Create<INativeApi, NoOpNativeApi>());
var callbackType = typeof(Plugin).GetNestedTypes(BindingFlags.NonPublic).Single(t =>
    t.GetInterfaces().Any(i => i.GetMethods().Any(m => m.Name == "ListboxItemsChanged")));
var callbacks = Activator.CreateInstance(callbackType, plugin);
foreach (var editor in new ActionEditorAction[] { scene }) {
    Call(plugin, "AddAction", editor, false);
    typeof(ActionEditorAction).GetMethod("Initialize", members)!.Invoke(editor, new[] { (object)plugin, callbacks });
}

var sizes = Enum.GetValues<PluginImageSize>().Distinct().ToArray();
var package = new PluginPackage();
Call(package, "Init", root);
var captions = new Dictionary<string, string[]> {
    ["ToggleStreamCommand"] = new[]{"Stream", "Start Stream", "Stop Stream"},
    ["ToggleRecordingCommand"] = new[]{"Recording", "Start Recording", "Stop Recording"},
    ["ToggleMicrophoneCommand"] = new[]{"Microphone", "Unmute Microphone", "Mute Microphone"},
    ["ToggleCameraCommand"] = new[]{"Camera", "Show Camera", "Hide Camera"},
    ["ToggleVirtualCameraCommand"] = new[]{"Virtual Camera", "Start Virtual Camera", "Stop Virtual Camera"}
};

byte[] Render(string actionName, string stateName, string caption, byte[] expected, int size, bool showText) {
    var actionString = new ActionString("MeldStudioControls", actionName, null, stateName);
    Check(package.TryGetActionIcon(actionString, out var image), $"Packaged icon missing: {actionString}");
    var bytes = ImageBytes(image, actionString.ToString());
    Check(Hash(bytes) == Hash(expected), "Package and callback disagree: " + actionString);
    // Match the host's precedence: an action-specific template is COMPLETE.
    // Only the plugin default template receives a runtime image. Never inject
    // an image into an action template: that hid the black-button regression.
    var templatePath = new[] {
        CombinedString.GetString(actionName, null, stateName), actionName
    }.Distinct().Select(name => Path.Combine(root, "icontemplates", name + ".ict"))
        .FirstOrDefault(File.Exists);
    var isActionTemplate = templatePath != null;
    templatePath ??= Path.Combine(root, "metadata", "DefaultIconTemplate.ict");
    Check(ActionIcon.TryReadFromFile(templatePath, out var layout), "Template failed to load: " + templatePath);
    if (isActionTemplate) {
        Check(layout.TryGetImageItem(out var embedded) && embedded.Image?.Length > 0,
            "Action template overrides runtime artwork with an empty image: " + templatePath);
        Check(Hash(embedded.Image) == Hash(expected), "Wrong image embedded in action template");
    } else {
        layout.SetImage(bytes);
    }
    layout.SetText(caption);
    Check(layout.Items.OfType<ActionIconTextItem>().Any(x => x.IsVisible) == showText, "Wrong caption visibility");
    var rendered = ImageBytes(ActionIconBuilder.CreateImage(size, size, layout, ActionImageBuilderFlags.None), actionString + " rendered");
    using var bitmap = SKBitmap.Decode(rendered);
    Check(bitmap.Width == size && bitmap.Height == size, "Wrong rendered size");
    if (!showText) {
        layout.SetText("THIS MUST NEVER APPEAR");
        using var hiddenTextImage = ActionIconBuilder.CreateImage(size, size, layout, ActionImageBuilderFlags.None);
        Check(Hash(rendered) == Hash(hiddenTextImage.ToArray()), "Caption leaked onto icon-only action");
    } else {
        Check(bitmap.Pixels.Where((p, i) => i / size < size * .70).Count(p => p.Blue > 80) > 70, "Scene glyph missing");
        Check(bitmap.Pixels.Where((p, i) => i / size > size * .72).Count(p => p.Red > 80) > 20, "Scene label missing");
        Check(bitmap.Pixels.Skip((size - 2) * size).All(p => p.Red < 30 && p.Blue < 30), "Scene label touches bottom edge");
    }
    File.WriteAllBytes(Path.Combine(output, actionName.Split('.').Last() + "-" + (stateName ?? "default") + "-" + size + ".png"), rendered);
    return rendered;
}
foreach (var action in toggles) {
    var stateHashes = new List<string>();
    for (var i = 0; i < action.States.Count; i++) {
        var state = action.States[i];
        Check(action.TryGetCommandDisplayName(null, state.Name, PluginImageSize.None, out var label) && label == captions[action.GetType().Name][i], "Wrong state label");
        byte[] source = null;
        foreach (var size in sizes) {
            Check(action.TryGetCommandImage(null, state.Name, size, out var img), $"Callback failed: {action.GetType().Name}/{state.Name}/{size}");
            var data = ImageBytes(img, $"{action.GetType().Name}/{state.Name}/{size}");
            if (size == PluginImageSize.None) source = data;
        }
        stateHashes.Add(Hash(source));
        foreach (var size in new[]{50, 80, 116}) Render(action.GetType().FullName!, state.Name, label, source, size, false);
    }
    Check(stateHashes[1] != stateHashes[2], "Active and inactive graphics are identical");
    Check(stateHashes.Distinct().Count() == 3, "Unknown status must have a distinct neutral graphic");
}
foreach (var action in singles) {
    foreach (var size in sizes) {
        Check(action.TryGetCommandImage(null, size, out var img), "Single action callback failed at " + size);
        ImageBytes(img, action.GetType().Name);
    }
    action.TryGetCommandImage(null, PluginImageSize.None, out var source);
    var bytes = ImageBytes(source, action.GetType().Name);
    foreach(var size in new[]{50, 80, 116}) Render(action.GetType().FullName!, null, "", bytes, size, false);
}
foreach (var size in sizes) {
    // The collection adapts SDK size requests to the action editor callback.
    Check(sceneCollection.TryGetCommandImage(scene.Name, null, size, out var img), "Scene callback failed at " + size);
    ImageBytes(img, "Show Scene/" + size);
}
sceneCollection.TryGetCommandImage(scene.Name, null, PluginImageSize.None, out var sceneImage);
var sceneBytes = ImageBytes(sceneImage, "Show Scene source");
using (var unconfigured = SKBitmap.Decode(sceneBytes)) {
    Check(unconfigured.GetPixel(0,0).Alpha==0, "Unconfigured scene icon must have no background box");
    Check(unconfigured.Pixels.All(p=>p.Alpha<50 || p.Red<220 || p.Green<220 || p.Blue<220),
        "Unconfigured scene image contains a baked white caption");
}
Check(!Directory.Exists(Path.Combine(root, "icontemplates")) || !Directory.GetFiles(Path.Combine(root, "icontemplates"), "*.ict").Any(), "Runtime actions must not have template overrides");
byte[] RenderScene(ActionEditorActionParameters selection, int size, string suffix) {
    Check(sceneCollection.TryGetCommandImage(scene.Name, selection, PluginImageSize.None, out var source), "Scene callback failed");
    Check(ActionIcon.TryReadFromFile(Path.Combine(root, "metadata/DefaultIconTemplate.ict"), out var layout), "Missing default layout");
    layout.SetImage(ImageBytes(source, "Live scene source"));
    using var image = ActionIconBuilder.CreateImage(size, size, layout, ActionImageBuilderFlags.None);
    var bytes = image.ToArray();
    using var bitmap = SKBitmap.Decode(bytes);
    var glyphRows = bitmap.Pixels.Select((p, i) => (p, y:i / size)).Where(x => x.p.Blue > 220 && x.p.Red < 210 && x.p.Red > 110 && x.p.Green > 90).Select(x => x.y).ToArray();
    Check(glyphRows.Length > 40 && glyphRows.Min() >= size * .11, "Scene icon missing or top padding too small");
    Check(bitmap.Pixels.Where((p,i) => i / size > size * .54).Count(p => p.Red > 220 && p.Green > 220 && p.Blue > 220) > 65, "Larger scene caption missing");
    Check(bitmap.Pixels.Skip((size - 3)*size).All(p => p == bitmap.GetPixel(0,0)), "Scene caption clips at bottom");
    File.WriteAllBytes(Path.Combine(output, $"ShowScene-{suffix}-{size}.png"), bytes);
    return bytes;
}
Console.WriteLine("PASS: all image callbacks including unspecified size, native packaged state lookup, icon-only templates, scene glyph and caption.");

// Real WebChannel messages drive actual action states and icon selection.
await using var mock = new MockMeld();
mock.Start();
foreach (var action in toggles) Call(action, "OnLoad");
Call(scene, "OnLoad"); Call(camera, "OnLoad");
plugin.Load();
var editorState = new ActionEditorState(new[] { new ActionEditorControlState { Name = "Scene", Value = "" } });
ActionEditorListboxItemsRequestedEventArgs Picker() => (ActionEditorListboxItemsRequestedEventArgs)Call(scene.ActionEditor,
    "InvokeListboxItemsRequestedEvent", editorState, "Scene");
// Render cold saved selections directly. No editor setup or state-cache seeding.
// Reuse the layer-name fixtures below; only their Layer value is sent to the
// native list action. No ActionEditor parameter dictionary is sent to the host.
string CameraSelection(ActionEditorActionParameters selection = null) => selection == null ? "automatic" :
    selection.TryGetString("layer", out var layer) ? layer : null;
string CameraAssetHash(string state) => Hash(File.ReadAllBytes(Path.Combine(root, "actionicons",
    $"Loupedeck.MeldStudioControlsPlugin.ToggleCameraCommand______{state}.png")));
var cameraVisibleHash = CameraAssetHash("active");
var cameraHiddenHash = CameraAssetHash("inactive");
var cameraUnknownHash = CameraAssetHash("unknown");
string CameraLabel(ActionEditorActionParameters selection = null) {
    Check(camera.TryGetCommandDisplayName(CameraSelection(selection), PluginImageSize.None, out var text) &&
        String.IsNullOrEmpty(text), "Camera callback supplied a text caption");
    var hash = Hash(CameraImage(selection));
    Check(hash == cameraVisibleHash || hash == cameraHiddenHash || hash == cameraUnknownHash,
        "Camera returned an unexpected bitmap");
    return hash == cameraVisibleHash ? "Hide Camera" :
        hash == cameraHiddenHash ? "Show Camera" :
        "Camera";
}
bool PressCamera(ActionEditorActionParameters selection = null) => camera.TryRunCommand(CameraSelection(selection));
void RejectCamera(ActionEditorActionParameters selection, string message) {
    var before=mock.Calls.Count; PressCamera(selection); Check(mock.Calls.Count==before,message);
}
PickerChoices CameraChoices(string component, string legacySceneKey=null, string layerKey="") {
    Check(camera.IsProfileAction && camera.ProfileActionType == "list;Camera / capture layer" && !camera.HasParameter,
        "Camera must expose one configurable action with a picker");
    Check(camera.TryGetParameters(out var choices), "Native camera list request failed");
    return new(choices.Select(x => new PickerChoice(x.Name, x.DisplayName)).ToList());
}
string[] CurrentLabels() => new[]{toggles[0].GetCurrentState()?.DisplayName, toggles[1].GetCurrentState()?.DisplayName,
    toggles[2].GetCurrentState()?.DisplayName, CameraLabel(), toggles[3].GetCurrentState()?.DisplayName};
byte[] CameraImage(ActionEditorActionParameters selection = null, PluginImageSize size = PluginImageSize.None) {
    Check(camera.TryGetCommandImage(CameraSelection(selection), size, out var image), "Camera callback failed");
    return ImageBytes(image, "Camera image");
}
async Task Labels(params string[] expected) => await Until(() => CurrentLabels().SequenceEqual(expected),
    "Native state mismatch: " + string.Join(", ", CurrentLabels()));
await mock.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
await Labels("Start Stream", "Start Recording", "Microphone", "Camera", "Start Virtual Camera");
Check(Picker().Items.Single().Name == "meld-unavailable", "Null session must give a useful picker placeholder");
await mock.PublishAsync();
await Labels("Start Stream", "Start Recording", "Mute Microphone", "Hide Camera", "Start Virtual Camera");
// The SDK's list profile type is one catalog action with a picker. It must
// list the current scene's layers without a preselected scene or editor event.
var freshCameraChoices=CameraChoices("layer");
Check(freshCameraChoices.Items.Any(x=>x.DisplayName==mock.CameraName),
    "Fresh native camera picker did not list the current scene's camera");
Check(!camera.IsWidget && !camera.HasParameter && camera.IsProfileAction,
    "Camera must remain executable and must not expand into multiple catalog actions");
var freshSelection=freshCameraChoices.Items.Single(x=>x.DisplayName==mock.CameraName).Name;
Check(CameraLabel(new ActionEditorActionParameters(new Dictionary<string,string>{{"Layer",freshSelection}}))=="Hide Camera",
    "Selection from the fresh camera picker did not resolve visibility");
Console.WriteLine("PASS: one native camera list picker populates without editor setup; microphone-style multistate command.");
await Until(() => Picker().Items.Count == 14, "Live scene picker did not populate every scene");
var choice = Picker().Items.Single(x => x.DisplayName == "SYNAPSE // STARTING SOON");
var parameters = new ActionEditorActionParameters(new Dictionary<string,string>{{"Scene", choice.Name}});
Check(sceneCollection.TryGetCommandDisplayName(scene.Name, parameters, out var sceneLabel) && sceneLabel == choice.DisplayName, "Selected scene caption incorrect");
Check(sceneCollection.TryGetCommandDisplayName(scene.Name, null, out var emptyLabel) && emptyLabel == "Show Scene", "Unconfigured scene caption crashed");

// Toggle each action twice and check outgoing methods, arguments, graphics and labels.
for (var i = 0; i < 5; i++) {
    var action = i == 3 ? null : toggles[i == 4 ? 3 : i];
    string Label() => action?.GetCurrentState()?.DisplayName ?? CameraLabel();
    bool Press() => action != null ? action.TryRunCommand(null) : PressCamera();
    byte[] Graphic() {
        if(action == null) return CameraImage();
        action.TryGetCommandImage(null, PluginImageSize.None, out var image);
        return ImageBytes(image, action.GetType().Name);
    }
    var initialLabel=Label(); var initialHash=Hash(Graphic());
    Check(Press(), "SDK rejected toggle execution");
    await Until(() => Label()!=initialLabel, "Toggle state did not change");
    Check(Hash(Graphic())!=initialHash, "Toggle image did not change");
    Check(Press(), "SDK rejected second toggle execution");
    await Until(() => Label()==initialLabel, "Toggle state did not return");
}
foreach(var size in sizes) CameraImage(size:size);
foreach(var action in singles) Check(action.TryRunCommand(null), "SDK rejected single action");
await Until(() => mock.Calls.Count >= 12, "Not all action commands arrived");
var expectedCalls = new[]{"toggleStream:[]", "toggleStream:[]", "toggleRecord:[]", "toggleRecord:[]",
    "toggleMute:[\"mic\"]", "toggleMute:[\"mic\"]", "toggleLayer:[\"scene-0\",\"cam\"]", "toggleLayer:[\"scene-0\",\"cam\"]",
    "sendCommand:[\"meld.toggleVirtualCameraAction\"]", "sendCommand:[\"meld.toggleVirtualCameraAction\"]"};
Check(mock.Calls.Take(10).SequenceEqual(expectedCalls), "Incorrect outgoing toggle method/arguments");
Check(mock.Calls.Contains("sendCommand:[\"meld.recordClip\"]") && mock.Calls.Contains("sendCommand:[\"meld.screenshot\"]"), "Clip/screenshot commands incorrect");

var shortChoice = Picker().Items.Single(x => x.DisplayName == "VALORANT");
var shortParameters = new ActionEditorActionParameters(new Dictionary<string,string>{{"Scene",shortChoice.Name}});
foreach (var size in new[]{80,116}) {
    var live=RenderScene(shortParameters,size,"short-active");
    var inactive=RenderScene(parameters,size,"long-inactive");
    using var a=SKBitmap.Decode(live); using var b=SKBitmap.Decode(inactive);
    Check(a.GetPixel(0,0)!=b.GetPixel(0,0), "Active scene background not distinct");
}
Check((bool)Call(scene, "RunCommand", parameters), "Selected scene did not execute");
await Until(() => mock.Calls.Contains("showScene:[\"scene-1\"]"), "Show Scene sent wrong scene ID");
await Until(() => CameraLabel()=="Camera", "Camera must not target a layer in a different scene");
foreach(var size in new[]{80,116}) {
    RenderScene(shortParameters,size,"short-inactive");
    RenderScene(parameters,size,"long-active");
}
RejectCamera(null,"Camera toggled a non-live scene");
mock.CurrentScene=0; await mock.PublishAsync();
await Until(() => CameraLabel()=="Hide Camera", "Camera did not follow external scene change");
// IDs and ordering can change between sessions; a unique name remains selected.
mock.IdPrefix = "replacement-";
await mock.PublishAsync();
await Until(() => Picker().Items.Single(x => x.DisplayName == choice.DisplayName).Name != choice.Name, "Scene refresh failed");
Check((bool)Call(scene, "RunCommand", parameters), "Selection was lost after session ID replacement");
await Until(() => mock.Calls.Contains("showScene:[\"replacement-1\"]"), "Show Scene used stale session ID");
var bad = new ActionEditorActionParameters(new Dictionary<string,string>{{"Scene","not-a-scene"}});
Check(!(bool)Call(scene,"RunCommand",bad), "Invalid scene selection should not send commands");
// Duplicate scene names have distinct picker values.
var duplicate = Picker().Items.Where(x => x.DisplayName == "Duplicate").ToArray();
Check(duplicate.Length == 2 && duplicate[0].Name != duplicate[1].Name, "Duplicate scene names are ambiguous in picker");

mock.CurrentScene=0;
mock.Streaming = mock.Recording = mock.VirtualCamera = true;
mock.Muted = true; mock.CameraVisible = false;
await mock.PublishAsync();
await Labels("Stop Stream", "Stop Recording", "Unmute Microphone", "Show Camera", "Stop Virtual Camera");
Console.WriteLine("PASS: 14-scene picker, persisted selection with new IDs, every outgoing action, press twice, and external state changes.");

// Custom camera names, grouping, duplicate names, session IDs and live-scene scope.
PickerChoices CameraPicker() => CameraChoices("layer");
mock.CameraName="Elgato Facecam Pro"; mock.GroupedCamera=true; await mock.PublishAsync();
await Until(() => CameraPicker().Items.Any(x=>x.DisplayName==mock.CameraName), "Custom camera missing from picker");
Check(CameraLabel()=="Camera", "Automatic mode selected an arbitrary camera name");
var cameraChoice=CameraPicker().Items.Single(x=>x.DisplayName==mock.CameraName);
var cameraParameters=new ActionEditorActionParameters(new Dictionary<string,string>{{"Layer",cameraChoice.Name}});
Check(CameraLabel(cameraParameters)=="Show Camera", "Selected hidden custom camera state incorrect");
var hiddenHash=Hash(CameraImage(cameraParameters));
Check(PressCamera(cameraParameters), "Custom camera command rejected");
await Until(()=>CameraLabel(cameraParameters)=="Hide Camera", "Custom camera did not become visible");
Check(Hash(CameraImage(cameraParameters))!=hiddenHash, "Custom camera graphic did not follow visibility");
Check(mock.Calls.Last()=="toggleLayer:[\"replacement-0\",\"cam\"]", "Grouped camera sent incorrect owning scene/ID");
mock.CurrentScene=mock.CameraScene=2; mock.CameraId="new-camera-id"; mock.CameraIndex=7; await mock.PublishAsync();
await Until(()=>CameraPicker().Items.Any(x=>x.Name!=cameraChoice.Name && x.DisplayName==mock.CameraName), "Camera picker did not refresh");
Check(PressCamera(cameraParameters), "Saved camera name did not survive changed IDs/scene/index");
await Until(()=>CameraLabel(cameraParameters)=="Show Camera", "Camera did not toggle in newly live scene");
Check(mock.Calls.Last()=="toggleLayer:[\"replacement-2\",\"new-camera-id\"]", "Camera used stale IDs");
mock.CameraIndex=4; mock.DuplicateCamera=true; await mock.PublishAsync();
await Until(()=>CameraPicker().Items.Count(x=>x.DisplayName==mock.CameraName)==2, "Duplicate camera choices missing");
Check(PressCamera(cameraParameters), "Saved position did not disambiguate duplicate camera names");
await Until(()=>CameraLabel(cameraParameters)=="Hide Camera", "Duplicate choice toggled wrong layer");
RejectCamera(new ActionEditorActionParameters(new Dictionary<string,string>{{"Layer","invalid"}}),"Invalid camera selection executed");
mock.CurrentScene=mock.CameraScene=0; mock.CameraName="Camera"; mock.CameraId="cam"; mock.GroupedCamera=false; mock.DuplicateCamera=false;
mock.CameraVisible=false; await mock.PublishAsync();
await Until(()=>CameraLabel()=="Show Camera", "Camera automatic compatibility did not recover");
Console.WriteLine("PASS: custom camera picker, grouped layers, duplicate names, current scene, and replacement IDs; scene backgrounds follow live selection.");

// The camera selection follows the displayed scene, including old assignments
// that still carry a fixed Scene parameter from 0.4.9.
mock.IncludeOtherCaptures=true;
await mock.PublishAsync();
await Until(()=>CameraChoices("layer").Items.Any(x=>x.DisplayName=="HD60 X"),"Native camera picker lost capture-card layer");
Check(CameraChoices("layer").Items.Count==4,"Picker must contain automatic plus three untyped capture candidates");
Check(!CameraChoices("layer").Items.Any(x=>new[]{"Camera Frame","Browser","Video","Camera effect","Microphone"}.Contains(x.DisplayName)),
    "Known non-capture sources leaked into the camera list");
var legacySceneKey=Picker().Items.Single(x=>x.DisplayName=="VALORANT").Name;
var legacyCamera=new ActionEditorActionParameters(new Dictionary<string,string>{{"scene",legacySceneKey},{"layer","automatic"}});
mock.CurrentScene=1;await mock.PublishAsync();
await Until(()=>CameraChoices("layer").Items.Any(x=>x.Name=="unavailable"),"Live picker did not follow scene change to an empty scene");
Check(CameraChoices("layer",legacySceneKey).Items.Single().Name=="unavailable","Legacy scene parameter leaked inactive-scene layers into the picker");
RejectCamera(legacyCamera,"Legacy Scene parameter toggled an inactive scene");
mock.ExtraItems["old-camera"]=new{type="layer",name="Camera",index=4,parent="replacement-0",visible=false};
mock.CameraScene=1;mock.CameraId="live-camera";mock.CameraIndex=8;mock.CameraVisible=true;
await mock.PublishAsync();
await Until(()=>CameraLabel(legacyCamera)=="Hide Camera","Old Scene value prevented following the displayed scene");
Check(CameraChoices("layer").Items.Count(x=>x.DisplayName=="Camera")==1,"Inactive-scene camera leaked into the current picker");
Check(PressCamera(legacyCamera),"Saved camera did not follow the newly displayed scene");
await Until(()=>CameraLabel(legacyCamera)=="Show Camera","Current scene camera did not toggle");
Check(mock.Calls.Last()=="toggleLayer:[\"replacement-1\",\"live-camera\"]","Camera command used old scene or layer IDs");
var excluded=new ActionEditorActionParameters(new Dictionary<string,string>{{"Scene",legacySceneKey},
    {"Layer",Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Camera Frame"))+":9"}});
RejectCamera(excluded,"Camera picker action must reject a saved image-layer target");
mock.CurrentScene=-1;await mock.PublishAsync();
await Until(()=>CameraChoices("layer").Items.Any(x=>x.DisplayName.StartsWith("No scene is currently shown")),"No-live-scene state was blank");
Check(CameraLabel(legacyCamera)=="Camera","An old Scene value supplied visibility when no scene was current");
RejectCamera(legacyCamera,"Camera toggled a scene while no scene was displayed");
Check(CameraLabel(new ActionEditorActionParameters(new Dictionary<string,string>{{"Scene","invalid"}}))=="Camera","Removed scene must have an unknown camera state");
mock.ExtraItems.Clear();mock.CameraScene=mock.CurrentScene=0;mock.CameraId="cam";mock.CameraIndex=4;
mock.IncludeCamera=false;mock.IncludeOtherCaptures=false;await mock.PublishAsync();
await Until(()=>CameraChoices("layer",legacySceneKey).Items.Any(x=>x.DisplayName.StartsWith("No camera/capture layers")),
    "A scene containing only image/browser/media layers must have an explicit empty state");
mock.IncludeCamera=true;mock.CameraVisible=false;await mock.PublishAsync();
await Until(()=>CameraLabel()=="Show Camera","Restored camera was not recognized");
Console.WriteLine("PASS: one scene-agnostic camera picker, scene switching, legacy settings, native current-scene IDs, source exclusions and empty states.");

// Case-insensitive matching must never change the IDs sent to Meld or pick an
// arbitrary duplicate. Use mixed property keys/types and uppercase parent IDs.
mock.MixedCaseSession=true; mock.GroupedCamera=true; mock.CameraName="  cAmErA  ";
mock.MicName="  mIcRoPhOnE  "; mock.MicParent=""; mock.MicId="Microphone-ID";
await mock.PublishAsync();
await Until(()=>CameraChoices("LAYER").Items.Any(x=>x.DisplayName=="  cAmErA  "),"Mixed-case grouped capture disappeared");
Check(CameraLabel(legacyCamera)=="Show Camera","Mixed-case current-scene camera lookup failed");
var mixedSavedSelection=new ActionEditorActionParameters(new Dictionary<string,string>{
    {"sCeNe",Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(" valorant "))+":0"},
    {"lAyEr",Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("CAMERA"))+":4"}});
Check(CameraLabel(mixedSavedSelection)=="Show Camera","Saved scene/layer names were compared case-sensitively");
Check(CameraChoices("LAYER").Items.Count==2,"Uppercase image/browser/media source fields were not excluded");
Check(PressCamera(legacyCamera),"Mixed-case camera failed to execute");
await Until(()=>CameraLabel()=="Hide Camera","Mixed-case camera did not change state");
Check(mock.Calls.Last()=="toggleLayer:[\"replacement-0\",\"cam\"]","Normalized camera IDs leaked into outgoing API call");
mock.ExtraItems["case-camera"]=new{type="layer",name="CAMERA",index=10,parent="GROUP",visible=false};
await mock.PublishAsync();
await Until(()=>CameraLabel()=="Camera","Case-only duplicate names should make Automatic ambiguous");
Check(CameraLabel(mixedSavedSelection)=="Hide Camera","Saved position did not disambiguate case-only duplicates");
mock.ExtraItems.Clear();await mock.PublishAsync();
await Until(()=>CameraLabel()=="Hide Camera","Removing the duplicate did not recover camera targeting");
await Until(()=>toggles[2].GetCurrentState().DisplayName=="Unmute Microphone","Empty-parent, padded mixed-case microphone was not resolved");
Check(toggles[2].TryRunCommand(null),"Microphone SDK rejected toggle");
await Until(()=>toggles[2].GetCurrentState().DisplayName=="Mute Microphone","Mixed-case microphone did not unmute");
Check(mock.Calls.Last()=="toggleMute:[\"Microphone-ID\"]","Microphone command did not preserve the original track ID");
mock.ExtraItems["scene-mic"]=new{type="track",name="Mic",parent="cam",muted=true};
await mock.PublishAsync();
await Until(()=>Picker().Items.Count==14,"Mixed-case scenes missing from Show Scene");
Check(toggles[2].TryRunCommand(null),"Global microphone did not remain usable");
await Until(()=>toggles[2].GetCurrentState().DisplayName=="Unmute Microphone","Global microphone should take priority over a scene track");
Check(mock.Calls.Last()=="toggleMute:[\"Microphone-ID\"]","Scene track stole the global microphone target");
mock.ExtraItems["ambiguous-mic"]=new{type="track",name="MIC",parent=" ",muted=false};
await mock.PublishAsync();
await Until(()=>toggles[2].GetCurrentState().Name=="unknown","Duplicate global names should be unresolved");
var ambiguousCalls=mock.Calls.Count;
toggles[2].TryRunCommand(null);
Check(mock.Calls.Count==ambiguousCalls,"Ambiguous microphone sent a mute command");
mock.ExtraItems.Clear(); mock.MicParent="CAM";
await mock.PublishAsync();
await Until(()=>toggles[2].GetCurrentState().DisplayName=="Unmute Microphone","Mic attached to a grouped live-scene layer was not resolved");
Check(toggles[2].TryRunCommand(null),"Scene microphone rejected");
await Until(()=>toggles[2].GetCurrentState().DisplayName=="Mute Microphone","Scene microphone did not toggle");
Check(mock.Calls.Last()=="toggleMute:[\"Microphone-ID\"]","Scene microphone used a layer ID instead of track ID");
mock.CurrentScene=1;await mock.PublishAsync();
await Until(()=>toggles[2].GetCurrentState().Name=="unknown","Mic in an inactive scene should not be selected");
mock.CurrentScene=0;mock.MicParent="replacement-0";await mock.PublishAsync();
await Until(()=>toggles[2].GetCurrentState().DisplayName=="Mute Microphone","Direct scene-parent microphone was not resolved");
mock.MicName="USB Audio Device";await mock.PublishAsync();
await Until(()=>toggles[2].GetCurrentState().Name=="unknown","Arbitrary audio track should not be guessed to be a microphone");
var unknownCalls=mock.Calls.Count;toggles[2].TryRunCommand(null);
Check(mock.Calls.Count==unknownCalls,"Unrecognized microphone name sent a command");
mock.MicName="MIC";await mock.PublishAsync();
await Until(()=>toggles[2].GetCurrentState().DisplayName=="Mute Microphone","Named microphone did not recover before cyclic-parent test");
mock.MicParent="loop";
mock.ExtraItems["loop"]=new{type="group",parent="loop",name="Loop"};
await mock.PublishAsync();
await Until(()=>toggles[2].GetCurrentState().Name=="unknown","Cyclic parent chain should not resolve a microphone");
Check(Picker().Items.Count==14,"Cyclic hierarchy broke Show Scene enumeration");
mock.ExtraItems.Clear();mock.MicName="Microphone";mock.MicId="mic";mock.MicParent=null;mock.Muted=true;
mock.MixedCaseSession=false;mock.GroupedCamera=false;mock.CameraName="Camera";mock.CameraVisible=false;
await mock.PublishAsync();
await Labels("Stop Stream","Stop Recording","Unmute Microphone","Show Camera","Stop Virtual Camera");
Console.WriteLine("PASS: mixed-case schema, names and hierarchy; original RPC IDs; global and scene microphone scope; ambiguous/unknown targets remain inactive.");

// Unsupported virtual-camera status and missing controls never invent a state.
mock.OmitVirtualCameraStatus = true; mock.IncludeTargets = false;
await mock.PublishAsync();
await Labels("Stop Stream", "Stop Recording", "Microphone", "Camera", "Virtual Camera");
var before = mock.Calls.Count;
Check(toggles[3].TryRunCommand(null), "Virtual camera should still toggle without a status property");
await Until(() => mock.Calls.Count > before, "Unsupported-status virtual-camera command not sent");
Check(toggles[3].GetCurrentState().Name == "unknown", "Virtual camera guessed a state");
await mock.DisconnectAsync();
await Labels("Stream", "Recording", "Microphone", "Camera", "Virtual Camera");
Check(Picker().Items.Single().Name == "meld-unavailable", "Disconnected picker didn't report status");
Check(CameraChoices("layer").Items.Single().DisplayName.StartsWith("Waiting for Meld"),"Disconnected camera picker was blank");
// Automatic reconnect must recover the scene list and actual state.
mock.IncludeTargets = true; mock.OmitVirtualCameraStatus = false; mock.VirtualCamera = true;
await Until(() => mock.Connections >= 2, "Client did not reconnect");
await Labels("Stop Stream", "Stop Recording", "Microphone", "Camera", "Stop Virtual Camera");
await mock.PublishAsync();
await Labels("Stop Stream", "Stop Recording", "Unmute Microphone", "Show Camera", "Stop Virtual Camera");
await Until(() => Picker().Items.Count == 14, "Scenes did not recover after reconnect");
// Disconnected scene keeps its saved name and clears the live highlight.
plugin.Unload();
foreach(var action in toggles) Call(action, "OnUnload");
Call(scene,"OnUnload"); Call(camera,"OnUnload");
Console.WriteLine("PASS: unknown status, unavailable targets, disconnection and automatic reconnection.");
Console.WriteLine($"PASS: {checks} checks; {Directory.GetFiles(output, "*.png").Length} native renders. Hardware/Options+ UI still requires a real device.");

record PickerChoice(string Name,string DisplayName);
record PickerChoices(List<PickerChoice> Items);

public class NoOpNativeApi : DispatchProxy {
    protected override object Invoke(MethodInfo method, object[] args) =>
        method.ReturnType == typeof(void) || !method.ReturnType.IsValueType ? null : Activator.CreateInstance(method.ReturnType);
}
