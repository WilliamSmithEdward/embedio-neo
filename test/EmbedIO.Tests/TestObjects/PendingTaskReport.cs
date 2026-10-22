using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

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
            var entries = 0;
            Walk(
                root,
                depth,
                (value, path) =>
                {
                    if (value is not Task task) return entries < MaximumEntries;

                    if (!task.IsCompleted && entries < MaximumEntries)
                    {
                        report
                            .Append('\n')
                            .Append(path)
                            .Append(": ")
                            .Append(task.Status);
                        entries++;
                    }

                    return false;
                },
                report
            );
            return entries == 0 ? "no pending tasks" : report.ToString();
        }

        // Reports each QUIC connection owner the graph holds: whether it has finished,
        // its connection phase, the closing deadline against the owner's own clock and
        // the RTT estimate behind the closing period (three probe timeouts).
        internal static string DescribeQuicConnections(object root, int depth = 12)
        {
            var report = new StringBuilder();
            var entries = 0;
            Walk(
                root,
                depth,
                (value, path) =>
                {
                    if (value is Task || entries >= MaximumEntries) return false;

                    if (value.GetType().FullName != "EmbedIO.Quic.QuicConnectionOwner") return true;

                    report
                        .Append('\n')
                        .Append(path)
                        .Append(": ")
                        .Append(QuicState(value));
                    entries++;
                    return false;
                },
                report
            );
            return entries == 0 ? "no QUIC connections" : report.ToString();
        }

        private static string QuicState(object owner)
        {
            var state = new StringBuilder();

            try
            {
                var clock = EngineInternals.Field(owner, "_clock") as Func<double>;
                state
                    .Append("completion=")
                    .Append((EngineInternals.Property(owner, "Completion") as Task)?.Status)
                    .Append(" closing=")
                    .Append(EngineInternals.Field(owner, "_closing"))
                    .Append(" now=")
                    .Append(Milliseconds(clock?.Invoke()));

                if (EngineInternals.Field(owner, "_connection") is not { } connection)
                    return state
                        .Append(" connection=released")
                        .ToString();

                var lifetime = EngineInternals.Field(connection, "_lifetime")
                    ?? throw new AssertionException("Missing connection lifetime.");
                var recovery = EngineInternals.Field(connection, "_recovery")
                    ?? throw new AssertionException("Missing connection recovery.");
                return state
                    .Append(" phase=")
                    .Append(EngineInternals.Property(connection, "Phase"))
                    .Append(" deadline=")
                    .Append(Milliseconds(EngineInternals.Field(lifetime, "_deadline")))
                    .Append(" idle-expired=")
                    .Append(EngineInternals.Property(lifetime, "IdleExpired"))
                    .Append(" srtt=")
                    .Append(Milliseconds(EngineInternals.Field(recovery, "_smoothedRtt")))
                    .Append(" rttvar=")
                    .Append(Milliseconds(EngineInternals.Field(recovery, "_variation")))
                    .Append(" latest=")
                    .Append(Milliseconds(EngineInternals.Field(recovery, "_latestRtt")))
                    .Append(" ack-delay=")
                    .Append(Milliseconds(EngineInternals.Property(recovery, "MaximumAckDelay")))
                    .Append(" retention=")
                    .Append(Milliseconds(EngineInternals.Property(recovery, "KeyRetention")))
                    .ToString();
            }

            // Report a renamed member instead of replacing the failure being diagnosed.
            catch (AssertionException missing)
            {
                return state
                    .Append(" unreadable: ")
                    .Append(missing.Message)
                    .ToString();
            }
        }

        private static string? Milliseconds(object? value) =>
            value is double milliseconds ? milliseconds.ToString("F1", CultureInfo.InvariantCulture) : null;

        // Visits library objects and the collections they hold. The callback returns
        // whether to follow the visited object's own fields and items.
        private static void Walk(object root, int depth, Func<object, string, bool> visit, StringBuilder report)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            Visit(root, root.GetType().Name, depth);

            void Visit(object value, string path, int remaining)
            {
                if (!seen.Add(value) || !visit(value, path) || remaining == 0) return;

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
