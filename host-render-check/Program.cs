using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Loupedeck;
using Loupedeck.Service;
using Loupedeck.MeldStudioControlsPlugin;
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
string PixelHash(byte[] data) {
    using var bitmap=SKBitmap.Decode(data);
    return $"{bitmap.Width}x{bitmap.Height}:"+Hash(bitmap.Bytes);
}
var checks = 0;
void Check(bool pass, string message) { if (!pass) throw new Exception(message); Interlocked.Increment(ref checks); }
var expectedHostVersion = Environment.GetEnvironmentVariable("MELD_EXPECTED_HOST_VERSION");
foreach (var assembly in new[] { host, typeof(Plugin).Assembly, Assembly.Load("LoupedeckShared") }.Distinct()) {
    var version = assembly.GetName().Version!.ToString();
    Console.WriteLine($"Host assembly: {assembly.GetName().Name} {version} SHA256={Hash(File.ReadAllBytes(assembly.Location))}");
    if (!String.IsNullOrWhiteSpace(expectedHostVersion))
        Check(version == expectedHostVersion, $"Wrong host assembly: expected {expectedHostVersion}, loaded {assembly.GetName()}");
}
var plugin = new MeldStudioControlsPlugin();
// Plugin.Initialize normally owns this queue. The isolated host harness
// supplies that part of initialization and runs the real dispatcher.
var processQueue = typeof(Plugin).GetMethod("ProcessCommandQueue", members)!;
using var commandQueue = new PluginCommandQueue(plugin.Name,
    (Action)Delegate.CreateDelegate(typeof(Action), plugin, processQueue), 100);
typeof(Plugin).GetFields(members).Single(f => f.FieldType == typeof(PluginCommandQueue))
    .SetValue(plugin, commandQueue);
