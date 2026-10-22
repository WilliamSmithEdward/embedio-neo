using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace EmbedIO.Tests.TestObjects
{
    // Names the incomplete tasks an object graph of library types holds, by field path,
    // so a test that times out waiting for shutdown can report which stage is still
    // pending. It follows the library's own types and the arrays and standard
    // collections they hold, to a bounded depth and number of entries.
    internal static class PendingTaskReport
    {
        private const int MaximumEntries = 64;
        private const int MaximumItems = 1024;

        internal static string Describe(object root, int depth = 6)
        {
            var report = new StringBuilder();
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var entries = 0;
            Visit(root, root.GetType().Name, depth);
            return entries == 0 ? "no pending tasks" : report.ToString();

            void Visit(object value, string path, int remaining)
            {
                if (entries >= MaximumEntries || !seen.Add(value)) return;

                if (value is Task task)
                {
                    if (!task.IsCompleted)
                    {
                        report
                            .Append('\n')
                            .Append(path)
                            .Append(": ")
                            .Append(task.Status);
                        entries++;
                    }

                    return;
                }

                if (remaining == 0) return;

                var type = value.GetType();

                if (
                    value is IEnumerable items
                    && (type.IsArray || type.Namespace?.StartsWith("System.Collections", StringComparison.Ordinal)
                        == true)
                )
                {
                    try
                    {
                        var index = 0;

                        foreach (var item in items)
                        {
                            var at = path + "[" + index + "]";

                            if (item != null && IsKeyValue(item.GetType()))
                            {
                                if (Property(item, "Key") is { } key) Visit(key, at + ".Key", remaining - 1);

                                if (Property(item, "Value") is { } entry) Visit(entry, at + ".Value", remaining - 1);
                            }

                            else if (item != null) Visit(item, at, remaining - 1);

                            if (++index == MaximumItems) break;
                        }
                    }

                    // Another thread changed the collection; report what was read.
                    catch (InvalidOperationException)
                    {
                        report
                            .Append('\n')
                            .Append(path)
                            .Append(": changed while read");
                    }

                    return;
                }

                for (var current = type; current != null && IsLibraryType(current); current = current.BaseType)
                    foreach (var field in current.GetFields(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly
                    ))
                        if (!field.FieldType.IsValueType && field.GetValue(value) is { } child)
                            Visit(child, path + "." + field.Name, remaining - 1);
            }
        }

        private static bool IsLibraryType(Type type) =>
            type.Assembly == typeof(WebServer).Assembly || type.Namespace?.StartsWith(
                "EmbedIO",
                StringComparison.Ordinal
            )
                == true;

        private static bool IsKeyValue(Type type) =>
            type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>);

        private static object? Property(object target, string name) =>
            target
                .GetType()
                .GetProperty(name)
                ?.GetValue(target);
    }
}
