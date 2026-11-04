using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace PicoHex.ApiSurface;

/// <summary>
/// Deterministic public-API surface extractor for PicoHex release versioning.
///
/// Reads assemblies with System.Reflection.Metadata (no assembly loading, no
/// runtime reflection, no PackageReferences) so it works on any output -
/// including netstandard2.0 generators and NativeAOT-published binaries.
///
/// Subcommands:
///   dump &lt;assembly.dll&gt; [--out &lt;file&gt;]     surface lines (sorted, LF)
///   diff &lt;baseline.txt&gt; &lt;current.txt&gt;      added/removed lines + summary
///   next-version [--last v2026.9.0] [--api-changed true|false]
///                [--year 2026] [--reset-on-year-change] [--explain]
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
                return Usage();
            return args[0] switch
            {
                "dump" => DumpCommand(args),
                "diff" => DiffCommand(args),
                "next-version" => NextVersionCommand(args),
                _ => Fail($"unknown command '{args[0]}'"),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int Usage(string? message = null)
    {
        if (message is not null)
            Console.Error.WriteLine($"error: {message}");
        Console.Error.WriteLine(
            """
            picohex-api-surface - PicoHex release tooling (zero dependencies)

            dump <assembly.dll> [--out <file>]
                Print the public/protected API surface, sorted, LF-terminated.

            diff <baseline.txt> <current.txt>
                Print added (+) and removed (-) lines, then "added=N removed=M".
                Exit 0 = identical, 1 = different, 2 = error.

            next-version [--last <tag>] [--api-changed <bool>] [--year <yyyy>]
                         [--reset-on-year-change] [--explain]
                PicoHex rule: <year>.<x>.<y>
                  x bumps when the public API changed (y resets to 0)
                  y bumps when it did not
                  year is the current year (a stamp; carries x/y forward)
                With no --last, the first release is <year>.0.0.
            """
        );
        return message is null ? 0 : 2;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 2;
    }

    // ----------------------------- dump -----------------------------

    private static int DumpCommand(string[] args)
    {
        if (args.Length < 2)
            return Usage("dump requires <assembly.dll>");
        var assemblyPath = args[1];
        string? outPath = null;
        for (var i = 2; i < args.Length; i++)
        {
            if (args[i] == "--out" && i + 1 < args.Length)
                outPath = args[++i];
            else
                return Usage($"unknown dump option '{args[i]}'");
        }
        if (!File.Exists(assemblyPath))
            return Fail($"assembly not found: {assemblyPath}");

        var lines = Dump(assemblyPath);
        var text = lines.Count == 0 ? "" : string.Join('\n', lines) + "\n";
        if (outPath is null)
            Console.Out.Write(text);
        else
            File.WriteAllText(outPath, text);
        return 0;
    }

    internal static List<string> Dump(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var probe = new SignatureProbe();
        var provider = new NameProvider(probe);
        var lines = new List<string>();

        foreach (var typeHandle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(typeHandle);
            if (!IsVisible(md, type))
                continue;
            if (IsCompilerGenerated(md, type.GetCustomAttributes()))
                continue;
            var simpleName = md.GetString(type.Name);
            if (simpleName.Length == 0 || simpleName[0] == '<')
                continue;

            var typeName = TypeNameOf(md, type);
            lines.Add($"T:{KindOf(md, type)} {typeName}");

            var typeParams = ParamNames(md, type.GetGenericParameters());
            var ctx = new TypeContext { TypeParams = typeParams };

            if (!type.BaseType.IsNil)
            {
                var baseName = NameOf(md, type.BaseType, provider, ctx);
                if (
                    baseName
                    is not (
                        "object"
                        or "System.Object"
                        or "System.ValueType"
                        or "System.Enum"
                        or "System.MulticastDelegate"
                    )
                )
                    lines.Add($"B:{typeName} : {baseName}");
            }

            foreach (var implHandle in type.GetInterfaceImplementations())
            {
                var impl = md.GetInterfaceImplementation(implHandle);
                lines.Add($"I:{typeName} : {NameOf(md, impl.Interface, provider, ctx)}");
            }

            foreach (var fieldHandle in type.GetFields())
            {
                var field = md.GetFieldDefinition(fieldHandle);
                if (!IsVisible(field.Attributes))
                    continue;
                if (IsCompilerGenerated(md, field.GetCustomAttributes()))
                    continue;
                var fieldType = field.DecodeSignature(provider, ctx);
                lines.Add(
                    $"F:{typeName}.{md.GetString(field.Name)} : {fieldType}"
                        + $"{ConstantText(md, field)}{Modifiers(field)}"
                );
            }

            foreach (var methodHandle in type.GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                if (!IsVisible(method.Attributes))
                    continue;
                if (IsCompilerGenerated(md, method.GetCustomAttributes()))
                    continue;
                var methodName = md.GetString(method.Name);
                if (IsAccessor(method, methodName) || methodName == "Finalize")
                    continue;

                var methodCtx = new TypeContext
                {
                    TypeParams = typeParams,
                    MethodParams = ParamNames(md, method.GetGenericParameters()),
                };
                probe.SawReadOnly = false;
                var signature = method.DecodeSignature(provider, methodCtx);
                var sawReadOnly = probe.SawReadOnly;
                // Parameter rows are keyed by sequence number (0 = return value).
                var parameterBySequence = new Dictionary<int, Parameter>();
                foreach (var parameterHandle in method.GetParameters())
                {
                    var parameter = md.GetParameter(parameterHandle);
                    parameterBySequence[parameter.SequenceNumber] = parameter;
                }
                var parameters = new List<string>();
                for (var i = 0; i < signature.ParameterTypes.Length; i++)
                {
                    parameterBySequence.TryGetValue(i + 1, out var parameter);
                    parameters.Add(
                        ParameterText(md, parameter, signature.ParameterTypes[i], sawReadOnly, i)
                    );
                }
                var arity =
                    signature.GenericParameterCount > 0
                        ? $"``{signature.GenericParameterCount}"
                        : "";
                lines.Add(
                    $"M:{typeName}.{methodName}{arity}({string.Join(", ", parameters)})"
                        + $" : {signature.ReturnType}{MethodModifiers(method)}"
                );
            }

            foreach (var propertyHandle in type.GetProperties())
            {
                var property = md.GetPropertyDefinition(propertyHandle);
                if (IsCompilerGenerated(md, property.GetCustomAttributes()))
                    continue;
                var signature = property.DecodeSignature(provider, ctx);
                var accessors = property.GetAccessors();
                var hasGetter = IsVisibleAccessor(md, accessors.Getter);
                var hasSetter = IsVisibleAccessor(md, accessors.Setter);
                if (!hasGetter && !hasSetter)
                    continue;
                var parts = new List<string>();
                if (hasGetter)
                    parts.Add("get;");
                if (hasSetter)
                {
                    var initOnly = IsInitOnly(md, provider, probe, accessors.Setter);
                    parts.Add(
                        AccessorVisibility(md, accessors.Setter) + (initOnly ? "init;" : "set;")
                    );
                }
                var indexer =
                    signature.ParameterTypes.Length > 0
                        ? $"[{string.Join(", ", signature.ParameterTypes)}]"
                        : "";
                lines.Add(
                    $"P:{typeName}.{md.GetString(property.Name)}{indexer}"
                        + $" : {signature.ReturnType} {{ {string.Join(" ", parts)} }}"
                );
            }

            foreach (var eventHandle in type.GetEvents())
            {
                var eventDefinition = md.GetEventDefinition(eventHandle);
                if (IsCompilerGenerated(md, eventDefinition.GetCustomAttributes()))
                    continue;
                var accessors = eventDefinition.GetAccessors();
                if (
                    !IsVisibleAccessor(md, accessors.Adder)
                    && !IsVisibleAccessor(md, accessors.Remover)
                )
                    continue;
                lines.Add(
                    $"E:{typeName}.{md.GetString(eventDefinition.Name)}"
                        + $" : {NameOf(md, eventDefinition.Type, provider, ctx)}"
                );
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    // ----------------------------- diff -----------------------------

    private static int DiffCommand(string[] args)
    {
        if (args.Length != 3)
            return Usage("diff requires <baseline.txt> <current.txt>");
        if (!File.Exists(args[1]))
            return Fail($"baseline not found: {args[1]}");
        if (!File.Exists(args[2]))
            return Fail($"current surface not found: {args[2]}");

        var baseline = ReadLines(args[1]);
        var current = ReadLines(args[2]);
        var currentSet = new HashSet<string>(current, StringComparer.Ordinal);
        var baselineSet = new HashSet<string>(baseline, StringComparer.Ordinal);

        var added = current.Where(line => !baselineSet.Contains(line)).ToList();
        var removed = baseline.Where(line => !currentSet.Contains(line)).ToList();

        foreach (var line in removed)
            Console.Out.WriteLine($"- {line}");
        foreach (var line in added)
            Console.Out.WriteLine($"+ {line}");
        Console.Out.WriteLine($"added={added.Count} removed={removed.Count}");
        return added.Count == 0 && removed.Count == 0 ? 0 : 1;
    }

    private static List<string> ReadLines(string path) =>
        File.ReadAllLines(path)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .ToList();

    // -------------------------- next-version ------------------------

    private static int NextVersionCommand(string[] args)
    {
        string? last = null;
        var apiChanged = false;
        var year = DateTime.UtcNow.Year;
        var resetOnYearChange = false;
        var explain = false;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--last" when i + 1 < args.Length:
                    last = args[++i];
                    break;
                case "--api-changed" when i + 1 < args.Length:
                    if (!bool.TryParse(args[++i], out apiChanged))
                        return Usage("--api-changed expects true or false");
                    break;
                case "--year" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out year))
                        return Usage("--year expects a four-digit year");
                    break;
                case "--reset-on-year-change":
                    resetOnYearChange = true;
                    break;
                case "--explain":
                    explain = true;
                    break;
                default:
                    return Usage($"unknown next-version option '{args[i]}'");
            }
        }

        if (year is < 2000 or > 2100)
            return Fail($"year out of range: {year}");

        var next = NextVersion(last, apiChanged, year, resetOnYearChange);
        if (explain)
            Console.Error.WriteLine(
                $"last={last ?? "(none)"} apiChanged={apiChanged.ToString().ToLowerInvariant()} "
                    + $"year={year} resetOnYearChange={resetOnYearChange.ToString().ToLowerInvariant()} -> {next}"
            );
        Console.Out.WriteLine(next);
        return 0;
    }

    /// <summary>
    /// PicoHex version rule. Segment 1 is the current year; segment 2 only
    /// moves when the public API changed; segment 3 moves when it did not.
    /// </summary>
    internal static string NextVersion(
        string? lastTag,
        bool apiChanged,
        int year,
        bool resetOnYearChange = false
    )
    {
        if (string.IsNullOrWhiteSpace(lastTag))
            return $"{year}.0.0";

        var text = lastTag.Trim().TrimStart('v', 'V');
        var parts = text.Split('.');
        if (
            parts.Length != 3
            || !int.TryParse(parts[0], out var lastYear)
            || !int.TryParse(parts[1], out var x)
            || !int.TryParse(parts[2], out var y)
            || x < 0
            || y < 0
        )
            throw new FormatException($"last version '{lastTag}' is not <year>.<x>.<y>");

        if (resetOnYearChange && year != lastYear)
        {
            x = 0;
            y = 0;
        }

        return apiChanged ? $"{year}.{x + 1}.0" : $"{year}.{x}.{y + 1}";
    }

    // --------------------------- metadata ---------------------------

    private static bool IsVisible(MetadataReader md, TypeDefinition type)
    {
        var visibility = type.Attributes & TypeAttributes.VisibilityMask;
        if (
            visibility
            is not (
                TypeAttributes.Public
                or TypeAttributes.NestedPublic
                or TypeAttributes.NestedFamily
                or TypeAttributes.NestedFamORAssem
            )
        )
            return false;
        var declaring = type.GetDeclaringType();
        while (!declaring.IsNil)
        {
            var outer = md.GetTypeDefinition(declaring);
            if (!IsVisible(md, outer))
                return false;
            declaring = outer.GetDeclaringType();
        }
        return true;
    }

    private static bool IsVisible(MethodAttributes attributes) =>
        (attributes & MethodAttributes.MemberAccessMask)
            is MethodAttributes.Public
                or MethodAttributes.Family
                or MethodAttributes.FamORAssem;

    private static bool IsVisible(FieldAttributes attributes) =>
        (attributes & FieldAttributes.FieldAccessMask)
            is FieldAttributes.Public
                or FieldAttributes.Family
                or FieldAttributes.FamORAssem;

    private static bool IsVisibleAccessor(MetadataReader md, MethodDefinitionHandle handle) =>
        !handle.IsNil && IsVisible(md.GetMethodDefinition(handle).Attributes);

    private static bool IsAccessor(MethodDefinition method, string name) =>
        method.Attributes.HasFlag(MethodAttributes.SpecialName)
        && (
            name.StartsWith("get_", StringComparison.Ordinal)
            || name.StartsWith("set_", StringComparison.Ordinal)
            || name.StartsWith("add_", StringComparison.Ordinal)
            || name.StartsWith("remove_", StringComparison.Ordinal)
        );

    private static string AccessorVisibility(MetadataReader md, MethodDefinitionHandle handle)
    {
        var attributes = md.GetMethodDefinition(handle).Attributes;
        return (attributes & MethodAttributes.MemberAccessMask) switch
        {
            MethodAttributes.Public => "",
            MethodAttributes.Family => "protected ",
            MethodAttributes.FamORAssem => "protected internal ",
            MethodAttributes.Assembly => "internal ",
            MethodAttributes.FamANDAssem => "private protected ",
            _ => "private ",
        };
    }

    private static bool IsInitOnly(
        MetadataReader md,
        NameProvider provider,
        SignatureProbe probe,
        MethodDefinitionHandle setter
    )
    {
        if (setter.IsNil)
            return false;
        probe.SawInit = false;
        _ = md.GetMethodDefinition(setter).DecodeSignature(provider, new TypeContext());
        return probe.SawInit;
    }

    private static string KindOf(MetadataReader md, TypeDefinition type)
    {
        if (type.Attributes.HasFlag(TypeAttributes.Interface))
            return "interface";
        if (type.BaseType.IsNil)
            return "class";
        return NameOf(md, type.BaseType, null, null) switch
        {
            "System.Enum" => "enum",
            "System.MulticastDelegate" => "delegate",
            "System.ValueType" => "struct",
            _ => "class",
        };
    }

    private static string Modifiers(FieldDefinition field)
    {
        var parts = new List<string>();
        if (field.Attributes.HasFlag(FieldAttributes.Literal))
            parts.Add("const");
        else
        {
            if (field.Attributes.HasFlag(FieldAttributes.Static))
                parts.Add("static");
            if (field.Attributes.HasFlag(FieldAttributes.InitOnly))
                parts.Add("readonly");
        }
        return parts.Count == 0 ? "" : " " + string.Join(" ", parts);
    }

    private static string MethodModifiers(MethodDefinition method)
    {
        var attributes = method.Attributes;
        var parts = new List<string>();
        if (attributes.HasFlag(MethodAttributes.Static))
            parts.Add("static");
        else if (attributes.HasFlag(MethodAttributes.Abstract))
            parts.Add("abstract");
        else if (attributes.HasFlag(MethodAttributes.Virtual))
            parts.Add(attributes.HasFlag(MethodAttributes.Final) ? "override" : "virtual");
        return parts.Count == 0 ? "" : " " + string.Join(" ", parts);
    }

    private static string ParameterText(
        MetadataReader md,
        Parameter parameter,
        string type,
        bool sawReadOnly,
        int index
    )
    {
        var prefix = "";
        if (type.StartsWith("ref ", StringComparison.Ordinal))
        {
            type = type[4..];
            prefix =
                parameter.Attributes.HasFlag(ParameterAttributes.Out) ? "out "
                : sawReadOnly ? "in "
                : "ref ";
        }
        if (!parameter.Name.IsNil && IsParams(md, parameter))
            prefix = "params " + prefix;
        var name = parameter.Name.IsNil ? $"arg{index}" : md.GetString(parameter.Name);
        return $"{prefix}{type} {name}{DefaultValueText(md, parameter)}";
    }

    private static bool IsParams(MetadataReader md, Parameter parameter)
    {
        foreach (var handle in parameter.GetCustomAttributes())
        {
            if (
                AttributeTypeName(md, md.GetCustomAttribute(handle)) == "System.ParamArrayAttribute"
            )
                return true;
        }
        return false;
    }

    private static string DefaultValueText(MetadataReader md, Parameter parameter)
    {
        if (!parameter.Attributes.HasFlag(ParameterAttributes.HasDefault))
            return "";
        if (parameter.Name.IsNil)
            return ""; // return-value parameter
        var constant = ConstantText(md, parameter.GetDefaultValue());
        return constant.Length == 0 ? "" : constant;
    }

    private static string ConstantText(MetadataReader md, FieldDefinition field) =>
        ConstantText(md, field.GetDefaultValue());

    private static string ConstantText(MetadataReader md, ConstantHandle handle)
    {
        if (handle.IsNil)
            return "";
        var constant = md.GetConstant(handle);
        var blob = md.GetBlobReader(constant.Value);
        return constant.TypeCode switch
        {
            ConstantTypeCode.Boolean => $" = {blob.ReadBoolean().ToString().ToLowerInvariant()}",
            ConstantTypeCode.Char => $" = '{EscapeLiteral(blob.ReadChar().ToString(), '\'')}'",
            ConstantTypeCode.SByte => $" = {blob.ReadSByte()}",
            ConstantTypeCode.Byte => $" = {blob.ReadByte()}",
            ConstantTypeCode.Int16 => $" = {blob.ReadInt16()}",
            ConstantTypeCode.UInt16 => $" = {blob.ReadUInt16()}",
            ConstantTypeCode.Int32 => $" = {blob.ReadInt32()}",
            ConstantTypeCode.UInt32 => $" = {blob.ReadUInt32()}",
            ConstantTypeCode.Int64 => $" = {blob.ReadInt64()}",
            ConstantTypeCode.UInt64 => $" = {blob.ReadUInt64()}",
            ConstantTypeCode.Single =>
                $" = {blob.ReadSingle().ToString(System.Globalization.CultureInfo.InvariantCulture)}f",
            ConstantTypeCode.Double =>
                $" = {blob.ReadDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            ConstantTypeCode.String => $" = \"{EscapeLiteral(blob.ReadUTF16(blob.Length), '"')}\"",
            ConstantTypeCode.NullReference => " = null",
            _ => "",
        };
    }

    /// <summary>
    /// Escapes control characters so baseline files stay plain text (several
    /// PicoTui constants are raw ANSI sequences containing ESC).
    /// </summary>
    private static string EscapeLiteral(string value, char quote)
    {
        var builder = new System.Text.StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c == quote)
                        builder.Append('\\').Append(c);
                    else if (c < ' ' || c == '\u007f')
                        builder
                            .Append("\\u")
                            .Append(
                                ((int)c).ToString(
                                    "X4",
                                    System.Globalization.CultureInfo.InvariantCulture
                                )
                            );
                    else
                        builder.Append(c);
                    break;
            }
        }
        return builder.ToString();
    }

    private static bool IsCompilerGenerated(
        MetadataReader md,
        CustomAttributeHandleCollection attributes
    )
    {
        foreach (var handle in attributes)
        {
            if (
                AttributeTypeName(md, md.GetCustomAttribute(handle))
                    .EndsWith("CompilerGeneratedAttribute", StringComparison.Ordinal)
            )
                return true;
        }
        return false;
    }

    private static string AttributeTypeName(MetadataReader md, CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var reference = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                return NameOf(md, reference.Parent, null, null);
            case HandleKind.MethodDefinition:
                var method = md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                return TypeNameOf(md, md.GetTypeDefinition(method.GetDeclaringType()));
            default:
                return "";
        }
    }

    private static IReadOnlyList<string> ParamNames(
        MetadataReader md,
        GenericParameterHandleCollection handles
    )
    {
        var names = new List<string>(handles.Count);
        foreach (var handle in handles)
        {
            var name = md.GetString(md.GetGenericParameter(handle).Name);
            names.Add(string.IsNullOrEmpty(name) ? $"T{names.Count}" : name);
        }
        return names;
    }

    private static string TypeNameOf(MetadataReader md, TypeDefinition type)
    {
        var name = md.GetString(type.Name);
        var arity = type.GetGenericParameters().Count;
        // Generic type names already carry the arity in metadata (List`1);
        // append only when a producer omitted it.
        if (arity > 0 && !name.EndsWith($"`{arity}", StringComparison.Ordinal))
            name += $"`{arity}";
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
            return $"{TypeNameOf(md, md.GetTypeDefinition(declaring))}.{name}";
        var ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    private static string TypeNameOf(MetadataReader md, TypeReference reference)
    {
        var name = md.GetString(reference.Name);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
            return $"{TypeNameOf(md, md.GetTypeReference((TypeReferenceHandle)reference.ResolutionScope))}.{name}";
        var ns = md.GetString(reference.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    private static string NameOf(
        MetadataReader md,
        EntityHandle handle,
        NameProvider? provider,
        TypeContext? context
    )
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                return TypeNameOf(md, md.GetTypeDefinition((TypeDefinitionHandle)handle));
            case HandleKind.TypeReference:
                return TypeNameOf(md, md.GetTypeReference((TypeReferenceHandle)handle));
            case HandleKind.TypeSpecification:
                if (provider is null)
                    return "?";
                return md.GetTypeSpecification((TypeSpecificationHandle)handle)
                    .DecodeSignature(provider, context ?? new TypeContext());
            default:
                return "?";
        }
    }

    internal sealed class TypeContext
    {
        public IReadOnlyList<string> TypeParams { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> MethodParams { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Mutable decode state. The metadata decoder calls the context-free
    /// GetModifiedType overload, so modifiers must be observed here rather than
    /// through TypeContext.
    /// </summary>
    internal sealed class SignatureProbe
    {
        public bool SawInit { get; set; }
        public bool SawReadOnly { get; set; }
    }

    private sealed class NameProvider(SignatureProbe probe)
        : ISignatureTypeProvider<string, TypeContext>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) =>
            typeCode switch
            {
                PrimitiveTypeCode.Boolean => "bool",
                PrimitiveTypeCode.Byte => "byte",
                PrimitiveTypeCode.SByte => "sbyte",
                PrimitiveTypeCode.Char => "char",
                PrimitiveTypeCode.Int16 => "short",
                PrimitiveTypeCode.UInt16 => "ushort",
                PrimitiveTypeCode.Int32 => "int",
                PrimitiveTypeCode.UInt32 => "uint",
                PrimitiveTypeCode.Int64 => "long",
                PrimitiveTypeCode.UInt64 => "ulong",
                PrimitiveTypeCode.Single => "float",
                PrimitiveTypeCode.Double => "double",
                PrimitiveTypeCode.IntPtr => "nint",
                PrimitiveTypeCode.UIntPtr => "nuint",
                PrimitiveTypeCode.Object => "object",
                PrimitiveTypeCode.String => "string",
                PrimitiveTypeCode.Void => "void",
                PrimitiveTypeCode.TypedReference => "TypedReference",
                _ => typeCode.ToString(),
            };

        public string GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind
        ) => TypeNameOf(reader, reader.GetTypeDefinition(handle));

        public string GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind
        ) => TypeNameOf(reader, reader.GetTypeReference(handle));

        public string GetTypeFromSpecification(
            MetadataReader reader,
            TypeContext genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind
        ) => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetArrayType(string elementType, ArrayShape shape) =>
            $"{elementType}[{new string(',', shape.Rank - 1)}]";

        public string GetByReferenceType(string elementType) => "ref " + elementType;

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetGenericInstantiation(
            string genericType,
            ImmutableArray<string> typeArguments
        ) => $"{genericType}<{string.Join(", ", typeArguments)}>";

        public string GetGenericMethodParameter(TypeContext genericContext, int index) =>
            index < genericContext.MethodParams.Count
                ? genericContext.MethodParams[index]
                : $"M{index}";

        public string GetGenericTypeParameter(TypeContext genericContext, int index) =>
            index < genericContext.TypeParams.Count
                ? genericContext.TypeParams[index]
                : $"T{index}";

        // The metadata decoder uses this overload; record the modifiers here.
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
            Observe(modifier, unmodifiedType);

        public string GetModifiedType(
            string modifier,
            string unmodifiedType,
            bool isRequired,
            TypeContext genericContext
        ) => Observe(modifier, unmodifiedType);

        private string Observe(string modifier, string unmodifiedType)
        {
            if (modifier.EndsWith("IsExternalInit", StringComparison.Ordinal))
                probe.SawInit = true;
            else if (modifier.EndsWith("IsReadOnlyAttribute", StringComparison.Ordinal))
                probe.SawReadOnly = true;
            return unmodifiedType;
        }

        public string GetPinnedType(string elementType) => elementType;

        public string GetFunctionPointerType(MethodSignature<string> signature) => "delegate*";
    }
}