typeof(Plugin).GetProperty("Localization")!.SetValue(plugin,new PluginLocalizationEngine(plugin.Name,new TestLocalization()));
Check(Hash(File.ReadAllBytes(Path.Combine(root, "bin/MeldStudioControlsPlugin.dll"))) ==
    Hash(File.ReadAllBytes(typeof(MeldStudioControlsPlugin).Assembly.Location)), "Tested assembly differs from package");
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
var camera = (PluginAction)Activator.CreateInstance(typeof(MeldStudioControlsPlugin).Assembly.GetType("Loupedeck.MeldStudioControlsPlugin.ToggleCameraCommand")!);
// Use native profile serialization under an isolated Applications directory.
var applicationsDirectory=Path.Combine(output,"Applications");
var cameraProfileFile=Path.Combine(applicationsDirectory,"Loupedeck70","System","Profiles","CameraCheck","ProfileInfo.json");
Directory.CreateDirectory(Path.GetDirectoryName(cameraProfileFile)!);
var profileBridge=camera.GetType().GetField("_profiles",members);
if(profileBridge!=null) profileBridge.SetValue(camera,Activator.CreateInstance(profileBridge.FieldType,
    members,null,new object[]{applicationsDirectory},null));
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
var profile = new ApplicationProfile { DeviceType = DeviceType.Loupedeck70 };
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
mock.CurrentScene=0;mock.CameraName="OBSBOT Meet 2 StreamCamera";mock.GroupedCamera=true;
mock.ExtraItems["desk-camera"]=new{type="layer",name="Desk Camera",index=3,parent="scene-0",visible=false};
await mock.PublishAsync();
PluginActionParameter[] Choices() {
    if (camera is PluginMultistateDynamicCommand legacy) {
        Check(legacy.TryGetParameters(out var choices),"Legacy camera picker request failed");
        return choices;
    }
    var editor = ((ActionEditorCommand)camera).ActionEditor;
    var state = new ActionEditorState(new[]{new ActionEditorControlState{Name="Layer",Value=""}});
    var result = (ActionEditorListboxItemsRequestedEventArgs)Call(editor,"InvokeListboxItemsRequestedEvent",state,"Layer");
    return result.Items.Select(x=>new PluginActionParameter(camera,x.Name,x.DisplayName,x.Description,"Scenes")).ToArray();
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
// A list profile action is persisted under a generated alias, not the picker
// value. Exercise that native model: a direct ApplicationAction is insufficient.
if (Environment.GetEnvironmentVariable("MELD_REPRO_LEGACY_CAMERA") == "1") {
    var assigned = ApplicationAction.CreateLegacyProfileAction(plugin.Name, camera, liveKey,
        "Toggle Camera", null, "Scenes", null);
    profile.ProfileCommands.Add(assigned);
    string AssignedState() {
        manager.TryGetPluginActionCurrentStateName(new ActionString(assigned.Name), profile, out var value);
        return value;
    }
    byte[] AssignedImage() {
        using var image=ApplicationProfileImages.CreateActionImage(service,profile,null,assigned.Name,
            (PluginImageSize)116,BitmapRotation.None,ActionImageBuilderFlags.None);
        return image.ToArray();
    }
    var before=AssignedImage();
    File.WriteAllBytes(Path.Combine(output,"legacy-camera-before.png"),before);
    Console.WriteLine($"Saved camera: {assigned.Name}; selected layer: {assigned.LegacyProfileActionParameter}");
    Console.WriteLine($"BEFORE: raw layer state={State(liveAction)}, saved-button state={AssignedState()}");
    // ActionExecutor resolves LegacyProfileActionParameter before queuing the
    // press. Use that exact stored value, then inspect the saved button again.
    manager.ExecuteAction(new ActionString(plugin.Name,camera.Name,assigned.LegacyProfileActionParameter),null,1);
    await Until(()=>!mock.CameraVisible && State(liveAction)=="inactive","Camera toggle failed during alias reproduction");
    var after=AssignedImage();
    File.WriteAllBytes(Path.Combine(output,"legacy-camera-after.png"),after);
    Console.WriteLine($"AFTER: raw layer state={State(liveAction)}, saved-button state={AssignedState()}, image changed={Hash(before)!=Hash(after)}");
    Check(AssignedState()=="unknown" && Hash(before)==Hash(after),"Expected 0.4.14 saved-button regression was not reproduced");
    Console.WriteLine("REPRODUCED: native list-profile alias stays unknown and its image is unchanged although the layer toggles.");
    commandQueue.Stop(); plugin.Unload(); Call(camera,"OnUnload");
    return;
}
if (Environment.GetEnvironmentVariable("MELD_REPRO_EDITOR_CAMERA") == "1") {
    Check(camera is MultistateActionEditorCommand,"Expected the 0.4.15 editor camera");
    var editorParameters=new ActionEditorActionParameters(new Dictionary<string,string>{{"Layer",liveKey}});
    var editorAddress=new ActionString(plugin.Name,camera.Name,null).ToString();
    var assigned=new ApplicationProfileCommand(camera,editorAddress,null,null,null,null,editorParameters);
    profile.ProfileActions.Add(assigned);
    string EditorState() {
        manager.TryGetPluginActionCurrentStateName(new ActionString(assigned.Name),profile,out var value); return value;
    }
    byte[] EditorSubscriptionImage() {
        using var image=(BitmapImage)render.Invoke(null,new object[]{manager,editorAddress,editorParameters.Parameters,
            null,null,new BitmapImageSize(116,116),BitmapRotation.None,ActionImageBuilderFlags.None,BitmapImageFormat.Png});
        return image.ToArray();
    }
    await Until(()=>EditorState()=="active","0.4.15 profile state was not active");
    var before=EditorSubscriptionImage();
    manager.ExecuteAction(new ActionString(editorAddress),editorParameters,1);
    await Until(()=>!mock.CameraVisible && EditorState()=="inactive","0.4.15 toggle/profile-state reproduction failed");
    var after=EditorSubscriptionImage();
    Check(PixelHash(before)==PixelHash(after),"Expected 0.4.15 standalone renderer failure was not reproduced");
    File.WriteAllBytes(Path.Combine(output,"editor-camera-before.png"),before);
    File.WriteAllBytes(Path.Combine(output,"editor-camera-after.png"),after);
    Console.WriteLine("REPRODUCED: 0.4.15 saved profile state changes active -> inactive, but the native standalone renderer ignores the selected editor parameters and its image is unchanged.");
    commandQueue.Stop();plugin.Unload();Call(scene,"OnUnload");Call(camera,"OnUnload"); return;
}
Check(camera is PluginMultistateDynamicCommand && !camera.HasActionEditor && camera.IsProfileAction && !camera.HasParameter &&
    camera.ProfileActionType=="list;Camera / capture layer",
    "Camera must use the microphone's native multistate path with one list picker");
var templateAction=new ActionString(plugin.Name,camera.Name,null).ToString();
void PersistProfile() => File.WriteAllText(cameraProfileFile,JsonHelpers.SerializeAnyObject(profile));
ApplicationAction SaveCamera(string key) {
    var created=ApplicationAction.CreateLegacyProfileAction(plugin.Name,camera,key,"Toggle Camera",null,"Scenes",null);
    var restored=System.Text.Json.JsonSerializer.Deserialize<ApplicationAction>(
        System.Text.Json.JsonSerializer.Serialize(created));
    Check(restored.LegacyProfileActionParameter==key && new ActionString(restored.Name).ActionParameter!=key,
        "Must test a saved alias, not a direct layer key");
    profile.ProfileCommands.Add(restored); PersistProfile(); return restored;
}
var live=SaveCamera(liveKey); var desk=SaveCamera(deskKey); var missing=SaveCamera("missing");
// Compare the configuration handler's editable defaults with the working mic.
// The live button/subscription renderer is checked independently below.
var configurationHandler=(ConfigurationWindowMessageHandler)RuntimeHelpers.GetUninitializedObject(typeof(ConfigurationWindowMessageHandler));
for(var type=configurationHandler.GetType();type!=null;type=type.BaseType)
    foreach(var field in type.GetFields(members | BindingFlags.DeclaredOnly))
        if(field.FieldType==typeof(LoupedeckService)) field.SetValue(configurationHandler,service);
ActionIcon ConfiguredIcon(string action) {
    var arguments=new object[]{profile,action,(PluginImageSize)116,null};
    Check((bool)typeof(ConfigurationWindowMessageHandler).GetMethod("TryGetActionIcon",members)!
        .Invoke(configurationHandler,arguments),"Configuration handler returned no saved-camera icon");
    return (ActionIcon)arguments[3];
}
var configuredIcon=ConfiguredIcon(live.Name);
using(var configuredImage=ActionIconBuilder.CreateImage(116,116,configuredIcon,ActionImageBuilderFlags.None))
    File.WriteAllBytes(Path.Combine(output,"Camera-configuration-handler.png"),configuredImage.ToArray());
// The editable default is not the live button renderer: even the working
// microphone gets a caption here. Check the native multistate route, then
// actual subscription and saved-profile pixels instead of this editor preview.
var micDefault=ConfiguredIcon(new ActionString(plugin.Name,commands[2].Name,null).ToString());
Check(micDefault.Items.OfType<ActionIconTextItem>().Any(x=>x.IsVisible && !String.IsNullOrEmpty(x.Text)),
    "Expected native editor default for the working microphone");
var multiIcons=new object[]{profile,live.Name,(PluginImageSize)116,false,null};
Check((bool)typeof(ConfigurationWindowMessageHandler).GetMethod("TryGetMultiStateActionIcons",members)!
    .Invoke(configurationHandler,multiIcons),"Camera must expose the native multi-icon route used by the microphone");
Check(((Dictionary<string,ActionIcon>)multiIcons[4]).Count==3,"Camera state metadata missing");
string SavedState(ApplicationAction assigned) {
    manager.TryGetPluginActionCurrentStateName(new ActionString(assigned.Name),profile,out var state); return state;
}
BitmapImage ProfileImage(ApplicationAction assigned,int size=116) =>
    ApplicationProfileImages.CreateActionImage(service,profile,null,assigned.Name,
        (PluginImageSize)size,BitmapRotation.None,ActionImageBuilderFlags.None);
byte[] SavedImage(ApplicationAction assigned,int size=116) {
    using var image=ProfileImage(assigned,size);
    Check(image!=null,"Saved camera image missing"); return image.ToArray();
}
void Press(ApplicationAction assigned) => manager.ExecuteAction(new ActionString(assigned.Name),null,1);
await Until(()=>SavedState(live)=="active" && SavedState(desk)=="inactive","Initial saved states must match selected layers");
Console.WriteLine($"Saved camera: {live.Name}; selected layer: {live.LegacyProfileActionParameter}");
Console.WriteLine($"BEFORE: saved-button state={SavedState(live)}");
// Use the unmodified native subscription renderer with actual saved aliases.
// Do not substitute the saved-profile renderer for this path.
using var subscription=new NativeImageSubscription(manager,plugin);
subscription.Subscribe(1,live.Name); subscription.Subscribe(2,desk.Name);
var shown=SavedImage(live); var hidden=SavedImage(desk); var neutral=SavedImage(missing);
Check(Hash(shown)!=Hash(hidden) && Hash(shown)!=Hash(neutral),"Native states returned identical camera graphics");
foreach(var size in new[]{50,80,116}) foreach(var (action,state) in
    new[]{(live,"active"),(desk,"inactive"),(missing,"unknown")}) {
    var bytes=SavedImage(action,size);
    Check(ActionIcon.TryReadFromFile(Path.Combine(root,"metadata/DefaultIconTemplate.ict"),out var iconLayout) &&
        !iconLayout.Items.OfType<ActionIconTextItem>().Any(x=>x.IsVisible),"Camera template must be icon-only");
    iconLayout.SetImage(File.ReadAllBytes(Path.Combine(root,"actionicons",
        $"Loupedeck.MeldStudioControlsPlugin.ToggleCameraCommand______{state}.png")));
    using var expectedLayout=ActionIconBuilder.CreateImage((PluginImageSize)size,iconLayout,ActionImageBuilderFlags.None);
    // Compare the same native pixel format after laying out the full-size asset.
    using var expected=ActionImageBuilder.GetBitmapImage(expectedLayout,action.Name,null,(PluginImageSize)size,
        BitmapRotation.None,BitmapColor.White,BitmapColor.Black,ActionImageBuilderFlags.None).ToImage();
    File.WriteAllBytes(Path.Combine(output,$"Camera-native-{state}-{size}.png"),bytes);
    File.WriteAllBytes(Path.Combine(output,$"Camera-expected-{state}-{size}.png"),expected.ToArray());
    Check(PixelHash(bytes)==PixelHash(expected.ToArray()),"Saved camera display contains a caption or has the wrong size");
    Check(PixelHash(Render(action.Name,size))==PixelHash(expectedLayout.ToArray()),
        "Standalone Options+ renderer lost the saved camera selection or layout");
    var savedCaption=action.DisplayName;
    action.DisplayName="THIS MUST NEVER APPEAR";
    Check(PixelHash(bytes)==PixelHash(SavedImage(action,size)),"Saved profile caption leaked onto camera");
    action.DisplayName=savedCaption;
    Check(PixelHash(expectedLayout.ToArray())==PixelHash(Render(new ActionString(plugin.Name,camera.Name,null,state).ToString(),size,"Toggle Camera")),
        "Explicit-state standalone preview changed layout");
}
foreach(var state in new[]{"unknown","inactive","active"}) {
    Check(((PluginMultistateDynamicCommand)camera).TryGetCommandImage(new ActionString(live.Name).ActionParameter,state,PluginImageSize.None,out var image),"State preview missing");
    using(image) Check(Hash(image.ToArray())==Hash(File.ReadAllBytes(Path.Combine(root,"actionicons",
        $"Loupedeck.MeldStudioControlsPlugin.ToggleCameraCommand______{state}.png"))),"State preview ignores explicit state");
}
var shownFrame=Render(live.Name); var hiddenFrame=Render(desk.Name);
bool Frame(int id,byte[] expected)=>subscription.Frames.Any(x=>x.Id==id && x.Image!=null &&
    PixelHash(x.Image)==PixelHash(ReferenceEquals(expected,shown)?shownFrame:ReferenceEquals(expected,hidden)?hiddenFrame:expected));
await Until(()=>Frame(1,shown) && Frame(2,hidden),"Native subscription did not render independently selected camera states");
subscription.Frames.Clear();
var callsBefore=mock.Calls.Count;
var snapshotsBefore=mock.SnapshotRequests;
mock.SnapshotResponseGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
mock.PublishAfterCommand=false;
Press(live);
await Until(()=>mock.Calls.Count>callsBefore && !mock.CameraVisible,"Native command queue did not toggle the camera");
await Until(()=>mock.SnapshotRequests>snapshotsBefore,"Press did not poll visibility");
Console.WriteLine($"Awaiting Meld: state={SavedState(live)}, pixels unchanged={PixelHash(SavedImage(live))==PixelHash(shown)}");
Check(SavedState(live)=="active" && PixelHash(SavedImage(live))==PixelHash(shown),"Icon guessed visibility before Meld replied");
mock.SnapshotResponseGate.TrySetResult();
await Until(()=>SavedState(live)=="inactive" && Frame(1,hidden),"Polled state did not update the native image subscription");
Check(mock.Calls.Last()=="toggleLayer:[\"scene-0\",\"cam\"]","Press used incorrect native IDs");
Check(SavedState(desk)=="inactive","Press changed another camera's icon");
Console.WriteLine($"AFTER: saved-button state={SavedState(live)}, image changed={Hash(shown)!=Hash(SavedImage(live))}");
mock.SnapshotResponseGate=null;
subscription.Frames.Clear();
Press(live);
await Until(()=>SavedState(live)=="active" && mock.CameraVisible && Frame(1,shown),"Second queued press did not restore the camera");
mock.PublishAfterCommand=true;
subscription.Frames.Clear();
mock.CameraVisible=false;await mock.PublishAsync();
await Until(()=>SavedState(live)=="inactive" && Frame(1,hidden),"Direct Meld hide did not update native state and render");
subscription.Frames.Clear();
mock.CameraVisible=true;await mock.PublishAsync();
await Until(()=>SavedState(live)=="active" && Frame(1,shown),"Direct Meld show did not update native state and render");
Check(SavedState(desk)=="inactive","Direct changes leaked to another camera");
Console.WriteLine("PASS: saved profile rendering and native invalidations change camera icons after queued presses and direct Meld updates; no captions.");
mock.IgnoreCameraToggle=true;mock.PublishAfterCommand=false;
snapshotsBefore=mock.SnapshotRequests;
Press(live);
await Task.Delay(1800);
var snapshotsAfter=mock.SnapshotRequests;
Check(snapshotsAfter>snapshotsBefore && snapshotsAfter-snapshotsBefore<=10,"Camera polling must be bounded");
await Task.Delay(300);
Check(mock.SnapshotRequests==snapshotsAfter && SavedState(live)=="active","No-change reply guessed a state or kept polling");
mock.IgnoreCameraToggle=false;mock.PublishAfterCommand=true;
mock.ExtraItems["cam"]=new{type="layer",name=mock.CameraName,index=4,parent="scene-0",visible=true};
mock.CameraScene=mock.CurrentScene=1;mock.CameraId="next-scene-camera";mock.CameraIndex=7;mock.CameraVisible=false;
await mock.PublishAsync();
await Until(()=>SavedState(live)=="inactive" && SavedState(desk)=="unknown","Saved selection did not follow the current scene");
Check(Choices().Single(x=>x.DisplayName==mock.CameraName).Value.EndsWith(":7"),"Picker retained an old-scene layer");
Press(live);
await Until(()=>SavedState(live)=="active","Saved selection failed in the next scene");
Check(mock.Calls.Last()=="toggleLayer:[\"scene-1\",\"next-scene-camera\"]","Scene change used stale command IDs");
mock.CurrentScene=2;await mock.PublishAsync();
await Until(()=>SavedState(live)=="unknown","Absent layer should have neutral state");
callsBefore=mock.Calls.Count;
Press(live);
await Task.Delay(150);
Check(mock.Calls.Count==callsBefore,"Absent camera sent a toggle");
await mock.DisconnectAsync();
await Until(()=>SavedState(live)=="unknown" && PixelHash(SavedImage(live))==PixelHash(neutral),"Disconnect retained a live icon");
mock.CameraScene=mock.CurrentScene=0;mock.CameraVisible=true;mock.CameraId="cam";mock.CameraIndex=4;
mock.ExtraItems.Remove("cam");
await Until(()=>mock.Connections>=2,"Camera did not reconnect");
await mock.PublishAsync();
await Until(()=>SavedState(live)=="active" && SavedState(desk)=="inactive","Reconnect did not restore independent camera states");
// Editing the selection must refresh the saved alias without a press or a
// Meld visibility event. This also covers atomic saves, bad partial JSON,
// duplicate profiles and deletion; all files are isolated test fixtures.
subscription.Frames.Clear();
live.LegacyProfileActionParameter=deskKey; PersistProfile();
await Until(()=>SavedState(live)=="inactive" && Frame(1,hidden),"Edited picker selection kept the old camera state");
live.LegacyProfileActionParameter=liveKey;
var replacement=cameraProfileFile+".tmp";
File.WriteAllText(replacement,JsonHelpers.SerializeAnyObject(profile));
File.Move(replacement,cameraProfileFile,true);
await Until(()=>SavedState(live)=="active","Atomic profile save was not picked up");
var profileHash=Hash(File.ReadAllBytes(cameraProfileFile));
Call(camera,"OnUnload"); Call(camera,"OnLoad");
await Until(()=>SavedState(live)=="active","Saved aliases were not restored on plugin reload");
Check(Hash(File.ReadAllBytes(cameraProfileFile))==profileHash,"Plugin changed a user profile");
File.WriteAllText(cameraProfileFile,"{");
await Until(()=>SavedState(live)=="unknown","Partial profile save retained stale selection");
File.WriteAllText(cameraProfileFile,"[]");
await Task.Delay(650);
Check(SavedState(live)=="unknown","Invalid profile root must be unavailable");
PersistProfile();
await Until(()=>SavedState(live)=="active","Completed profile save did not recover");
var duplicate=Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(cameraProfileFile)!)!,"ConflictingCopy","ProfileInfo.json");
Directory.CreateDirectory(Path.GetDirectoryName(duplicate)!);
var conflicting=System.Text.Json.JsonSerializer.Deserialize<ApplicationAction>(System.Text.Json.JsonSerializer.Serialize(live))!;
conflicting.LegacyProfileActionParameter=deskKey;
var copiedProfile=new ApplicationProfile {DeviceType=DeviceType.Loupedeck70};
copiedProfile.ProfileCommands.Add(conflicting);
File.WriteAllText(duplicate,JsonHelpers.SerializeAnyObject(copiedProfile));
await Until(()=>SavedState(live)=="unknown","Conflicting saved aliases must not choose an arbitrary camera");
File.Delete(duplicate);
await Until(()=>SavedState(live)=="active","Removing a conflicting copy did not recover state");
File.Delete(cameraProfileFile);
await Until(()=>SavedState(live)=="unknown","Deleted profile kept stale alias state");
PersistProfile();
await Until(()=>SavedState(live)=="active" && SavedState(desk)=="inactive","Recreated profile did not restore independent state");
Console.WriteLine("PASS: saved selection edits, plugin reload, atomic/partial saves, conflicting copies and deletion; profile bytes unchanged by plugin.");
Check(subscription.Errors.IsEmpty,"Native image subscription failed");
commandQueue.Stop();
plugin.Unload();
Call(scene,"OnUnload");Call(camera,"OnUnload");
foreach(var command in commands.OfType<PluginMultistateDynamicCommand>()) Call(command,"OnUnload");
Console.WriteLine("PASS: one populated picker and saved parameter persistence, independent camera selections, scene changes, bounded polling and reconnection.");
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
