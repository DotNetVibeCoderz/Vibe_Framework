namespace RustNet.Core;

/// <summary>
/// Marks a method whose body lives in the Rust runtime. The
/// MetadataProcessor emits such methods as internal-call entries in the
/// RNX module; calling them on the desktop CLR throws.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor)]
public sealed class InternalCallAttribute : Attribute
{
}

/// <summary>Thrown when an internal-call stub runs on the desktop CLR.</summary>
public sealed class RuntimeOnlyException : InvalidOperationException
{
    public RuntimeOnlyException()
        : base("This method executes inside the RustNet device runtime, not on the desktop CLR.")
    {
    }
}
