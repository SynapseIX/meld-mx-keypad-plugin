using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Loupedeck;
using Loupedeck.Service;

// Exercise the service's standalone multi-icon subscription dispatcher and queue.
// Replace only the final IPC transport with a frame collector. No device,
// daemon, user profile or binary socket is opened by this harness.
// This helper does not capture or prove the Options+ frontend's request shape.
internal sealed class NativeImageSubscription : IDisposable
{
    const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    readonly object _session;
    readonly object _queue;
    readonly Type _itemType;
    readonly PluginManager _manager;
    readonly Plugin _plugin;
    readonly EventHandler<PluginActionImageChangedEventArgs> _dispatch;
    readonly EventHandler<ActionImageChangedEventArgs> _image;
    readonly EventHandler<ActionStateChangedEventArgs> _state;
    public ConcurrentQueue<(int Id, byte[] Image)> Frames { get; } = new();
    public ConcurrentQueue<Exception> Errors { get; } = new();

    public NativeImageSubscription(PluginManager manager, Plugin plugin)
    {
        _manager = manager; _plugin = plugin;
        var host = typeof(PluginManager).Assembly;
        var sessionType = host.GetTypes().Single(t => t.GetMethod("Subscribe", Members) != null &&
            t.GetMethod("ProcessBinaryClientQueueItem", Members) != null);
        _session = RuntimeHelpers.GetUninitializedObject(sessionType);
        _itemType = sessionType.GetMethod("Subscribe", Members)!.GetParameters()[0].ParameterType.GetElementType()!;
        var render = host.GetTypes().SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Single(m => m.Name == "CreateActionImage" && m.GetParameters().Length == 5 && m.GetParameters()[1].ParameterType == _itemType);
        var queueField = sessionType.GetFields(Members).Single(f => f.FieldType.IsGenericType &&
            f.FieldType.Name.StartsWith("BackgroundQueue"));
        Action<object> collect = item => {
            try {
                using var image = (BitmapImage)render.Invoke(null,new[]{manager,item,(object)BitmapRotation.None,
                    ActionImageBuilderFlags.None,BitmapImageFormat.Png});
                Frames.Enqueue(((int)_itemType.GetProperty("ActionId")!.GetValue(item)!,image?.ToArray()));
            } catch(Exception error) { Errors.Enqueue(error); }
        };
        var argument = Expression.Parameter(_itemType,"item");
        var callback = Expression.Lambda(typeof(Action<>).MakeGenericType(_itemType),
            Expression.Invoke(Expression.Constant(collect),Expression.Convert(argument,typeof(object))),argument).Compile();
        _queue = Activator.CreateInstance(queueField.FieldType,new object[]{callback,"Meld native image subscription test"})!;
        foreach(var field in sessionType.GetFields(Members)) {
            if(field == queueField) field.SetValue(_session,_queue);
            else if(field.FieldType == typeof(PluginManager)) field.SetValue(_session,manager);
            else if(field.FieldType == typeof(object)) field.SetValue(_session,new object());
            else if(field.FieldType.IsGenericType && field.FieldType.GetConstructor(Type.EmptyTypes) != null)
                field.SetValue(_session,Activator.CreateInstance(field.FieldType));
        }
        _dispatch = (EventHandler<PluginActionImageChangedEventArgs>)Delegate.CreateDelegate(
            typeof(EventHandler<PluginActionImageChangedEventArgs>),_session,
            sessionType.GetMethod("OnPluginActionImageChanged",Members)!);
        _image = (EventHandler<ActionImageChangedEventArgs>)Delegate.CreateDelegate(typeof(EventHandler<ActionImageChangedEventArgs>),
            manager,typeof(PluginManager).GetMethod("OnPluginActionImageChanged",Members,null,
                new[]{typeof(object),typeof(ActionImageChangedEventArgs)},null)!);
        _state = (EventHandler<ActionStateChangedEventArgs>)Delegate.CreateDelegate(typeof(EventHandler<ActionStateChangedEventArgs>),
            manager,typeof(PluginManager).GetMethod("OnPluginActionStateChanged",Members)!);
        manager.PluginActionImageChanged += _dispatch;
        plugin.ActionImageChanged += _image;
        plugin.ActionStateChanged += _state;
        queueField.FieldType.GetMethod("Start",Members)!.Invoke(_queue,null);
    }

    public void Subscribe(int id, string action)
    {
        var item = Activator.CreateInstance(_itemType)!;
        foreach(var pair in new Dictionary<string,object>{{"ActionId",id},{"IsActive",true},{"ActionName",action},{"Width",116},{"Height",116}})
            _itemType.GetProperty(pair.Key)!.SetValue(item,pair.Value);
        var items = Array.CreateInstance(_itemType,1); items.SetValue(item,0);
        _session.GetType().GetMethod("Subscribe",Members)!.Invoke(_session,new object[]{items});
    }

    public void Dispose()
    {
        _plugin.ActionImageChanged -= _image;
        _plugin.ActionStateChanged -= _state;
        _manager.PluginActionImageChanged -= _dispatch;
        ((IDisposable)_queue).Dispose();
    }
}
