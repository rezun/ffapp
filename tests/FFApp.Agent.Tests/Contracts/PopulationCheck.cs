using System.Collections;
using System.Reflection;
using System.Text.Json.Serialization;

namespace FFApp.Agent.Tests.Contracts;

/// <summary>
/// Finds properties of a sample that still hold the value a freshly constructed instance has.
/// Such a property would not be detected by a round-trip test when it gets lost on the wire.
/// </summary>
internal static class PopulationCheck
{
    public static IReadOnlyList<string> DefaultValuedProperties(object sample)
    {
        var findings = new List<string>();
        Visit(sample, sample.GetType().Name, findings);
        return findings;
    }

    private static void Visit(object value, string path, List<string> findings)
    {
        var type = value.GetType();
        if (!IsModel(type))
            return;

        var baseline = CreateBaseline(type);
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0
                || property.GetCustomAttribute<JsonIgnoreAttribute>() is not null
                || property.Name == "EqualityContract")
            {
                continue;
            }

            var propertyPath = $"{path}.{property.Name}";
            var actual = property.GetValue(value);
            var reference = baseline is null ? null : property.GetValue(baseline);
            if (actual is null || IsEmpty(actual) || (property.PropertyType.IsEnum ? false : SameValue(actual, reference)))
            {
                findings.Add(propertyPath);
                continue;
            }

            if (actual is IEnumerable sequence and not string)
            {
                var index = 0;
                foreach (var item in sequence)
                {
                    if (item is not null)
                        Visit(item, $"{propertyPath}[{index}]", findings);
                    index++;
                }
            }
            else
            {
                Visit(actual, propertyPath, findings);
            }
        }
    }

    private static bool IsModel(Type type) =>
        type.Namespace?.StartsWith("FFApp", StringComparison.Ordinal) == true && type.IsClass;

    private static bool IsEmpty(object value) => value switch
    {
        string text => text.Length == 0,
        IEnumerable sequence => !sequence.GetEnumerator().MoveNext(),
        _ => false,
    };

    private static bool SameValue(object actual, object? reference)
    {
        if (reference is null)
            return false;
        if (actual is IEnumerable a and not string && reference is IEnumerable b)
            return a.Cast<object>().SequenceEqual(b.Cast<object>());
        return actual.GetType().IsValueType || actual is string ? actual.Equals(reference) : false;
    }

    /// <summary>Constructs the type with every constructor argument at its declared or CLR default.</summary>
    private static object? CreateBaseline(Type type)
    {
        var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        if (constructor is null)
            return null;
        var arguments = constructor.GetParameters().Select(parameter =>
            parameter.HasDefaultValue ? parameter.DefaultValue
            : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType)
            : parameter.ParameterType == typeof(string) ? "" : null).ToArray();
        return constructor.Invoke(arguments);
    }
}
