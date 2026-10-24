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
// args: package-directory output-directory
// MELD_REPRO_SCENE_0418=1 runs only the original standalone-render negative control.
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
var scene = new ShowSceneCommand();
var applicationsDirectory=Path.Combine(output,"Applications");
typeof(Plugin).GetProperty("NativeApi")!.SetValue(plugin,DispatchProxy.Create<INativeApi,NoOpNativeApi>());
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

Call(plugin.PluginPackage,"Init",root);
defaultTemplate.SetValue(wrapper,File.ReadAllText(Path.Combine(root,"metadata/DefaultIconTemplate.ict")));
var service=(LoupedeckService)RuntimeHelpers.GetUninitializedObject(typeof(LoupedeckService));
typeof(LoupedeckService).GetFields(members).Single(f=>f.FieldType==typeof(PluginManager)).SetValue(service,manager);
typeof(PluginManager).GetFields(members).Single(f=>f.FieldType==typeof(LoupedeckService)).SetValue(manager,service);
var profile=new ApplicationProfile{DeviceType=DeviceType.Loupedeck70};
var profileDirectory=Path.Combine(output,"profile");Directory.CreateDirectory(profileDirectory);
typeof(ApplicationProfile).GetFields(members|BindingFlags.DeclaredOnly).Single(f=>f.FieldType==typeof(string) &&
    !f.IsDefined(typeof(CompilerGeneratedAttribute),false)).SetValue(profile,profileDirectory);
var callbackType=typeof(Plugin).GetNestedTypes(BindingFlags.NonPublic).Single(t=>t.GetInterfaces().Any(i=>i.GetMethods().Any(m=>m.Name=="ListboxItemsChanged")));
typeof(ActionEditorAction).GetMethod("Initialize",members)!.Invoke(scene,new[]{(object)plugin,Activator.CreateInstance(callbackType,plugin)});
Call(scene,"OnLoad");
async Task Until(Func<bool> condition,string message){
    for(var i=0;i<100;i++){if(condition()){Interlocked.Increment(ref checks);return;}await Task.Delay(50);}
    throw new Exception(message);
}
var activeColor=new SKColor(0xc4,0xe5,0xf5);var inactiveColor=new SKColor(0x11,0x18,0x23);
SKColor Background(byte[] bytes){using var b=SKBitmap.Decode(bytes);return b.GetPixel(0,0);}
var shortKey=Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("VALORANT"))+":0";
var longKey=Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("SYNAPSE // STARTING SOON"))+":1";
ActionEditorActionParameters OldParameters(string key)=>new(new Dictionary<string,string>{{"Scene",key}});
byte[] OldStandalone(string key){
    using var image=(BitmapImage)render.Invoke(null,new object[]{manager,new ActionString(plugin.Name,scene.Name,null).ToString(),
        new Dictionary<string,string>{{"Scene",key}},null,null,new BitmapImageSize(116,116),BitmapRotation.None,ActionImageBuilderFlags.None,BitmapImageFormat.Png});
    Check(image!=null,"Original standalone scene image missing");return image.ToArray();
}
byte[] OldSavedProfile(string key){
    var assigned=new ApplicationProfileCommand(scene,new ActionString(plugin.Name,scene.Name,null).ToString(),null,null,null,null,OldParameters(key));
    profile.ProfileActions.Add(assigned);
    using var image=ApplicationProfileImages.CreateActionImage(service,profile,null,assigned.Name,(PluginImageSize)116,BitmapRotation.None,ActionImageBuilderFlags.None);
    Check(image!=null,"Original saved scene image missing");return image.ToArray();
}
await using var mock=new MockMeld{GroupedCamera=false};mock.Start();plugin.Load();
await mock.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));await mock.PublishAsync();
await Until(()=>Background(OldSavedProfile(shortKey))==activeColor,"Initial original saved scene did not become active");
var oldShort=OldStandalone(shortKey);var oldLong=OldStandalone(longKey);
Check(PixelHash(oldShort)==PixelHash(oldLong),"Expected original standalone renderer to lose distinct editor selections");
mock.CurrentScene=1;await mock.PublishAsync();
await Until(()=>Background(OldSavedProfile(shortKey))==inactiveColor,"Original profile renderer did not observe scene switch");
Check(PixelHash(oldShort)==PixelHash(OldStandalone(shortKey)) && PixelHash(oldLong)==PixelHash(OldStandalone(longKey)),
    "Expected original standalone image to remain unchanged after scene switch");
