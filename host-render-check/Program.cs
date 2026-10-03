using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Loupedeck;
using Loupedeck.Service;
using Loupedeck.MeldMxKeypadPlugin;
using SkiaSharp;

// Headless integration checks for the selected Logi Plugin Service assemblies.
// Uses the native profile models, event dispatcher and image renderers with
// a mock Meld server. Presses run through the native plugin command queue.
// It does not run the Options+ frontend or physical-device transport.
// args: package-directory output-directory [old-0.4.1-package-directory]
var root = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
var host = typeof(PluginManager).Assembly;
const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
object Call(object x, string name, params object[] values) => x.GetType().GetMethod(name, members)!.Invoke(x, values);
string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
var checks = 0;
void Check(bool pass, string message) { if (!pass) throw new Exception(message); Interlocked.Increment(ref checks); }
var expectedHostVersion = Environment.GetEnvironmentVariable("MELD_EXPECTED_HOST_VERSION");
foreach (var assembly in new[] { host, typeof(Plugin).Assembly, Assembly.Load("LoupedeckShared") }.Distinct()) {
    var version = assembly.GetName().Version!.ToString();
    Console.WriteLine($"Host assembly: {assembly.GetName().Name} {version} SHA256={Hash(File.ReadAllBytes(assembly.Location))}");
    if (!String.IsNullOrWhiteSpace(expectedHostVersion))
        Check(version == expectedHostVersion, $"Wrong host assembly: expected {expectedHostVersion}, loaded {assembly.GetName()}");
}
var plugin = new MeldMxKeypadPlugin();
// Plugin.Initialize normally owns this queue. The isolated host harness
// supplies that part of initialization and runs the real dispatcher.
var processQueue = typeof(Plugin).GetMethod("ProcessCommandQueue", members)!;
using var commandQueue = new PluginCommandQueue(plugin.Name,
    (Action)Delegate.CreateDelegate(typeof(Action), plugin, processQueue), 100);
typeof(Plugin).GetFields(members).Single(f => f.FieldType == typeof(PluginCommandQueue))
    .SetValue(plugin, commandQueue);
typeof(Plugin).GetProperty("Localization")!.SetValue(plugin,new PluginLocalizationEngine(plugin.Name,new TestLocalization()));
Check(Hash(File.ReadAllBytes(Path.Combine(root, "bin/MeldMxKeypadPlugin.dll"))) ==
    Hash(File.ReadAllBytes(typeof(MeldMxKeypadPlugin).Assembly.Location)), "Tested assembly differs from package");
var manager = (PluginManager)RuntimeHelpers.GetUninitializedObject(typeof(PluginManager));
foreach (var field in typeof(PluginManager).GetFields(members)) {
    if (field.FieldType.FullName.Contains("Dictionary") && field.FieldType.GetConstructor(Type.EmptyTypes) != null)
        field.SetValue(manager, Activator.CreateInstance(field.FieldType));
}
foreach (var field in typeof(PluginManager).GetFields(members)) {
    if (!field.FieldType.IsGenericType) continue;
    if (field.FieldType.GetGenericArguments().Last() == typeof(Plugin))
        ((IDictionary)field.GetValue(manager))[plugin.Name] = plugin;
    if (field.FieldType.GetGenericArguments().Last() == typeof(uint))
        field.FieldType.GetProperty("Item")!.SetValue(field.GetValue(manager),
            field.Name is "af" or "ai" ? 0xff000000u : 0xffffffffu, new object[] { plugin.Name });
}
var wrapper = new PluginWrapper(plugin);
var wrappers = typeof(PluginManager).GetFields(members).Single(f => f.FieldType.IsGenericType &&
    f.FieldType.GetGenericArguments().Last() == typeof(PluginWrapper));
((IDictionary)wrappers.GetValue(manager))[plugin.Name] = wrapper;
var defaultTemplate = typeof(PluginWrapper).GetFields(members).Single(f => f.FieldType == typeof(string));
var commands = new PluginDynamicCommand[] { new ToggleStreamCommand(), new ToggleRecordingCommand(),
    new ToggleMicrophoneCommand(), new ToggleVirtualCameraCommand(),
    new SaveClipCommand(), new ScreenshotCommand() };
