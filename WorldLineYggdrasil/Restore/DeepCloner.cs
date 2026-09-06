using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace WorldLineYggdrasil.Restore;

/// <summary>
/// Reflection-based deep clone with cycle detection. Used for state where the game
/// keeps mutable inner objects (PowerModel._internalData, RelicModel.DynamicVars, etc.)
/// that the reference impl's MemberwiseClone misses, causing the bugs described in
/// project notes (Stone Sword stack count, Statue scaling persistence).
///
/// Rules:
///  - Primitives, enums, string, decimal, DateTime, Guid → returned as-is.
///  - Godot.GodotObject (and subclasses) → NEVER cloned. Scene-tree references
///    must stay live; cloning a node would produce a half-disposed orphan.
///  - Delegates / events → returned as-is (no point cloning them; we want them to
///    drop on snapshot teardown).
///  - Type / MethodInfo / FieldInfo → returned as-is.
///  - Arrays, List&lt;T&gt;, Dictionary&lt;K,V&gt;, HashSet&lt;T&gt; → element-wise deep clone.
///  - Other reference types → uninitialized instance + per-field copy
///    (walking the inheritance chain so private base fields are also covered).
///  - Cycles handled via reference-identity dictionary.
/// </summary>
internal static class DeepCloner
{
    public static T? Clone<T>(T? obj) where T : class
        => (T?)CloneInternal(obj, new Dictionary<object, object>(ReferenceEqualityComparer.Instance));

    public static object? CloneObject(object? obj)
        => CloneInternal(obj, new Dictionary<object, object>(ReferenceEqualityComparer.Instance));

    private static object? CloneInternal(object? obj, Dictionary<object, object> seen)
    {
        if (obj == null) return null;
        var type = obj.GetType();

        if (IsImmutable(type)) return obj;
        if (obj is Delegate) return obj;
        if (obj is Type or MemberInfo) return obj;
        if (obj is Godot.GodotObject) return obj;

        if (seen.TryGetValue(obj, out var existing)) return existing;

        if (type.IsArray)
        {
            var src = (Array)obj;
            var elem = type.GetElementType()!;
            var dst = Array.CreateInstance(elem, src.Length);
            seen[obj] = dst;
            for (int i = 0; i < src.Length; i++)
                dst.SetValue(CloneInternal(src.GetValue(i), seen), i);
            return dst;
        }

        // Generic collections: List<T>, Dictionary<K,V>, HashSet<T>, Queue<T>, Stack<T>.
        // We don't try to be clever — we copy fields just like any other class. This
        // works because the underlying storage is an array which we deep-clone above.
        // Custom IEnumerable types fall through to the field-by-field path too.

        object clone;
        try
        {
            clone = RuntimeHelpers.GetUninitializedObject(type);
        }
        catch
        {
            // Some types (delegates, COM types, Span, generic pointers) cannot be
            // uninitialized-allocated. Fall back to the original — best we can do.
            return obj;
        }

        seen[obj] = clone;

        foreach (var field in GetCloneFields(type))
        {
            // Skip readonly only for primitives — readonly reference fields can still
            // be set via reflection on .NET 9, and we need to clone them.
            object? value;
            try { value = field.GetValue(obj); }
            catch { continue; }
            try { field.SetValue(clone, CloneInternal(value, seen)); }
            catch { /* unsettable — skip */ }
        }

        return clone;
    }

    // Field lists per Type are stable for the process lifetime; computing them
    // is reflection-heavy (multiple GetFields across the inheritance chain).
    // Cache once per type — this is the single largest snapshot-capture cost
    // when many powers / relics route through CloneInternal on each card play.
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> _fieldCache = new();

    private static FieldInfo[] GetCloneFields(Type type)
        => _fieldCache.GetOrAdd(type, BuildCloneFields);

    private static FieldInfo[] BuildCloneFields(Type type)
    {
        var list = new List<FieldInfo>();
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            list.AddRange(t.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                      | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        }
        return list.ToArray();
    }

    private static bool IsImmutable(Type t)
    {
        if (t.IsPrimitive) return true;
        if (t.IsEnum) return true;
        if (t == typeof(string)) return true;
        if (t == typeof(decimal)) return true;
        if (t == typeof(DateTime)) return true;
        if (t == typeof(Guid)) return true;
        return false;
    }
}