File.WriteAllBytes(Path.Combine(output,"old-scene-standalone-unchanged.png"),oldShort);
Console.WriteLine("REPRODUCED: original Show Scene saved-profile colors change, but native standalone images ignore distinct Scene parameters and remain unchanged.");
if(Environment.GetEnvironmentVariable("MELD_REPRO_SCENE_0418")=="1"){
    commandQueue.Stop();plugin.Unload();Call(scene,"OnUnload");
    Console.WriteLine("PASS: 0.4.18 selected-scene standalone regression reproduced.");return;
}
var buttonType=typeof(MeldStudioControlsPlugin).Assembly.GetType("Loupedeck.MeldStudioControlsPlugin.ShowSceneButtonCommand")!;
Check(buttonType!=null,"New scene button action missing");
var button=(PluginMultistateDynamicCommand)Activator.CreateInstance(buttonType)!;
var bridge=buttonType.GetField("_profiles",members)!;
bridge.SetValue(button,Activator.CreateInstance(bridge.FieldType,members,null,new object[]{applicationsDirectory},null));
var init=typeof(PluginDynamicAction).GetMethod("Init",members)!;
var ctor=init.GetParameters()[2].ParameterType.GetConstructors(members).Single();
var callbackNames=new[]{"PluginActionsChanged","OnActionImageChanged","OnAdjustmentValueChanged","OnActionStateChanged"};
var nativeCallbacks=ctor.Invoke(ctor.GetParameters().Select((p,i)=>{
    var signature=p.ParameterType.GetMethod("Invoke")!.GetParameters().Select(x=>x.ParameterType).ToArray();
    var method=typeof(Plugin).GetMethods(members).Single(m=>m.Name==callbackNames[i] && m.GetParameters().Select(x=>x.ParameterType).SequenceEqual(signature));
    return (object)Delegate.CreateDelegate(p.ParameterType,plugin,method);
}).ToArray());
init.Invoke(button,new[]{(object)plugin,plugin.NativeApi,nativeCallbacks});
Call(plugin.DynamicCommands,"AddAction",button);plugin.AddAction(button,false);
Check(button.Load(),"Scene button lifecycle failed");
Check(button.IsProfileAction && !button.HasActionEditor && !button.HasParameter && button.ProfileActionType=="list;Meld scene",
    "New Show Scene must have exactly one native list picker");
Check(scene.IsHidden && !button.IsHidden,"Only the fixed scene action should be visible in action list");
Check(button.TryGetParameters(out var choices) && choices.Length==14,"Scene picker should list every scene");
Check(choices.Single(x=>x.DisplayName=="VALORANT").Value==shortKey,"Scene picker changed persisted key");
var profileFile=Path.Combine(applicationsDirectory,"Loupedeck70","System","Profiles","SceneCheck","ProfileInfo.json");
Directory.CreateDirectory(Path.GetDirectoryName(profileFile)!);
void Persist()=>File.WriteAllText(profileFile,JsonHelpers.SerializeAnyObject(profile));
ApplicationAction Save(string key,string name){
    var created=ApplicationAction.CreateLegacyProfileAction(plugin.Name,button,key,name,null,"Scenes",null);
    var saved=System.Text.Json.JsonSerializer.Deserialize<ApplicationAction>(System.Text.Json.JsonSerializer.Serialize(created))!;
    Check(saved.LegacyProfileActionParameter==key && new ActionString(saved.Name).ActionParameter!=key,"Test must retain native generated aliases");
    profile.ProfileCommands.Add(saved);Persist();return saved;
}
var shortScene=Save(shortKey,"VALORANT");var longScene=Save(longKey,"SYNAPSE // STARTING SOON");
var duplicateShort=Save(shortKey,"VALORANT duplicate button");var missing=Save("missing","Missing scene");
string State(ApplicationAction a){manager.TryGetPluginActionCurrentStateName(new ActionString(a.Name),profile,out var value);return value;}
byte[] SavedImage(ApplicationAction a,int size=116){
    using var image=ApplicationProfileImages.CreateActionImage(service,profile,null,a.Name,(PluginImageSize)size,BitmapRotation.None,ActionImageBuilderFlags.None);
    Check(image!=null,"Saved scene image missing");return image.ToArray();
}
void Press(ApplicationAction a)=>manager.ExecuteAction(new ActionString(a.Name),null,1);
await Until(()=>State(shortScene)=="inactive" && State(longScene)=="active" && State(duplicateShort)=="inactive" && State(missing)=="unknown",
    "Cold saved scene aliases did not resolve independently");