foreach (var command in commands) {
    typeof(PluginAction).GetProperty("Name")!.SetValue(command, command.GetType().FullName);
    Call(plugin.DynamicCommands, "AddAction", command);
    typeof(Plugin).GetMethod("AddAction", members)!.Invoke(plugin, new object[] { command, false });
    if (command is PluginMultistateDynamicCommand) Call(command, "OnLoad");
}
var scene = new ShowSceneCommand();
var camera = (PluginAction)Activator.CreateInstance(typeof(MeldMxKeypadPlugin).Assembly.GetType("Loupedeck.MeldMxKeypadPlugin.ToggleCameraCommand")!);
typeof(Plugin).GetProperty("NativeApi")!.SetValue(plugin,DispatchProxy.Create<INativeApi,NoOpNativeApi>());
if (camera is ActionEditorCommand oldCamera) plugin.ActionEditorCommands.AddAction(oldCamera);
else {
    var init=typeof(PluginDynamicAction).GetMethod("Init",members)!;
    var ctor=init.GetParameters()[2].ParameterType.GetConstructors(members).Single();
    var names=new[]{"PluginActionsChanged","OnActionImageChanged","OnAdjustmentValueChanged","OnActionStateChanged"};
    var nativeCallbacks=ctor.Invoke(ctor.GetParameters().Select((p,i)=> {
        var signature=p.ParameterType.GetMethod("Invoke")!.GetParameters().Select(x=>x.ParameterType).ToArray();
        var method=typeof(Plugin).GetMethods(members).Single(m=>m.Name==names[i] &&
            m.GetParameters().Select(x=>x.ParameterType).SequenceEqual(signature));
        return (object)Delegate.CreateDelegate(p.ParameterType,plugin,method);
    }).ToArray());
    init.Invoke(camera,new[]{(object)plugin,plugin.NativeApi,nativeCallbacks});
    Call(plugin.DynamicCommands, "AddAction", camera);
}
plugin.AddAction(camera,false);
plugin.ActionEditorCommands.AddAction(scene);
typeof(Plugin).GetMethod("AddAction", members)!.Invoke(plugin, new object[] { scene, false });
var renderer = host.GetTypes().Single(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public)
    .Any(m => m.Name == "CreateActionImage" && m.GetParameters().Length == 9));
var render = renderer.GetMethods(BindingFlags.Static | BindingFlags.Public)
    .Single(m => m.Name == "CreateActionImage" && m.GetParameters().Length == 9);
var customType = render.GetParameters()[4].ParameterType;
byte[] Render(string action, int size = 116, string caption = null) {
    object custom = null;
    if (caption != null) {
        custom = Activator.CreateInstance(customType, true);
        foreach (var property in customType.GetProperties().Where(p => p.PropertyType == typeof(BitmapColor)))
            property.SetValue(custom, BitmapColor.Invalid);
        customType.GetProperty("Text")!.SetValue(custom, caption);
    }
    using var image = (BitmapImage)render.Invoke(null, new object[] { manager, action, null, null, custom,
        new BitmapImageSize(size, size), BitmapRotation.None, ActionImageBuilderFlags.None, BitmapImageFormat.Png });
    Check(image != null, "Host returned no image: " + action);
    return image.ToArray();
}
int ColoredPixels(SKBitmap image) => image.Pixels.Count(p => p.Alpha > 50 &&
    Math.Max(p.Red, Math.Max(p.Green, p.Blue)) - Math.Min(p.Red, Math.Min(p.Green, p.Blue)) > 25);
