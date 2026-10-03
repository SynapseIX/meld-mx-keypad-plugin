namespace Loupedeck.MeldMxKeypadPlugin;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

// IDs can change when Meld reloads a session. Prefer a unique saved name;
// retain the original position only to disambiguate duplicate names.
internal static class MeldSelection
{
    public static Boolean SameName(String left, String right) =>
        String.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    // Compare references without changing the original IDs sent back to Meld.
    public static Boolean BelongsToScene(MeldItem item, MeldItem scene, IReadOnlyList<MeldItem> items)
    {
        var parent = item.Parent;
        for (var depth = 0; !String.IsNullOrWhiteSpace(parent) && depth < items.Count; depth++)
        {
            if (String.Equals(parent, scene.Id, StringComparison.OrdinalIgnoreCase)) return true;
            parent = items.FirstOrDefault(x => String.Equals(x.Id, parent, StringComparison.OrdinalIgnoreCase))?.Parent;
        }
        return false;
    }

    public static String Key(MeldItem item) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(item.Name ?? String.Empty)) + ":" + item.Index;

    public static Boolean TryRead(String key, out String name, out Int32 index)
    {
        name = null;
        index = 0;
        var split = key?.LastIndexOf(':') ?? -1;
        if (split < 0 || !Int32.TryParse(key[(split + 1)..], out index)) return false;
        try { name = Encoding.UTF8.GetString(Convert.FromBase64String(key[..split])); }
        catch (FormatException) { return false; }
        return true;
    }

    public static MeldItem Resolve(IEnumerable<MeldItem> items, String key)
    {
        if (!TryRead(key, out var name, out var index)) return null;
        var matches = items.Where(x => SameName(x.Name, name)).ToArray();
        if (matches.Length == 1) return matches[0];
        var atPosition = matches.Where(x => x.Index == index).ToArray();
        return atPosition.Length == 1 ? atPosition[0] : null;
    }
}