// Options+ requests this native three-icon metadata for multistate actions.
var configurationHandler=(ConfigurationWindowMessageHandler)RuntimeHelpers.GetUninitializedObject(typeof(ConfigurationWindowMessageHandler));
for(var type=configurationHandler.GetType();type!=null;type=type.BaseType)
    foreach(var field in type.GetFields(members|BindingFlags.DeclaredOnly))
        if(field.FieldType==typeof(LoupedeckService))field.SetValue(configurationHandler,service);
var multiIcons=new object[]{profile,shortScene.Name,(PluginImageSize)116,false,null};
Check((bool)typeof(ConfigurationWindowMessageHandler).GetMethod("TryGetMultiStateActionIcons",members)!
    .Invoke(configurationHandler,multiIcons),"Scene button must expose the native multi-icon route");
var stateIcons=(Dictionary<string,ActionIcon>)multiIcons[4];
Check(stateIcons.Keys.OrderBy(x=>x).SequenceEqual(new[]{"active","inactive","unknown"}),"Native scene state icon metadata is incomplete");
var savedAlias=new ActionString(shortScene.Name).ActionParameter;
foreach(var state in new[]{"unknown","inactive","active"}){
    var explicitAction=new ActionString(plugin.Name,button.Name,savedAlias,state).ToString();
    var preview=Render(explicitAction);
    using var image=SKBitmap.Decode(preview);
    Check(Background(preview)==(state=="active"?activeColor:inactiveColor),"Native scene preview ignores explicit state");
    Check(image.Pixels.Where((p,i)=>i/image.Width>image.Height*.54).Count(p=>state=="active"
        ?p.Red<80&&p.Green<100&&p.Blue<125:p.Red>220&&p.Green>220&&p.Blue>220)>90,"State preview lost its contrasting scene title");
    Check(PixelHash(preview)==PixelHash(Render(explicitAction,116,"MUST NOT APPEAR")),"Extra native caption leaked onto explicit scene state");
}
Check(State(shortScene)=="inactive","Explicit-state previews altered the actual selected scene state");
using var subscription=new NativeImageSubscription(manager,plugin);
subscription.Subscribe(1,shortScene.Name);subscription.Subscribe(2,longScene.Name);subscription.Subscribe(3,duplicateShort.Name);
bool Frame(int id,bool active)=>subscription.Frames.Any(x=>x.Id==id && x.Image!=null && Background(x.Image)==(active?activeColor:inactiveColor));
await Until(()=>Frame(1,false)&&Frame(2,true)&&Frame(3,false),"Initial native scene subscription ignored saved selections");
void VerifyRender(ApplicationAction a,string label,bool active,int size){
    var standalone=Render(a.Name,size);var saved=SavedImage(a,size);
    using var b=SKBitmap.Decode(standalone);
    Check(b.Width==size && b.Height==size && Background(standalone)==(active?activeColor:inactiveColor),"Wrong scene fill or dimensions");
    Check(PixelHash(standalone)==PixelHash(saved),"Standalone and saved-profile scene rendering disagree");
    Check(b.Pixels.Where((p,i)=>i/size>size*.54).Count(p=>active?p.Red<80&&p.Green<100&&p.Blue<125:p.Red>220&&p.Green>220&&p.Blue>220)>65,
        "Scene title lacks contrasting pixels");
    Check(b.Pixels.Skip((size-3)*size).All(p=>p==b.GetPixel(0,0)),"Scene title touches bottom edge");
    Check(PixelHash(standalone)==PixelHash(Render(a.Name,size,"MUST NOT APPEAR")),"Native extra caption leaks onto scene image");
    File.WriteAllBytes(Path.Combine(output,$"scene-{label}-{(active?"active":"inactive")}-{size}.png"),standalone);
}
foreach(var size in new[]{80,116}){VerifyRender(shortScene,"short",false,size);VerifyRender(longScene,"long",true,size);}
subscription.Frames.Clear();var before=mock.Calls.Count;
Press(shortScene);
await Until(()=>mock.Calls.Count>before && mock.CurrentScene==0 && State(shortScene)=="active" && State(longScene)=="inactive" && Frame(1,true)&&Frame(2,false)&&Frame(3,true),
    "Queued scene press did not update all native subscribed buttons");