Console.WriteLine("Actual host: " + host.GetName());
var packages = new List<(string Path, bool Old)>();
if (args.Length > 2) packages.Add((Path.GetFullPath(args[2]), true));
packages.Add((root, false));
foreach (var (path, old) in packages) {
    Call(plugin.PluginPackage, "Init", path);
    defaultTemplate.SetValue(wrapper, File.ReadAllText(Path.Combine(path, "metadata/DefaultIconTemplate.ict")));
    foreach (var command in commands) {
        var multi = command as PluginMultistateDynamicCommand;
        var states = multi != null ? multi.States.Select(x => x.Name).ToArray() : new string[] { null };
        foreach (var state in states) {
            var action = new ActionString(plugin.Name, command.Name, null, state).ToString();
            var bytes = Render(action);
            using var bitmap = SKBitmap.Decode(bytes);
            Check(bitmap != null && bitmap.Width == 116 && bitmap.Height == 116, "Host image failed to decode");
            var colored = ColoredPixels(bitmap);
            Check(old ? colored == 0 : colored > 100, (old ? "Old failure not reproduced: " : "New icon missing: ") + action);
            Console.WriteLine($"{(old ? "OLD" : "NEW")} {command.GetType().Name}/{state ?? "default"}: {colored} colored pixels");
            File.WriteAllBytes(Path.Combine(output, (old ? "old-" : "new-") + command.GetType().Name + "-" + (state ?? "default") + ".png"), bytes);
            if (old) continue;
            Check(Hash(bytes) == Hash(Render(action, caption: "MUST NOT APPEAR")), "Caption leaked onto graphic-only action");
            if (multi != null) {
                var setState = typeof(PluginMultistateDynamicCommand).GetMethod("SetCurrentState", members, null, new[] { typeof(int) }, null)!;
                setState.Invoke(multi, new object[] { Array.IndexOf(states, state) });
                var toggleAction = new ActionString(plugin.Name, command.Name, null).ToString();
                Check(Hash(bytes) == Hash(Render(toggleAction)), "Host did not follow current toggle state: " + action);
                Check(manager.GetPluginActionDisplayName(toggleAction, PluginImageSize.None) == multi.GetCurrentState().DisplayName,
                    "Host did not use current state label");
            }
        }
    }
    if (!old) Check(!manager.TryGetPluginActionIconTemplate(new ActionString(plugin.Name, scene.Name, null), out _),
        "Static scene override would prevent live background updates");
}

// The profile renderer resolves saved ActionEditor parameters. A standalone
// request with a bare template name is a separate contract, tested below as
// a diagnostic, not as evidence of which request the Options+ frontend uses.
var service=(LoupedeckService)RuntimeHelpers.GetUninitializedObject(typeof(LoupedeckService));
typeof(LoupedeckService).GetFields(members).Single(f=>f.FieldType==typeof(PluginManager)).SetValue(service,manager);
typeof(PluginManager).GetFields(members).Single(f=>f.FieldType==typeof(LoupedeckService)).SetValue(manager,service);
// Exercise the service's default-template factory with an unconfigured image
// and a supplied scene caption. This does not capture a frontend request.
var sceneAction = new ActionString(plugin.Name, scene.Name, null);
Check(manager.TryCreatePluginActionIconTemplate(sceneAction, null, PluginImageSize.None, out var editorTemplate),
    "Options+ default scene template missing");
File.WriteAllText(Path.Combine(output,"options-scene-template.json"),JsonHelpers.SerializeAnyObject(editorTemplate));
Check(editorTemplate.TryGetImageItem(out var editorImage),"Options+ scene symbol missing");
using(var source=SKBitmap.Decode(editorImage.Image)) {
    Check(source.GetPixel(0,0).Alpha==0,"Options+ source contains an opaque nested button");
    Check(source.Pixels.All(p=>p.Alpha<50 || p.Red<220 || p.Green<220 || p.Blue<220),
        "Options+ source contains the duplicate Show Scene caption");
}
var nativeCaption=editorTemplate.Items.OfType<ActionIconTextItem>().Single(x=>x.IsVisible);
Check(nativeCaption.FontSize==5,"Options+ scene name should retain the large native caption");
var optionsCustom = Activator.CreateInstance(customType, true);
foreach(var property in customType.GetProperties().Where(p=>p.PropertyType==typeof(BitmapColor)))
    property.SetValue(optionsCustom,BitmapColor.Invalid);
