using System.Collections;
using System.Reflection;

internal static class ListenerInventory
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Field(object owner, string name)
        => owner.GetType().GetField(name, Hidden)?.GetValue(owner) ?? throw new InvalidOperationException("Missing listener owner field " + name);
    internal static IDictionary Pending(object listener)
        => (IDictionary)Field(Field(Field(listener, "_coordinator"), "_epoch"), "_pending");
    internal static IDictionary Sessions(object listener)
        => (IDictionary)Field(Field(listener, "_coordinator"), "_sessions");
}