Check(mock.Calls.Last()=="showScene:[\"scene-0\"]","Wrong scene command IDs");
foreach(var size in new[]{80,116}){VerifyRender(shortScene,"short",true,size);VerifyRender(longScene,"long",false,size);}
before=mock.Calls.Count;Press(shortScene);
await Until(()=>mock.Calls.Count>before,"Already-active scene press failed");
Check(mock.CurrentScene==0 && State(shortScene)=="active" && mock.Calls.Last()=="showScene:[\"scene-0\"]","Scene action must show the selected scene, never toggle it off");
subscription.Frames.Clear();Press(longScene);
await Until(()=>State(longScene)=="active"&&Frame(2,true)&&Frame(1,false),"Second scene press failed to clear previous highlight");
subscription.Frames.Clear();mock.CurrentScene=0;await mock.PublishAsync();
await Until(()=>State(shortScene)=="active"&&Frame(1,true)&&Frame(2,false)&&Frame(3,true),"Direct Meld scene switch failed to update all buttons");
Console.WriteLine("PASS: native queued presses and direct Meld changes update standalone scene subscriptions, titles, duplicate assignments, and previous highlights.");
// Selecting a scene must not change any streaming/audio/camera state.
Check(!mock.Streaming&&!mock.Recording&&!mock.Muted&&mock.CameraVisible,"Scene button changed unrelated state");
subscription.Frames.Clear();shortScene.LegacyProfileActionParameter=longKey;Persist();
await Until(()=>State(shortScene)=="inactive"&&Frame(1,false),"Saved scene picker edit retained old scene highlight");
shortScene.LegacyProfileActionParameter=shortKey;Persist();
await Until(()=>State(shortScene)=="active","Restored scene selection did not recover");
mock.IdPrefix="replacement-";await mock.PublishAsync();
await Until(()=>button.TryGetParameters(out var updated)&&updated.Single(x=>x.DisplayName=="VALORANT").Value!=shortKey,"Scene picker did not refresh replacement IDs");
Press(longScene);await Until(()=>mock.Calls.Last()=="showScene:[\"replacement-1\"]" && State(longScene)=="active","Scene selection used stale session IDs");
// Old assignments retain their original command handler.
Check((bool)Call(scene,"RunCommand",OldParameters(shortKey)),"Old Show Scene assignment stopped executing");
await Until(()=>mock.Calls.Last()=="showScene:[\"replacement-0\"]"&&mock.CurrentScene==0,"Old scene command changed functionality");
var calls=mock.Calls.Count;Press(missing);await Task.Delay(150);
Check(mock.Calls.Count==calls,"Missing scene sent a command");
await mock.DisconnectAsync();
await Until(()=>State(shortScene)=="unknown"&&State(longScene)=="unknown","Disconnected scene kept active highlight");
Check(Background(Render(shortScene.Name))==inactiveColor,"Disconnected scene must not retain baby-blue active fill");
await Until(()=>mock.Connections>=2,"Scene connection did not recover");
await mock.PublishAsync();
await Until(()=>State(shortScene)=="active"&&State(longScene)=="inactive","Scene states did not recover after reconnect");
var conflict=System.Text.Json.JsonSerializer.Deserialize<ApplicationAction>(System.Text.Json.JsonSerializer.Serialize(shortScene))!;
conflict.LegacyProfileActionParameter=longKey;profile.ProfileCommands.Add(conflict);Persist();
await Until(()=>State(shortScene)=="unknown","Conflicting aliases in one profile must remain unavailable");
calls=mock.Calls.Count;Press(shortScene);await Task.Delay(150);
Check(mock.Calls.Count==calls,"Conflicting scene aliases sent a command");
profile.ProfileCommands.Remove(conflict);Persist();
await Until(()=>State(shortScene)=="active","Removing same-profile alias conflict did not restore state");
var bytesBefore=File.ReadAllBytes(profileFile);Call(button,"OnUnload");Call(button,"OnLoad");
await Until(()=>State(shortScene)=="active","Saved scenes did not recover after plugin reload");
Check(File.ReadAllBytes(profileFile).SequenceEqual(bytesBefore),"Scene plugin modified a user profile");
Check(subscription.Errors.IsEmpty,"Native scene image subscription failed");
commandQueue.Stop();Call(button,"OnUnload");Call(scene,"OnUnload");plugin.Unload();
Console.WriteLine("PASS: scene picker edits, replacement IDs, legacy assignments, unavailable scenes, reconnect and reload; no unrelated control or profile changes.");
Console.WriteLine($"PASS: {checks} native scene checks against {host.GetName().Version}.");
Console.WriteLine("Physical Options+ frontend, real Meld, and keypad: NOT TESTED.");

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