customType.GetProperty("Text")!.SetValue(optionsCustom,"VALORANT");
foreach(var size in new[]{80,116}) {
    using var optionsImage=(BitmapImage)render.Invoke(null,new object[]{manager,sceneAction.ToString(),
        new Dictionary<string,string>{{"Scene","VkFMT1JBTlQ=:0"}},editorTemplate,optionsCustom,
        new BitmapImageSize(size,size),BitmapRotation.None,ActionImageBuilderFlags.None,BitmapImageFormat.Png});
    var bytes=optionsImage.ToArray();
    using var bitmap=SKBitmap.Decode(bytes);
    var white=bitmap.Pixels.Select((p,i)=>(p,y:i/size)).Where(x=>x.p.Red>220 && x.p.Green>220 && x.p.Blue>220).ToArray();
    Check(white.Length>50 && white.All(x=>x.y>=size*.65),"Scene name missing or duplicated above the native caption");
    var glyph=bitmap.Pixels.Select((p,i)=>(p,x:i%size,y:i/size)).Where(x=>x.p.Blue>220 && x.p.Red<210 && x.p.Red>110 && x.p.Green>90).ToArray();
    Check(glyph.Length>50 && glyph.Min(x=>x.y)>=size*.14,"Scene symbol needs top padding");
    Check(glyph.Max(x=>x.x)-glyph.Min(x=>x.x)>=size*.37,"Scene symbol is too small");
    Check(white.Min(x=>x.y)-glyph.Max(x=>x.y)>=size*.10,"Scene symbol and name need separation");
    Check(bitmap.GetPixel(size/2,(int)(size*.6))==SKColors.Black,"Opaque inner button remains");
    File.WriteAllBytes(Path.Combine(output,$"Options-ShowScene-{size}.png"),bytes);
}
Console.WriteLine("PASS: service default-template factory and renderer: transparent larger symbol, top padding, one native scene caption.");
Check(!camera.IsWidget, "Camera action must remain an executable non-widget action");
var profile = new ApplicationProfile();
// Empty isolated profile directory; no user profiles are opened or changed.
var profileDirectory=Path.Combine(output,"empty-profile");
Directory.CreateDirectory(profileDirectory);
typeof(ApplicationProfile).GetFields(members | BindingFlags.DeclaredOnly).Single(f=>f.FieldType==typeof(string) &&
    !f.IsDefined(typeof(CompilerGeneratedAttribute),false)).SetValue(profile,profileDirectory);
var callbackType=typeof(Plugin).GetNestedTypes(BindingFlags.NonPublic).Single(t =>
    t.GetInterfaces().Any(i => i.GetMethods().Any(m => m.Name=="ListboxItemsChanged")));
var callbacks=Activator.CreateInstance(callbackType,plugin);
var invalidations=new System.Collections.Concurrent.ConcurrentQueue<string>();
plugin.ActionImageChanged += (_,e)=>invalidations.Enqueue(e.ActionName);
typeof(ActionEditorAction).GetMethod("Initialize",members)!.Invoke(scene,new[]{(object)plugin,callbacks});
Call(scene,"OnLoad");
if (camera is ActionEditorAction cameraEditor)
    typeof(ActionEditorAction).GetMethod("Initialize",members)!.Invoke(cameraEditor,new[]{(object)plugin,callbacks});
if(camera is PluginDynamicAction dynamicCamera) Check(dynamicCamera.Load(),"Native camera lifecycle load failed");
else Call(camera,"OnLoad");
byte[] RenderProfile(ActionEditorCommand command, ActionEditorActionParameters selection, int size=116) {
    var templateAction = new ActionString(plugin.Name,command.Name,null).ToString();
    var assigned = new ApplicationProfileCommand(command,templateAction,null,null,null,null,selection);
    profile.ProfileActions.Add(assigned);
    using var image = ApplicationProfileImages.CreateActionImage(service,profile,null,assigned.Name,
        (PluginImageSize)size,BitmapRotation.None,ActionImageBuilderFlags.None);
    Check(image!=null,"Host profile image missing");
    return image.ToArray();
}
async Task Until(Func<bool> condition,string message) {
    for(var i=0;i<100;i++) { if(condition()) { Interlocked.Increment(ref checks);return; } await Task.Delay(50); }
    throw new Exception(message);
}
var inactiveColor = new SKColor(0x11,0x18,0x23);
var activeColor = new SKColor(0x49,0x30,0x79);
SKColor Background(byte[] bytes) { using var image=SKBitmap.Decode(bytes); return image.GetPixel(0,0); }
ActionEditorActionParameters Selection(string control,string name,int index) => new(new Dictionary<string,string>{
    {control,Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(name))+":"+index}});
