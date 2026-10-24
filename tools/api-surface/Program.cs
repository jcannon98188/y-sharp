// Dumps the public API surface of one assembly as sorted, diffable text (TASK-4.2, ADR 0021).
// Usage: dotnet run --project tools/api-surface -- <path to YSharp.dll>
// Every line starts with the owning type's full name, so ordinal sorting keeps each type's members together.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace ApiSurface;

internal static class Program
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: api-surface <path to assembly>");
            return 2;
        }

        var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        var lines = new List<string>();
        foreach (var type in assembly.GetExportedTypes())
        {
            DumpType(type, lines);
        }

        lines.Sort(StringComparer.Ordinal);
        var output = new StringBuilder();
        foreach (var line in lines)
        {
            output.Append(line).Append('\n');
        }

        Console.Out.Write(output.ToString());
        return 0;
    }

    private static void DumpType(Type type, List<string> lines)
    {
        var name = FormatTypeName(type);
        lines.Add($"{name}|{Kind(type)}");

        if (type.BaseType is { } baseType && !type.IsEnum && !type.IsInterface)
        {
            lines.Add($"{name}|base {FormatType(baseType)}");
        }

        foreach (var iface in type.GetInterfaces().OrderBy(i => FormatType(i), StringComparer.Ordinal))
        {
            lines.Add($"{name}|implements {FormatType(iface)}");
        }

        if (type.IsEnum)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                lines.Add($"{name}|enum {field.Name} = {field.GetRawConstantValue()}");
            }

            return;
        }

        foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            lines.Add($"{name}|ctor({FormatParameters(ctor.GetParameters())})");
        }

        foreach (var field in type.GetFields(Declared))
        {
            if (field.IsSpecialName)
            {
                continue;
            }

            var modifiers = (field.IsStatic ? "static " : string.Empty) + (field.IsLiteral ? "const " : string.Empty) + (field.IsInitOnly ? "readonly " : string.Empty);
            var value = field.IsLiteral ? $" = {FormatConstant(field.GetRawConstantValue())}" : string.Empty;
            lines.Add($"{name}|field {modifiers}{FormatType(field.FieldType)} {field.Name}{value}");
        }

        foreach (var property in type.GetProperties(Declared))
        {
            var accessors = (property.GetMethod is { IsPublic: true } ? "get;" : string.Empty)
                + (property.SetMethod is { IsPublic: true } ? "set;" : string.Empty);
            var isStatic = (property.GetMethod ?? property.SetMethod)?.IsStatic == true ? "static " : string.Empty;
            lines.Add($"{name}|property {isStatic}{FormatType(property.PropertyType)} {property.Name} {{{accessors}}}");
        }

        foreach (var evt in type.GetEvents(Declared))
        {
            var isStatic = (evt.AddMethod ?? evt.RemoveMethod)?.IsStatic == true ? "static " : string.Empty;
            lines.Add($"{name}|event {isStatic}{FormatType(evt.EventHandlerType!)} {evt.Name}");
        }

        foreach (var method in type.GetMethods(Declared))
        {
            // Property, event and nested accessors are described above; operators keep their op_ names.
            if (method.IsSpecialName && !method.Name.StartsWith("op_", StringComparison.Ordinal))
            {
                continue;
            }

            lines.Add($"{name}|method {MethodModifiers(method)}{FormatType(method.ReturnType)} {method.Name}{FormatGenericArguments(method)}({FormatParameters(method.GetParameters())})");
        }
    }

    private static string Kind(Type type)
    {
        if (type.IsEnum) return "enum";
        if (type.IsInterface) return "interface";
        if (type.IsDelegate()) return "delegate";
        if (type.IsValueType)
        {
            var readOnly = type.GetCustomAttributes(inherit: false).Any(a => a.GetType().FullName == "System.Runtime.CompilerServices.IsReadOnlyAttribute");
            return (type.IsByRefLike ? "ref struct" : "struct") + (readOnly ? " readonly" : string.Empty) + (type.IsAbstract ? " static" : string.Empty);
        }

        var modifiers = type.IsAbstract && type.IsSealed ? "static " : type.IsAbstract ? "abstract " : type.IsSealed ? "sealed " : string.Empty;
        return modifiers + "class";
    }

    private static bool IsDelegate(this Type type) => type.BaseType == typeof(MulticastDelegate);

    private static string MethodModifiers(MethodInfo method)
    {
        if (method.IsStatic) return "static ";
        if (method.IsAbstract) return "abstract ";
        if (method.IsVirtual)
        {
            if (method.IsFinal) return "sealed-override ";
            return (method.Attributes & MethodAttributes.NewSlot) != 0 ? "virtual " : "override ";
        }

        return string.Empty;
    }

    private static string FormatGenericArguments(MethodInfo method) =>
        method.IsGenericMethodDefinition
            ? "<" + string.Join(", ", method.GetGenericArguments().Select(a => a.Name)) + ">"
            : string.Empty;

    private static string FormatParameters(ParameterInfo[] parameters) =>
        string.Join(", ", parameters.Select(FormatParameter));

    private static string FormatParameter(ParameterInfo parameter)
    {
        var sb = new StringBuilder();
        if (parameter.GetCustomAttributes(typeof(ParamArrayAttribute), inherit: false).Any())
        {
            sb.Append("params ");
        }
        else if (parameter.IsIn)
        {
            sb.Append("in ");
        }
        else if (parameter.IsOut)
        {
            sb.Append("out ");
        }
        else if (parameter.ParameterType.IsByRef)
        {
            sb.Append("ref ");
        }

        sb.Append(FormatType(parameter.ParameterType)).Append(' ').Append(parameter.Name);
        if (parameter.HasDefaultValue)
        {
            sb.Append(" = ").Append(FormatDefault(parameter));
        }

        return sb.ToString();
    }

    private static string FormatDefault(ParameterInfo parameter)
    {
        var raw = parameter.RawDefaultValue;
        if (raw is null)
        {
            return parameter.ParameterType.IsByRef || parameter.ParameterType.IsValueType || Nullable.GetUnderlyingType(parameter.ParameterType) != null
                ? "default"
                : "null";
        }

        return FormatConstant(raw);
    }

    private static string FormatConstant(object? value) => value switch
    {
        null => "null",
        string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        bool b => b ? "true" : "false",
        Enum e => e.ToString(),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string FormatTypeName(Type type)
    {
        var name = type.FullName ?? type.Name;
        if (type.IsGenericTypeDefinition)
        {
            name = name.Substring(0, name.IndexOf('`'));
        }

        return name.Replace('+', '.');
    }

    private static string FormatType(Type type)
    {
        if (type.IsByRef)
        {
            return FormatType(type.GetElementType()!);
        }

        if (type.IsArray)
        {
            return FormatType(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        }

        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (type.IsGenericType)
        {
            var definition = FormatTypeName(type.GetGenericTypeDefinition());
            return definition + "<" + string.Join(", ", type.GetGenericArguments().Select(FormatType)) + ">";
        }

        return FormatTypeName(type);
    }
}
