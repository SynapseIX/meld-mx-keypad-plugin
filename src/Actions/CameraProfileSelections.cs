namespace Loupedeck.MeldMxKeypadPlugin;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

// Logi 6.4 resolves legacy picker aliases for presses, but not for image/state
// requests. Read ONLY this action's saved alias -> picker value mappings from
// the service's profile files. Never edit a profile or infer a camera from the
// last button pressed. No Logi internal assemblies are loaded by this bridge.
internal sealed class CameraProfileSelections
{
    private readonly String _directory;
    private readonly Object _sync = new();
    private readonly Dictionary<String, (DateTime Modified, Int64 Length, Dictionary<String, String> Values)> _files = new();
    private Dictionary<String, String> _aliases = new(StringComparer.Ordinal);
    private Timer _timer;
    public event Action Changed;

    public CameraProfileSelections() : this(Path.Combine(Helpers.GetLoupedeckDataDirectory(), "Applications")) { }
    internal CameraProfileSelections(String directory) => _directory = directory;

    public String[] Aliases { get { lock (_sync) return _aliases.Keys.ToArray(); } }
    public String Resolve(String selection)
    {
        lock (_sync) return selection != null && _aliases.TryGetValue(selection, out var target) ? target : selection;
    }

    public void Start()
    {
        Refresh();
        _timer ??= new Timer(_ => Refresh(), null, 500, 500);
    }

    public void Stop() { _timer?.Dispose(); _timer = null; }

    private void Refresh()
    {
        Boolean changed;
        lock (_sync)
        {
            var aliases = new Dictionary<String, String>(StringComparer.Ordinal);
            var found = new HashSet<String>(StringComparer.Ordinal);
            try
            {
                if (Directory.Exists(_directory))
                    foreach (var path in Directory.EnumerateFiles(_directory, "ProfileInfo.json", new EnumerationOptions
                    { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint, MatchCasing = MatchCasing.CaseInsensitive }))
                    {
                        found.Add(path);
                        try
                        {
                            var file = new FileInfo(path);
                            if (!_files.TryGetValue(path, out var cached) || cached.Modified != file.LastWriteTimeUtc || cached.Length != file.Length)
                            {
                                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                                using var json = JsonDocument.Parse(stream);
                                cached = (file.LastWriteTimeUtc, file.Length, ReadSelections(json.RootElement));
                                _files[path] = cached;
                            }
                            foreach (var pair in cached.Values)
                            {
                                // A duplicated profile may contain the same alias. Conflicting
                                // selections are unavailable rather than targeting a wrong layer.
                                if (aliases.TryGetValue(pair.Key, out var existing) && existing != pair.Value)
                                    aliases[pair.Key] = null;
                                else aliases[pair.Key] = pair.Value;
                            }
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
                        { _files.Remove(path); } // Atomic/partial saves are retried on the next tick.
                    }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            foreach (var deleted in _files.Keys.Except(found).ToArray()) _files.Remove(deleted);
            changed = aliases.Count != _aliases.Count || aliases.Any(x => !_aliases.TryGetValue(x.Key, out var old) || old != x.Value);
            _aliases = aliases;
        }
        if (changed) Changed?.Invoke();
    }

    private static Dictionary<String, String> ReadSelections(JsonElement profile)
    {
        var values = new Dictionary<String, String>(StringComparer.Ordinal);
        if (profile.ValueKind != JsonValueKind.Object || !profile.TryGetProperty("profileCommands", out var commands) ||
            commands.ValueKind != JsonValueKind.Array) return values;
        foreach (var command in commands.EnumerateArray())
        {
            if (command.ValueKind != JsonValueKind.Object ||
                !command.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                !command.TryGetProperty("legacyProfileActionParameter", out var selection) || selection.ValueKind != JsonValueKind.String ||
                !ActionString.TryGetValidAction(name.GetString(), out var action) ||
                !String.Equals(action.PluginName, "MeldMxKeypad", StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(action.ActionName, typeof(ToggleCameraCommand).FullName, StringComparison.OrdinalIgnoreCase) ||
                String.IsNullOrEmpty(action.ActionParameter)) continue;
            values[action.ActionParameter] = selection.GetString();
        }
        return values;
    }
}