var shortScene=Selection("Scene","VALORANT",0);
var longScene=Selection("Scene","SYNAPSE // STARTING SOON",1);
await using var mock=new MockMeld();
mock.Start(); plugin.Load();
await mock.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
await mock.PublishAsync();
await Until(()=>invalidations.Contains(scene.Name),"Scene updates did not request a host redraw");
await Until(()=>Background(RenderProfile(scene,shortScene))==activeColor,"Actual profile renderer did not show the active scene");
foreach(var selected in new[]{0,1}) {
    mock.CurrentScene=selected; await mock.PublishAsync();
    await Until(()=>Background(RenderProfile(scene,shortScene))==(selected==0?activeColor:inactiveColor),"Profile background did not update");
    foreach(var size in new[]{80,116}) foreach(var (selection,label) in new[]{(shortScene,"short"),(longScene,"long")}) {
        var bytes=RenderProfile(scene,selection,size);
        using var bitmap=SKBitmap.Decode(bytes);
        Check(bitmap.Width==size && bitmap.Height==size,"Profile renderer returned wrong dimensions");
        var isActive=label=="short" ? selected==0 : selected==1;
        Check(Background(bytes)==(isActive?activeColor:inactiveColor),"Wrong scene highlight");
        Check(bitmap.Pixels.Count(p=>p.Red>220 && p.Green>220 && p.Blue>220)>90,"Large scene caption missing");
        File.WriteAllBytes(Path.Combine(output,$"ShowScene-{label}-{(isActive?"active":"inactive")}-{size}.png"),bytes);
    }
}
// Reproduce the reported old display through the service's default-template
// path before asserting the camera uses the microphone's native state path.
if (camera is not PluginMultistateDynamicCommand nativeCamera) {
    var oldAction=new ActionString(plugin.Name,camera.Name,null);
    Check(manager.TryCreatePluginActionIconTemplate(oldAction,null,PluginImageSize.None,out var oldTemplate),
        "Old camera template could not be reproduced");
    using var oldImage=(BitmapImage)render.Invoke(null,new object[]{manager,oldAction.ToString(),null,oldTemplate,null,
        new BitmapImageSize(116,116),BitmapRotation.None,ActionImageBuilderFlags.None,BitmapImageFormat.Png});
    File.WriteAllBytes(Path.Combine(output,"camera-regression-small-icon-caption.png"),oldImage.ToArray());
    throw new Exception("REGRESSION: camera uses the static action-editor display, not the microphone's native multistate display");
}
Check(!camera.HasActionEditor && camera.IsProfileAction && !camera.HasParameter &&
    camera.ProfileActionType=="list;Camera / capture layer", "Camera must expose ONE native list action, not separate layer actions");
Check(camera.GetType().BaseType==typeof(PluginMultistateDynamicCommand), "Camera must share microphone's native state API");
mock.CurrentScene=0;mock.CameraName="OBSBOT Meet 2 StreamCamera";mock.GroupedCamera=true;
mock.ExtraItems["desk-camera"]=new{type="layer",name="Desk Camera",index=3,parent="scene-0",visible=false};
await mock.PublishAsync();
PluginActionParameter[] Choices() {
    Check(nativeCamera.TryGetParameters(out var choices),"Native camera picker request failed");
    return choices;
}
await Until(()=>Choices().Any(x=>x.DisplayName==mock.CameraName),"Fresh picker did not list the capture layer");
Check(Choices().Any(x=>x.DisplayName=="Desk Camera"),"Hidden camera missing from picker");
var liveKey=Choices().Single(x=>x.DisplayName==mock.CameraName).Value;
var deskKey=Choices().Single(x=>x.DisplayName=="Desk Camera").Value;
string Address(string key,string state=null)=>new ActionString(plugin.Name,camera.Name,key,state).ToString();
var liveAction=Address(liveKey);
var deskAction=Address(deskKey);
string State(string action) {
    manager.TryGetPluginActionCurrentStateName(new ActionString(action),null,out var state);
    return state;
}
// Parameter identity survives persistence in the native action representation.
var saved=new ApplicationAction(plugin.Name,camera,Choices().Single(x=>x.Value==liveKey));
var restored=System.Text.Json.JsonSerializer.Deserialize<ApplicationAction>(
    System.Text.Json.JsonSerializer.Serialize(saved));
Check(new ActionString(restored.Name).ActionParameter==liveKey,"Saved action lost the chosen layer");
var catalogAction=new ApplicationAction(plugin.Name,camera);
Check(catalogAction.ProfileActionType=="list;Camera / capture layer","Catalog action lost its single-picker metadata");
await Until(()=>State(liveAction)=="active" && State(deskAction)=="inactive",
    "Initial native states must match each layer before an image callback or press");
