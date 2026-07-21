using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace RustNet.MetadataProcessor;

/// <summary>
/// Decodes metadata signatures into RustNet canonical type codes:
/// bool, char, i1..u8, r4, r8, string, object, plus "[]" for SZ arrays.
/// Class/value types decode to their full name; <see cref="ToCode"/> folds
/// them to "object" for parameter encoding.
/// </summary>
internal sealed class SignatureProvider : ISignatureTypeProvider<string, object?>
{
    public static readonly SignatureProvider Instance = new();

    public static string ToCode(string decoded)
    {
        switch (decoded)
        {
            case "bool":
            case "char":
            case "i1":
            case "u1":
            case "i2":
            case "u2":
            case "i4":
            case "u4":
            case "i8":
            case "u8":
            case "r4":
            case "r8":
            case "string":
            case "object":
            case "void":
            case "i":
            case "u":
                return decoded;
        }
        if (decoded.EndsWith("[]", StringComparison.Ordinal))
        {
            string elem = ToCode(decoded[..^2]);
            return elem == "string" || elem == "object" ? "object[]" : elem + "[]";
        }
        return "object";
    }

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.SByte => "i1",
        PrimitiveTypeCode.Byte => "u1",
        PrimitiveTypeCode.Int16 => "i2",
        PrimitiveTypeCode.UInt16 => "u2",
        PrimitiveTypeCode.Int32 => "i4",
        PrimitiveTypeCode.UInt32 => "u4",
        PrimitiveTypeCode.Int64 => "i8",
        PrimitiveTypeCode.UInt64 => "u8",
        PrimitiveTypeCode.Single => "r4",
        PrimitiveTypeCode.Double => "r8",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.Void => "void",
        PrimitiveTypeCode.IntPtr => "i",
        PrimitiveTypeCode.UIntPtr => "u",
        PrimitiveTypeCode.TypedReference => "typedref",
        _ => "object",
    };

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var def = reader.GetTypeDefinition(handle);
        string ns = reader.GetString(def.Namespace);
        string name = reader.GetString(def.Name);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var tref = reader.GetTypeReference(handle);
        string ns = reader.GetString(tref.Namespace);
        string name = reader.GetString(tref.Name);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[,]";

    public string GetByReferenceType(string elementType) => elementType + "&";

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr!";

    // Generic instantiations canonicalize to the open generic name (arity
    // form, e.g. "System.Collections.Generic.List`1"): the runtime's
    // intrinsic dispatch is type-argument-agnostic, and ToCode folds any
    // instantiation used as a parameter to "object" anyway.
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        => genericType;

    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

    public string GetPinnedType(string elementType) => elementType;
}

/// <summary>
/// Decodes custom-attribute blobs into the same type codes as
/// <see cref="SignatureProvider"/>. Enums fold to their int32 underlying type
/// (attribute enum args are read as their integer value).
/// </summary>
internal sealed class AttributeTypeProvider : ICustomAttributeTypeProvider<string>
{
    public static readonly AttributeTypeProvider Instance = new();

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) =>
        SignatureProvider.Instance.GetPrimitiveType(typeCode);

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        SignatureProvider.Instance.GetTypeFromDefinition(reader, handle, rawTypeKind);

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        SignatureProvider.Instance.GetTypeFromReference(reader, handle, rawTypeKind);

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetSystemType() => "System.Type";

    public bool IsSystemType(string type) => type == "System.Type";

    public string GetTypeFromSerializedName(string name) => name;

    public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
}