using var subscription=new NativeImageSubscription(manager,plugin);
subscription.Subscribe(1,liveAction);
subscription.Subscribe(2,deskAction);
var shown=Render(liveAction);
var hidden=Render(deskAction);
var neutral=Render(Address(null));
Check(Hash(shown)!=Hash(hidden) && Hash(shown)!=Hash(neutral),"Native states returned identical camera graphics");
foreach(var size in new[]{50,80,116}) foreach(var (action,state) in
    new[]{(liveAction,"active"),(deskAction,"inactive")}) {
    var bytes=Render(action,size);
    Check(ActionIcon.TryReadFromFile(Path.Combine(root,"metadata/DefaultIconTemplate.ict"),out var iconLayout) &&
        !iconLayout.Items.OfType<ActionIconTextItem>().Any(x=>x.IsVisible),"Camera template must be icon-only");
    iconLayout.SetImage(File.ReadAllBytes(Path.Combine(root,"actionicons",
        $"Loupedeck.MeldMxKeypadPlugin.ToggleCameraCommand______{state}.png")));
    using var expected=ActionIconBuilder.CreateImage(size,size,iconLayout,ActionImageBuilderFlags.None);
    Check(Hash(bytes)==Hash(expected.ToArray()),"Standalone camera display contains a caption or has the wrong size");
    Check(Hash(bytes)==Hash(Render(action,size,"Toggle Camera")),"Host text customization leaked onto the camera");
    File.WriteAllBytes(Path.Combine(output,$"Camera-native-{state}-{size}.png"),bytes);
}
foreach(var state in new[]{"unknown","inactive","active"}) {
    Check(nativeCamera.TryGetCommandImage(liveKey,state,PluginImageSize.None,out var image),"State preview missing");
    using(image) Check(Hash(image.ToArray())==Hash(File.ReadAllBytes(Path.Combine(root,"actionicons",
        $"Loupedeck.MeldMxKeypadPlugin.ToggleCameraCommand______{state}.png"))),"State preview ignores explicit state");
}
bool Frame(int id,byte[] expected)=>subscription.Frames.Any(x=>x.Id==id && x.Image!=null && Hash(x.Image)==Hash(expected));
await Until(()=>Frame(1,shown) && Frame(2,hidden),"Native subscription did not render independently selected camera states");
var callsBefore=mock.Calls.Count;
var snapshotsBefore=mock.SnapshotRequests;
mock.SnapshotResponseGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
mock.PublishAfterCommand=false;
manager.ExecuteAction(new ActionString(liveAction),null,1);
await Until(()=>mock.Calls.Count>callsBefore && !mock.CameraVisible,"Native command queue did not toggle the camera");
await Until(()=>mock.SnapshotRequests>snapshotsBefore,"Press did not poll visibility");
Check(State(liveAction)=="active" && Hash(Render(liveAction))==Hash(shown),"Icon guessed visibility before Meld replied");
mock.SnapshotResponseGate.TrySetResult();
await Until(()=>State(liveAction)=="inactive" && Frame(1,hidden),"Polled state did not update the native image subscription");
Check(mock.Calls.Last()=="toggleLayer:[\"scene-0\",\"cam\"]","Press used incorrect native IDs");
Check(State(deskAction)=="inactive","Press changed another camera's icon");
mock.SnapshotResponseGate=null;
manager.ExecuteAction(new ActionString(liveAction),null,1);
await Until(()=>State(liveAction)=="active" && mock.CameraVisible,"Second queued press did not restore the camera");
mock.PublishAfterCommand=true;
subscription.Frames.Clear();
mock.CameraVisible=false;await mock.PublishAsync();
await Until(()=>State(liveAction)=="inactive" && Frame(1,hidden),"Direct Meld hide did not update native state and render");
mock.CameraVisible=true;await mock.PublishAsync();
await Until(()=>State(liveAction)=="active" && Frame(1,shown),"Direct Meld show did not update native state and render");
Check(State(deskAction)=="inactive","Direct changes leaked to another camera");
Console.WriteLine("PASS: the microphone-style native renderer and subscription change camera icons after queued presses and direct Meld updates; no captions.");
mock.IgnoreCameraToggle=true;mock.PublishAfterCommand=false;
snapshotsBefore=mock.SnapshotRequests;
manager.ExecuteAction(new ActionString(liveAction),null,1);
await Task.Delay(1800);
var snapshotsAfter=mock.SnapshotRequests;
Check(snapshotsAfter>snapshotsBefore && snapshotsAfter-snapshotsBefore<=10,"Camera polling must be bounded");
await Task.Delay(300);
Check(mock.SnapshotRequests==snapshotsAfter && State(liveAction)=="active","No-change reply guessed a state or kept polling");
mock.IgnoreCameraToggle=false;mock.PublishAfterCommand=true;
mock.ExtraItems["cam"]=new{type="layer",name=mock.CameraName,index=4,parent="scene-0",visible=true};
mock.CameraScene=mock.CurrentScene=1;mock.CameraId="next-scene-camera";mock.CameraIndex=7;mock.CameraVisible=false;
await mock.PublishAsync();
await Until(()=>State(liveAction)=="inactive" && State(deskAction)=="unknown","Saved selection did not follow the current scene");
Check(Choices().Single(x=>x.DisplayName==mock.CameraName).Value.EndsWith(":7"),"Picker retained an old-scene layer");
manager.ExecuteAction(new ActionString(liveAction),null,1);
await Until(()=>State(liveAction)=="active","Saved selection failed in the next scene");
Check(mock.Calls.Last()=="toggleLayer:[\"scene-1\",\"next-scene-camera\"]","Scene change used stale command IDs");
mock.CurrentScene=2;await mock.PublishAsync();
await Until(()=>State(liveAction)=="unknown","Absent layer should have neutral state");
callsBefore=mock.Calls.Count;
manager.ExecuteAction(new ActionString(liveAction),null,1);
await Task.Delay(150);
Check(mock.Calls.Count==callsBefore,"Absent camera sent a toggle");
await mock.DisconnectAsync();
await Until(()=>State(liveAction)=="unknown" && Hash(Render(liveAction))==Hash(neutral),"Disconnect retained a live icon");
mock.CameraScene=mock.CurrentScene=0;mock.CameraVisible=true;mock.CameraId="cam";mock.CameraIndex=4;
mock.ExtraItems.Remove("cam");
await Until(()=>mock.Connections>=2,"Camera did not reconnect");
await mock.PublishAsync();
await Until(()=>State(liveAction)=="active" && State(deskAction)=="inactive","Reconnect did not restore independent camera states");
Check(subscription.Errors.IsEmpty,"Native image subscription failed");
commandQueue.Stop();
plugin.Unload();
Call(scene,"OnUnload");Call(camera,"OnUnload");
foreach(var command in commands.OfType<PluginMultistateDynamicCommand>()) Call(command,"OnUnload");
Console.WriteLine("PASS: one populated list picker, independent camera selections, scene changes, bounded polling and reconnection.");
Console.WriteLine($"PASS: {checks} native host checks against {host.GetName().Version}.");
Console.WriteLine("Actual Options+ frontend, real Meld and physical keypad: NOT TESTED.");

sealed class TestLocalization : IPluginLocalizationEngineCallbacks {
    public event EventHandler<LanguageChangedEventArgs> LanguageChanged { add {} remove {} }
    public event EventHandler<LanguageChangedEventArgs> LoupedeckLanguageChanged { add {} remove {} }
    public bool RequestPluginLanguageChange(string pluginName,string language) => false;
    public string[] GetSupportedLanguages(string pluginName) => new[]{"en"};
    public string GetPluginLanguage(string pluginName) => "en";
    public System.Globalization.CultureInfo GetPluginCultureInfo(string pluginName) => System.Globalization.CultureInfo.InvariantCulture;
    public string GetLoupedeckLanguage() => "en";
    public System.Globalization.CultureInfo GetLoupedeckCultureInfo() => System.Globalization.CultureInfo.InvariantCulture;
    public string GetSystemLanguage() => "en";
    public bool TryGetString(string pluginName,string key,out string value) { value=null;return false; }
}

public class NoOpNativeApi : DispatchProxy {
    protected override object Invoke(MethodInfo method,object[] args) =>
        method.ReturnType==typeof(void) || !method.ReturnType.IsValueType ? null : Activator.CreateInstance(method.ReturnType);
}
