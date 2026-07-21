using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace RustNet.MetadataProcessor;

/// <summary>
/// Compiles one or more .NET assemblies (the app plus any RustNet.* class
/// libraries it references) into a single RNX module: metadata tokens are
/// rewritten into direct table indices so the on-device interpreter never
/// touches ECMA-335 metadata. Format documented in runtime/rustnet-core/src/rnx.rs.
/// </summary>
public sealed class RnxCompiler : IDisposable
{
    private const byte MFlagStatic = 0x01;
    private const byte MFlagInternal = 0x02;
    private const byte MFlagCtor = 0x04;
    private const byte MFlagHasRet = 0x08; // returns a value (non-void)

    private const byte EhCatch = 0;
    private const byte EhFinally = 1;
    private const byte EhFilter = 2;

    private const uint NoType = 0xFFFFFFFF;

    private sealed class TypeEntry
    {
        public required uint NameIdx;
        /// <summary>Total instance fields: own + inherited.</summary>
        public ushort FieldCount;
        public ushort StaticFieldCount;
        public uint Parent = NoType;
        public readonly List<uint> Interfaces = new();
        /// <summary>(root virtual slot method idx, impl method idx).</summary>
        public readonly List<(uint Slot, uint Impl)> Overrides = new();
        /// <summary>Declared fields for reflection (name idx, flags, slot) (v5).</summary>
        public readonly List<(uint NameIdx, byte Flags, uint Slot)> Fields = new();
        /// <summary>User custom attributes applied to this type (v6).</summary>
        public readonly List<AttrRecord> Attrs = new();
    }

    // Field descriptor flags (RNX v5), mirrored in rustnet-core/src/rnx.rs.
    private const byte FFlagStatic = 0x01;
    private const byte FFlagPublic = 0x02;

    /// <summary>A custom attribute applied to a type (RNX v6).</summary>
    private sealed class AttrRecord
    {
        public uint Ctor;
        public readonly List<AttrArg> Fixed = new();
        // Kind: 0 = field, 1 = property.
        public readonly List<(byte Kind, uint NameIdx, AttrArg Val)> Named = new();
    }

    /// <summary>A tagged constant attribute argument (RNX v6). Tag: 0 null,
    /// 1 i32, 2 i64, 3 f64, 4 str-idx, 5 bool.</summary>
    private readonly record struct AttrArg(byte Tag, long I, double D, uint S);

    /// <summary>Hierarchy working data collected before layout.</summary>
    private sealed class TypeInfo
    {
        public required LoadedAssembly Asm;
        public required TypeDefinitionHandle Handle;
        public required string Name;
        public string? BaseName;
        public readonly List<string> InterfaceNames = new();
        public bool IsInterface;
        /// <summary>-1 = not laid out yet; -2 = in progress (cycle guard).</summary>
        public int TotalFields = -1;
    }

    private sealed record EhClause(byte Kind, uint TryStart, uint TryEnd, uint HandlerStart, uint HandlerEnd, uint FilterStart);

    private sealed class MethodEntry
    {
        public required uint NameIdx;
        public ushort OwnerType = 0xFFFF;
        public byte Flags;
        public byte ParamCount;
        public ushort LocalCount;
        public ushort MaxStack;
        public uint Slot;
        public byte[] Code = Array.Empty<byte>();
        public readonly List<EhClause> Eh = new();
        // Virtual-dispatch working data (not serialized):
        public string SimpleName = "";
        public string ParamSig = "";
        public bool IsVirtual;
        public bool IsNewSlot;
    }

    private sealed class LoadedAssembly
    {
        public required string Path;
        public required PEReader Pe;
        public required MetadataReader Md;
        /// <summary>Portable-PDB reader (embedded or side-by-side), for debug
        /// sequence points; null when no symbols are present.</summary>
        public MetadataReader? Pdb;
        public MetadataReaderProvider? PdbProvider;
        public readonly Dictionary<int, uint> MethodIdxByToken = new();
        public readonly Dictionary<int, (bool IsStatic, uint Slot)> FieldByToken = new();
    }

    private readonly List<string> _strings = new();
    private readonly Dictionary<string, uint> _stringMap = new();
    private readonly List<TypeEntry> _types = new();
    private readonly Dictionary<string, ushort> _typeMap = new();
    private readonly List<MethodEntry> _methods = new();
    private readonly Dictionary<string, uint> _methodMap = new();
    /// <summary>Roslyn inline-array span helpers (params with 5+ args) — the
    /// runtime cannot execute their cross-frame byref pattern.</summary>
    private readonly HashSet<uint> _inlineArrayHelpers = new();
    private readonly Dictionary<string, (bool IsStatic, uint Slot)> _fieldMap = new();
    private readonly Dictionary<string, TypeInfo> _typeInfo = new();
    private readonly List<string> _typeOrder = new();
    private readonly Dictionary<string, List<uint>> _methodsByType = new();
    /// <summary>Debug sequence points: method index -&gt; (IL offset, source line).</summary>
    private readonly Dictionary<uint, List<(uint Il, uint Line)>> _debug = new();
    private readonly List<LoadedAssembly> _assemblies = new();
    private uint _staticSlots;
    private uint? _entryMethod;

    public IReadOnlyList<string> Warnings => _warnings;
    private readonly List<string> _warnings = new();

    /// <summary>
    /// Compile <paramref name="primaryAssembly"/> and every RustNet.* /
    /// user assembly next to it into an RNX byte image.
    /// </summary>
    public static byte[] Compile(string primaryAssembly, out IReadOnlyList<string> warnings)
    {
        using var compiler = new RnxCompiler();
        compiler.LoadWithReferences(primaryAssembly);
        byte[] result = compiler.Emit();
        warnings = compiler.Warnings.ToArray();
        return result;
    }

    public void LoadWithReferences(string primaryAssembly)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(primaryAssembly)) ?? ".";
        var queue = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        queue.Enqueue(Path.GetFullPath(primaryAssembly));
        while (queue.Count > 0)
        {
            string path = queue.Dequeue();
            if (!seen.Add(Path.GetFileNameWithoutExtension(path)))
            {
                continue;
            }
            var asm = Open(path);
            _assemblies.Add(asm);
            foreach (var handle in asm.Md.AssemblyReferences)
            {
                string name = asm.Md.GetString(asm.Md.GetAssemblyReference(handle).Name);
                if (IsFrameworkAssembly(name))
                {
                    continue;
                }
                string candidate = Path.Combine(dir, name + ".dll");
                if (File.Exists(candidate))
                {
                    queue.Enqueue(candidate);
                }
            }
        }
        RegisterTypesAndFields();
        RegisterMethods();
        CollectCustomAttributes();
        FixupVirtualSlots();
        CompileBodies();
        ResolveEntryPoint();
    }

    /// <summary>
    /// Record user-defined custom attributes applied to each type (RNX v6):
    /// the ctor method index plus decoded positional/named constant args.
    /// Framework attributes (not in the user type map) and any attribute with
    /// an unsupported argument type are skipped.
    /// </summary>
    private void CollectCustomAttributes()
    {
        foreach (var (typeName, info) in _typeInfo)
        {
            if (!_typeMap.TryGetValue(typeName, out ushort tIdx))
            {
                continue;
            }
            var md = info.Asm.Md;
            var type = md.GetTypeDefinition(info.Handle);
            var entry = _types[tIdx];
            foreach (var cah in type.GetCustomAttributes())
            {
                var ca = md.GetCustomAttribute(cah);
                string attrType = ca.Constructor.Kind switch
                {
                    HandleKind.MemberReference => TypeRefOrDefName(md,
                        md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent),
                    HandleKind.MethodDefinition => TypeRefOrDefName(md,
                        md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).GetDeclaringType()),
                    _ => "",
                };
                // Only user-defined attributes can be constructed by the runtime.
                if (!_typeMap.ContainsKey(attrType))
                {
                    continue;
                }
                uint ctorIdx;
                CustomAttributeValue<string> value;
                try
                {
                    ctorIdx = ResolveMethodToken(info.Asm,
                        MetadataTokens.GetToken(ca.Constructor), $"attribute {attrType}");
                    value = ca.DecodeValue(AttributeTypeProvider.Instance);
                }
                catch
                {
                    continue;
                }
                var rec = new AttrRecord { Ctor = ctorIdx };
                bool ok = true;
                foreach (var fa in value.FixedArguments)
                {
                    var a = ToAttrArg(fa.Value);
                    if (a is null) { ok = false; break; }
                    rec.Fixed.Add(a.Value);
                }
                if (!ok)
                {
                    continue;
                }
                foreach (var na in value.NamedArguments)
                {
                    var a = ToAttrArg(na.Value);
                    if (a is null) { ok = false; break; }
                    byte kind = na.Kind == CustomAttributeNamedArgumentKind.Field ? (byte)0 : (byte)1;
                    rec.Named.Add((kind, InternString(na.Name ?? ""), a.Value));
                }
                if (!ok)
                {
                    continue;
                }
                entry.Attrs.Add(rec);
            }
        }
    }

    /// <summary>Map a decoded attribute-argument CLR value to a tagged
    /// <see cref="AttrArg"/>, or null when its type is not supported.</summary>
    private AttrArg? ToAttrArg(object? value) => value switch
    {
        null => new AttrArg(0, 0, 0, 0),
        bool b => new AttrArg(5, b ? 1 : 0, 0, 0),
        sbyte or byte or short or ushort or int or uint or char =>
            new AttrArg(1, Convert.ToInt64(value), 0, 0),
        long l => new AttrArg(2, l, 0, 0),
        ulong ul => new AttrArg(2, unchecked((long)ul), 0, 0),
        float f => new AttrArg(3, 0, f, 0),
        double d => new AttrArg(3, 0, d, 0),
        string s => new AttrArg(4, 0, 0, InternString(s)),
        _ => null, // System.Type args, arrays, etc. — unsupported
    };

    private static void WriteAttrArg(BinaryWriter w, AttrArg a)
    {
        w.Write(a.Tag);
        switch (a.Tag)
        {
            case 1: w.Write((int)a.I); break;
            case 2: w.Write(a.I); break;
            case 3: w.Write(a.D); break;
            case 4: w.Write(a.S); break;
            case 5: w.Write((byte)a.I); break;
        }
    }

    private static bool IsFrameworkAssembly(string name)
        => name is "mscorlib" or "netstandard" or "System" or "System.Runtime"
            || name.StartsWith("System.", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.", StringComparison.Ordinal);

    private static LoadedAssembly Open(string path)
    {
        var pe = new PEReader(File.OpenRead(path));
        MetadataReader? pdb = null;
        MetadataReaderProvider? provider = null;
        try
        {
            if (pe.TryOpenAssociatedPortablePdb(
                    Path.GetFullPath(path),
                    p => File.Exists(p) ? File.OpenRead(p) : null,
                    out provider, out _)
                && provider is not null)
            {
                pdb = provider.GetMetadataReader();
            }
        }
        catch
        {
            provider = null; // symbols are optional
        }
        return new LoadedAssembly
        {
            Path = path,
            Pe = pe,
            Md = pe.GetMetadataReader(),
            Pdb = pdb,
            PdbProvider = provider,
        };
    }

    // ------------------------------------------------------------------
    // Pass A: types & fields
    // ------------------------------------------------------------------

    private void RegisterTypesAndFields()
    {
        // Pass 1: collect every type with its base/interface names and
        // assign stable indices.
        foreach (var asm in _assemblies)
        {
            var md = asm.Md;
            foreach (var th in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(th);
                string name = FullTypeName(md, type);
                if (name == "<Module>" || _typeMap.ContainsKey(name))
                {
                    continue;
                }
                var info = new TypeInfo
                {
                    Asm = asm,
                    Handle = th,
                    Name = name,
                    IsInterface = (type.Attributes & TypeAttributes.Interface) != 0,
                };
                if (!type.BaseType.IsNil)
                {
                    string baseName = TypeRefOrDefName(md, type.BaseType);
                    // Roots of the managed object model carry no layout.
                    if (baseName is not ("System.Object" or "System.ValueType" or "System.Enum"
                        or "System.MulticastDelegate" or "System.Delegate" or "System.Attribute"))
                    {
                        info.BaseName = baseName;
                    }
                }
                foreach (var iih in type.GetInterfaceImplementations())
                {
                    var ii = md.GetInterfaceImplementation(iih);
                    info.InterfaceNames.Add(TypeRefOrDefName(md, ii.Interface));
                }
                _typeMap[name] = (ushort)_types.Count;
                _typeOrder.Add(name);
                _typeInfo[name] = info;
                _types.Add(new TypeEntry { NameIdx = InternString(name) });
            }
        }

        // Pass 2: field layout, bases first so derived slots start after
        // inherited ones.
        foreach (string name in _typeOrder)
        {
            LayoutFields(_typeInfo[name]);
        }

        // Pass 3: parent indices and flattened interface lists.
        foreach (string name in _typeOrder)
        {
            var info = _typeInfo[name];
            var entry = _types[_typeMap[name]];
            if (info.BaseName is not null && _typeMap.TryGetValue(info.BaseName, out ushort p))
            {
                entry.Parent = p;
            }
            else if (info.BaseName is not null)
            {
                _warnings.Add(
                    $"type {name}: base {info.BaseName} is outside the compiled set; " +
                    "inherited members resolve statically");
            }
            var flat = new HashSet<uint>();
            CollectInterfaces(info, flat);
            entry.Interfaces.AddRange(flat);
        }
    }

    /// <summary>Assign field slots (base-first); returns total instance fields.</summary>
    private int LayoutFields(TypeInfo info)
    {
        if (info.TotalFields >= 0)
        {
            return info.TotalFields;
        }
        if (info.TotalFields == -2)
        {
            throw new InvalidOperationException($"type {info.Name}: inheritance cycle");
        }
        info.TotalFields = -2;
        int slot = 0;
        if (info.BaseName is not null && _typeInfo.TryGetValue(info.BaseName, out var baseInfo))
        {
            slot = LayoutFields(baseInfo);
        }
        var md = info.Asm.Md;
        var type = md.GetTypeDefinition(info.Handle);
        ushort staticCount = 0;
        foreach (var fh in type.GetFields())
        {
            var field = md.GetFieldDefinition(fh);
            var attrs = field.Attributes;
            if ((attrs & FieldAttributes.Literal) != 0)
            {
                continue; // consts are inlined by the compiler
            }
            string simpleName = md.GetString(field.Name);
            string fieldName = info.Name + "::" + simpleName;
            bool isStatic = (attrs & FieldAttributes.Static) != 0;
            bool isPublic = (attrs & FieldAttributes.FieldAccessMask) == FieldAttributes.Public;
            (bool, uint) slotInfo;
            if (isStatic)
            {
                slotInfo = (true, _staticSlots++);
                staticCount++;
            }
            else
            {
                slotInfo = (false, (uint)slot++);
            }
            _fieldMap[fieldName] = slotInfo;
            info.Asm.FieldByToken[MetadataTokens.GetToken(fh)] = slotInfo;
            // Reflection descriptor (v5): name + flags + resolved slot.
            byte fflags = 0;
            if (isStatic) fflags |= FFlagStatic;
            if (isPublic) fflags |= FFlagPublic;
            _types[_typeMap[info.Name]].Fields.Add((InternString(simpleName), fflags, slotInfo.Item2));
        }
        var entry = _types[_typeMap[info.Name]];
        entry.FieldCount = (ushort)slot;
        entry.StaticFieldCount = staticCount;
        info.TotalFields = slot;
        return slot;
    }

    /// <summary>Direct interfaces plus everything they extend (flattened).</summary>
    private void CollectInterfaces(TypeInfo info, HashSet<uint> into)
    {
        foreach (string iname in info.InterfaceNames)
        {
            if (_typeMap.TryGetValue(iname, out ushort idx) && into.Add(idx))
            {
                CollectInterfaces(_typeInfo[iname], into);
            }
        }
    }

    // ------------------------------------------------------------------
    // Pass B: method registration (canonical names -> indices)
    // ------------------------------------------------------------------

    private void RegisterMethods()
    {
        foreach (var asm in _assemblies)
        {
            var md = asm.Md;
            foreach (var th in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(th);
                string typeName = FullTypeName(md, type);
                if (typeName == "<Module>")
                {
                    continue;
                }
                foreach (var mh in type.GetMethods())
                {
                    var method = md.GetMethodDefinition(mh);
                    string methodName = md.GetString(method.Name);
                    var sig = method.DecodeSignature(SignatureProvider.Instance, null);
                    string paramSig = string.Join(",", sig.ParameterTypes.Select(SignatureProvider.ToCode));
                    string canonical = $"{typeName}::{methodName}({paramSig})";
                    bool isStatic = (method.Attributes & MethodAttributes.Static) != 0;
                    // The compiler-generated inline-array span helpers have IL
                    // bodies, but the interpreter models the buffer as a heap
                    // array and services them as intrinsics — force them internal
                    // so their (unsupported) IL is never executed.
                    bool isInlineArrayHelper = typeName == "<PrivateImplementationDetails>"
                        && methodName.StartsWith("InlineArray", StringComparison.Ordinal);
                    bool isInternal = method.RelativeVirtualAddress == 0
                        || HasInternalCallAttribute(md, method) || isInlineArrayHelper;
                    byte flags = 0;
                    if (isStatic) flags |= MFlagStatic;
                    if (isInternal) flags |= MFlagInternal;
                    if (methodName == ".ctor") flags |= MFlagCtor;
                    if (sig.ReturnType != "void") flags |= MFlagHasRet;
                    var entry = new MethodEntry
                    {
                        NameIdx = InternString(canonical),
                        OwnerType = _typeMap.TryGetValue(typeName, out var ti) ? ti : (ushort)0xFFFF,
                        Flags = flags,
                        ParamCount = (byte)sig.ParameterTypes.Length,
                        SimpleName = methodName,
                        ParamSig = paramSig,
                        IsVirtual = (method.Attributes & MethodAttributes.Virtual) != 0,
                        IsNewSlot = (method.Attributes & MethodAttributes.NewSlot) != 0,
                    };
                    if (_methodMap.ContainsKey(canonical))
                    {
                        throw new InvalidOperationException(
                            $"duplicate method signature after canonicalization: {canonical} " +
                            "(overloads differing only in class-typed parameters are not supported)");
                    }
                    uint idx = (uint)_methods.Count;
                    entry.Slot = idx;
                    _methods.Add(entry);
                    _methodMap[canonical] = idx;
                    asm.MethodIdxByToken[MetadataTokens.GetToken(mh)] = idx;
                    if (!_methodsByType.TryGetValue(typeName, out var list))
                    {
                        _methodsByType[typeName] = list = new List<uint>();
                    }
                    list.Add(idx);
                    if (typeName == "<PrivateImplementationDetails>"
                        && methodName.StartsWith("InlineArray", StringComparison.Ordinal))
                    {
                        _inlineArrayHelpers.Add(idx);
                    }
                }
            }
        }
    }

    private static bool HasInternalCallAttribute(MetadataReader md, MethodDefinition method)
    {
        foreach (var cah in method.GetCustomAttributes())
        {
            var ca = md.GetCustomAttribute(cah);
            string attrType = ca.Constructor.Kind switch
            {
                HandleKind.MemberReference => TypeRefOrDefName(md, md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent),
                HandleKind.MethodDefinition => TypeRefOrDefName(md,
                    md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).GetDeclaringType()),
                _ => "",
            };
            if (attrType == "RustNet.Core.InternalCallAttribute")
            {
                return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------
    // Pass B2: virtual slots & override tables (dynamic dispatch)
    // ------------------------------------------------------------------

    /// <summary>Find `name(paramSig)` on `typeName` or an ancestor.</summary>
    private uint? FindMethodInChain(string? typeName, string simpleName, string paramSig)
    {
        while (typeName is not null)
        {
            if (_methodsByType.TryGetValue(typeName, out var list))
            {
                foreach (uint idx in list)
                {
                    var m = _methods[(int)idx];
                    if (m.SimpleName == simpleName && m.ParamSig == paramSig)
                    {
                        return idx;
                    }
                }
            }
            typeName = _typeInfo.TryGetValue(typeName, out var info) ? info.BaseName : null;
        }
        return null;
    }

    /// <summary>Register (or reuse) an internal method entry by canonical name —
    /// used for virtual roots living outside the compiled set (System.Object).</summary>
    private uint GetOrRegisterExternal(string canonical, byte paramCount, bool isStatic)
    {
        if (_methodMap.TryGetValue(canonical, out uint existing))
        {
            return existing;
        }
        byte flags = MFlagInternal;
        if (isStatic) flags |= MFlagStatic;
        var entry = new MethodEntry
        {
            NameIdx = InternString(canonical),
            Flags = flags,
            ParamCount = paramCount,
        };
        uint idx = (uint)_methods.Count;
        entry.Slot = idx;
        _methods.Add(entry);
        _methodMap[canonical] = idx;
        return idx;
    }

    private void FixupVirtualSlots()
    {
        // Bases first so a base override chain has final slots before
        // derived types consult them.
        foreach (string typeName in TopologicalTypes())
        {
            var info = _typeInfo[typeName];
            var entry = _types[_typeMap[typeName]];
            if (!_methodsByType.TryGetValue(typeName, out var methods))
            {
                methods = new List<uint>();
            }
            foreach (uint idx in methods)
            {
                var m = _methods[(int)idx];
                if (!m.IsVirtual || (m.Flags & MFlagStatic) != 0)
                {
                    continue;
                }
                if (!m.IsNewSlot)
                {
                    // Implicit override of a base virtual.
                    uint? baseIdx = FindMethodInChain(info.BaseName, m.SimpleName, m.ParamSig);
                    if (baseIdx is not null)
                    {
                        m.Slot = _methods[(int)baseIdx.Value].Slot;
                        entry.Overrides.Add((m.Slot, idx));
                        continue;
                    }
                    // Overriding a virtual outside the compiled set:
                    // System.Object's ToString/Equals/GetHashCode.
                    uint root = GetOrRegisterExternal(
                        $"System.Object::{m.SimpleName}({m.ParamSig})", m.ParamCount, false);
                    m.Slot = root;
                    entry.Overrides.Add((root, idx));
                }
            }
            if (info.IsInterface)
            {
                continue;
            }
            // Interface satisfaction (implicit): map every method of every
            // implemented interface onto this type's matching method.
            foreach (uint ifaceIdx in entry.Interfaces)
            {
                string ifaceName = _typeOrder[(int)ifaceIdx];
                if (!_methodsByType.TryGetValue(ifaceName, out var ifaceMethods))
                {
                    continue;
                }
                foreach (uint imIdx in ifaceMethods)
                {
                    var im = _methods[(int)imIdx];
                    uint? impl = FindMethodInChain(typeName, im.SimpleName, im.ParamSig);
                    if (impl is not null && impl.Value != imIdx)
                    {
                        entry.Overrides.Add((im.Slot, impl.Value));
                    }
                }
            }
            // Explicit implementations / overrides (MethodImpl table).
            var md = info.Asm.Md;
            foreach (var mih in md.GetTypeDefinition(info.Handle).GetMethodImplementations())
            {
                var mi = md.GetMethodImplementation(mih);
                if (mi.MethodBody.Kind != HandleKind.MethodDefinition)
                {
                    continue;
                }
                uint bodyIdx = info.Asm.MethodIdxByToken[MetadataTokens.GetToken(mi.MethodBody)];
                uint? declIdx = mi.MethodDeclaration.Kind switch
                {
                    HandleKind.MethodDefinition =>
                        info.Asm.MethodIdxByToken.TryGetValue(
                            MetadataTokens.GetToken(mi.MethodDeclaration), out uint d) ? d : null,
                    HandleKind.MemberReference => ResolveDeclRef(info.Asm, mi.MethodDeclaration),
                    _ => null,
                };
                if (declIdx is not null)
                {
                    _methods[(int)bodyIdx].Slot = _methods[(int)declIdx.Value].Slot;
                    entry.Overrides.Add((_methods[(int)declIdx.Value].Slot, bodyIdx));
                }
            }
            // First mapping for a slot wins (list order: own overrides first).
            var seen = new HashSet<uint>();
            var dedup = new List<(uint, uint)>();
            foreach (var pair in entry.Overrides)
            {
                if (seen.Add(pair.Slot))
                {
                    dedup.Add(pair);
                }
            }
            entry.Overrides.Clear();
            entry.Overrides.AddRange(dedup);
        }
    }

    private uint? ResolveDeclRef(LoadedAssembly asm, EntityHandle handle)
    {
        var mref = asm.Md.GetMemberReference((MemberReferenceHandle)handle);
        string typeName = TypeRefOrDefName(asm.Md, mref.Parent);
        string name = asm.Md.GetString(mref.Name);
        var sig = mref.DecodeMethodSignature(SignatureProvider.Instance, null);
        string canonical = Canonical(typeName, name, sig.ParameterTypes.Select(SignatureProvider.ToCode));
        return _methodMap.TryGetValue(canonical, out uint idx) ? idx : null;
    }

    /// <summary>Types ordered so every base precedes its subclasses.</summary>
    private IEnumerable<string> TopologicalTypes()
    {
        var emitted = new HashSet<string>();
        var result = new List<string>();
        void Visit(string name)
        {
            if (!emitted.Add(name))
            {
                return;
            }
            if (_typeInfo[name].BaseName is string b && _typeInfo.ContainsKey(b))
            {
                Visit(b);
            }
            result.Add(name);
        }
        foreach (string name in _typeOrder)
        {
            Visit(name);
        }
        return result;
    }

    // ------------------------------------------------------------------
    // Pass C: IL rewriting
    // ------------------------------------------------------------------

    private void CompileBodies()
    {
        foreach (var asm in _assemblies)
        {
            var md = asm.Md;
            foreach (var th in md.TypeDefinitions)
            {
                foreach (var mh in md.GetTypeDefinition(th).GetMethods())
                {
                    var method = md.GetMethodDefinition(mh);
                    uint idx = asm.MethodIdxByToken[MetadataTokens.GetToken(mh)];
                    var entry = _methods[(int)idx];
                    if ((entry.Flags & MFlagInternal) != 0 || method.RelativeVirtualAddress == 0)
                    {
                        continue;
                    }
                    var body = asm.Pe.GetMethodBody(method.RelativeVirtualAddress);
                    foreach (var region in body.ExceptionRegions)
                    {
                        byte ehKind = region.Kind switch
                        {
                            ExceptionRegionKind.Catch => EhCatch,
                            ExceptionRegionKind.Finally => EhFinally,
                            ExceptionRegionKind.Filter => EhFilter,
                            _ => throw new InvalidOperationException(
                                $"{_strings[(int)entry.NameIdx]}: fault handlers are not supported"),
                        };
                        entry.Eh.Add(new EhClause(
                            ehKind,
                            (uint)region.TryOffset,
                            (uint)(region.TryOffset + region.TryLength),
                            (uint)region.HandlerOffset,
                            (uint)(region.HandlerOffset + region.HandlerLength),
                            ehKind == EhFilter ? (uint)region.FilterOffset : 0));
                    }
                    entry.MaxStack = (ushort)body.MaxStack;
                    entry.LocalCount = (ushort)LocalCount(md, body);
                    byte[]? il = body.GetILBytes();
                    entry.Code = RewriteIL(asm, il ?? Array.Empty<byte>(), _strings[(int)entry.NameIdx]);
                    CollectSequencePoints(asm, mh, idx);
                }
            }
        }
    }

    /// <summary>Read a method's PDB sequence points (IL offset -&gt; source
    /// line) into <see cref="_debug"/>. IL offsets survive token rewriting
    /// (tokens are 4 bytes rewritten in place), so they stay valid.</summary>
    private void CollectSequencePoints(LoadedAssembly asm, MethodDefinitionHandle mh, uint idx)
    {
        if (asm.Pdb is null)
        {
            return;
        }
        var dh = mh.ToDebugInformationHandle();
        MethodDebugInformation dbg;
        try
        {
            dbg = asm.Pdb.GetMethodDebugInformation(dh);
        }
        catch
        {
            return;
        }
        if (dbg.SequencePointsBlob.IsNil)
        {
            return;
        }
        var points = new List<(uint Il, uint Line)>();
        foreach (var sp in dbg.GetSequencePoints())
        {
            if (sp.IsHidden)
            {
                continue;
            }
            points.Add(((uint)sp.Offset, (uint)sp.StartLine));
        }
        if (points.Count > 0)
        {
            _debug[idx] = points;
        }
    }

    private static int LocalCount(MetadataReader md, MethodBodyBlock body)
    {
        if (body.LocalSignature.IsNil)
        {
            return 0;
        }
        var sig = md.GetStandaloneSignature(body.LocalSignature);
        return sig.DecodeLocalSignature(SignatureProvider.Instance, null).Length;
    }

    private byte[] RewriteIL(LoadedAssembly asm, byte[] il, string methodName)
    {
        byte[] output = (byte[])il.Clone();
        int pos = 0;
        while (pos < il.Length)
        {
            byte op = il[pos];
            OperandKind? kind;
            int operandPos;
            if (op == 0xFE)
            {
                if (pos + 1 >= il.Length)
                {
                    throw new InvalidOperationException($"{methodName}: truncated prefixed opcode");
                }
                kind = OpcodeTable.LookupPrefixed(il[pos + 1]);
                operandPos = pos + 2;
            }
            else
            {
                kind = OpcodeTable.Lookup(op);
                operandPos = pos + 1;
            }
            if (kind is null)
            {
                throw new InvalidOperationException($"{methodName}: unsupported IL opcode 0x{op:X2} at offset {pos}");
            }
            switch (kind.Value)
            {
                case OperandKind.None:
                    pos = operandPos;
                    break;
                case OperandKind.ByteVar:
                case OperandKind.ShortBranch:
                    pos = operandPos + 1;
                    break;
                case OperandKind.Var2:
                    pos = operandPos + 2;
                    break;
                case OperandKind.Int32:
                case OperandKind.Float32:
                case OperandKind.Branch32:
                    pos = operandPos + 4;
                    break;
                case OperandKind.Int64:
                case OperandKind.Float64:
                    pos = operandPos + 8;
                    break;
                case OperandKind.Switch:
                {
                    uint count = BitConverter.ToUInt32(il, operandPos);
                    pos = operandPos + 4 + (int)count * 4;
                    break;
                }
                case OperandKind.TokenSig:
                    throw new InvalidOperationException($"{methodName}: calli is not supported");
                case OperandKind.TokenAny:
                {
                    // ldtoken. `typeof(T)` targets a type handle -> resolve to a
                    // runtime type reference. Method/field handles (array
                    // initializers via InitializeArray, reflection member
                    // handles) stay unsupported.
                    int token = BitConverter.ToInt32(il, operandPos);
                    var handle = MetadataTokens.EntityHandle(token);
                    if (handle.Kind is HandleKind.TypeDefinition
                        or HandleKind.TypeReference or HandleKind.TypeSpecification)
                    {
                        WriteU32(output, operandPos, ResolveTypeofToken(asm, token));
                        pos = operandPos + 4;
                        break;
                    }
                    throw new InvalidOperationException(
                        $"{methodName}: ldtoken of a method or field (array initializer " +
                        "or reflection handle) is not supported");
                }
                case OperandKind.TokenString:
                {
                    int token = BitConverter.ToInt32(il, operandPos);
                    string value = asm.Md.GetUserString(MetadataTokens.UserStringHandle(token));
                    WriteU32(output, operandPos, InternString(value));
                    pos = operandPos + 4;
                    break;
                }
                case OperandKind.TokenMethod:
                {
                    int token = BitConverter.ToInt32(il, operandPos);
                    uint idx = ResolveMethodToken(asm, token, methodName);
                    // Inline-array span helpers (`<PrivateImplementationDetails>.
                    // InlineArray*`) are handled by the interpreter now — the
                    // buffer is modelled as a heap array. No rewrite needed here.
                    WriteU32(output, operandPos, idx);
                    pos = operandPos + 4;
                    break;
                }
                case OperandKind.TokenField:
                {
                    int token = BitConverter.ToInt32(il, operandPos);
                    var (isStatic, slot) = ResolveFieldToken(asm, token, methodName);
                    bool staticInstr = op is 0x7E or 0x7F or 0x80;
                    if (isStatic != staticInstr)
                    {
                        throw new InvalidOperationException(
                            $"{methodName}: field static-ness mismatch at offset {pos}");
                    }
                    WriteU32(output, operandPos, slot);
                    pos = operandPos + 4;
                    break;
                }
                case OperandKind.TokenType:
                {
                    int token = BitConverter.ToInt32(il, operandPos);
                    // `initobj <>y__InlineArrayN<T>` (0xFE 0x15): mark the operand
                    // with the high bit + N so the interpreter allocates a heap
                    // array as the inline-array buffer.
                    bool isInitObj = op == 0xFE && pos + 1 < il.Length && il[pos + 1] == 0x15;
                    if (isInitObj)
                    {
                        int n = InlineArraySize(asm, token);
                        if (n > 0)
                        {
                            WriteU32(output, operandPos, 0x8000_0000u | (uint)n);
                            pos = operandPos + 4;
                            break;
                        }
                    }
                    uint value = ResolveTypeToken(asm, token, op, il, pos);
                    WriteU32(output, operandPos, value);
                    pos = operandPos + 4;
                    break;
                }
                default:
                    throw new InvalidOperationException($"{methodName}: unhandled operand kind {kind}");
            }
        }
        return output;
    }

    private uint ResolveMethodToken(LoadedAssembly asm, int token, string context)
    {
        var handle = MetadataTokens.EntityHandle(token);
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
                return asm.MethodIdxByToken[token];
            case HandleKind.MemberReference:
            {
                var mref = asm.Md.GetMemberReference((MemberReferenceHandle)handle);
                string typeName = TypeRefOrDefName(asm.Md, mref.Parent);
                string name = asm.Md.GetString(mref.Name);
                var sig = mref.DecodeMethodSignature(SignatureProvider.Instance, null);
                string canonical = Canonical(typeName, name, sig.ParameterTypes.Select(SignatureProvider.ToCode));
                if (_methodMap.TryGetValue(canonical, out uint existing))
                {
                    return existing;
                }
                // Unknown target: register as an internal call handled by the runtime/host.
                byte flags = MFlagInternal;
                if (!sig.Header.IsInstance) flags |= MFlagStatic;
                if (name == ".ctor") flags |= MFlagCtor;
                var entry = new MethodEntry
                {
                    NameIdx = InternString(canonical),
                    Flags = flags,
                    ParamCount = (byte)sig.ParameterTypes.Length,
                };
                uint idx = (uint)_methods.Count;
                entry.Slot = idx;
                _methods.Add(entry);
                _methodMap[canonical] = idx;
                return idx;
            }
            case HandleKind.MethodSpecification:
            {
                // Generic method instantiation (e.g. Enumerable.Where<int>):
                // canonicalization is type-argument-agnostic, so resolve the
                // underlying generic method definition/reference.
                var spec = asm.Md.GetMethodSpecification((MethodSpecificationHandle)handle);
                return ResolveMethodToken(asm, MetadataTokens.GetToken(spec.Method), context);
            }
            default:
                throw new InvalidOperationException($"{context}: unsupported method token kind {handle.Kind}");
        }
    }

    private (bool IsStatic, uint Slot) ResolveFieldToken(LoadedAssembly asm, int token, string context)
    {
        var handle = MetadataTokens.EntityHandle(token);
        switch (handle.Kind)
        {
            case HandleKind.FieldDefinition:
                return asm.FieldByToken[token];
            case HandleKind.MemberReference:
            {
                var mref = asm.Md.GetMemberReference((MemberReferenceHandle)handle);
                string canonical = TypeRefOrDefName(asm.Md, mref.Parent) + "::" + asm.Md.GetString(mref.Name);
                if (_fieldMap.TryGetValue(canonical, out var info))
                {
                    return info;
                }
                throw new InvalidOperationException($"{context}: unresolved field reference {canonical}");
            }
            default:
                throw new InvalidOperationException($"{context}: unsupported field token kind {handle.Kind}");
        }
    }

    private uint ResolveTypeToken(LoadedAssembly asm, int token, byte op, byte[] il, int pos)
    {
        // newarr/box/unbox.any/ldelem/stelem/ldelema want an element-type code;
        // castclass/isinst want a type index; the rest are ignored by the runtime.
        string typeName = TypeNameFromToken(asm, token);
        bool wantsElemCode = op is 0x8D or 0x8C or 0xA5 or 0xA3 or 0xA4 or 0x8F;
        if (wantsElemCode)
        {
            return ElementCode(typeName);
        }
        bool wantsTypeIndex = op is 0x74 or 0x75; // castclass / isinst
        if (wantsTypeIndex)
        {
            return _typeMap.TryGetValue(typeName, out var idx) ? idx : 0xFFFFu;
        }
        _ = il;
        _ = pos;
        return 0;
    }

    private static uint ElementCode(string typeName) => typeName switch
    {
        "i1" => 0,
        "u1" => 1,
        "i2" => 2,
        "u2" => 3,
        "i4" => 4,
        "u4" => 5,
        "i8" => 6,
        "u8" => 7,
        "r4" => 8,
        "r8" => 9,
        "bool" => 10,
        "char" => 11,
        _ => 12, // reference type
    };

    /// <summary>
    /// Encode a `typeof(T)` type token for the interpreter's `ldtoken`: a bare
    /// RNX type index for a user type, or (high bit set) a string-table index
    /// holding a BCL/external type's full name.
    /// </summary>
    private uint ResolveTypeofToken(LoadedAssembly asm, int token)
    {
        string full = TypeofFullName(asm, token);
        if (_typeMap.TryGetValue(full, out ushort idx))
        {
            return idx;
        }
        return 0x8000_0000u | InternString(full);
    }

    /// <summary>Full type name for a `typeof` token — keeps `System.Int32`
    /// (unlike <see cref="TypeNameFromToken"/>, which folds primitives to
    /// element codes for newarr/box).</summary>
    private static string TypeofFullName(LoadedAssembly asm, int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => SignatureProvider.Instance.GetTypeFromDefinition(
                asm.Md, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeReference => SignatureProvider.Instance.GetTypeFromReference(
                asm.Md, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => asm.Md
                .GetTypeSpecification((TypeSpecificationHandle)handle)
                .DecodeSignature(SignatureProvider.Instance, null),
            _ => "object",
        };
    }

    /// <summary>Size N of an `<>y__InlineArrayN&lt;T&gt;` buffer type, parsed
    /// from its name, or -1 if the token is not an inline-array type.</summary>
    private static int InlineArraySize(LoadedAssembly asm, int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        string name = handle.Kind switch
        {
            HandleKind.TypeSpecification => asm.Md
                .GetTypeSpecification((TypeSpecificationHandle)handle)
                .DecodeSignature(SignatureProvider.Instance, null),
            HandleKind.TypeDefinition => SignatureProvider.Instance.GetTypeFromDefinition(
                asm.Md, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeReference => SignatureProvider.Instance.GetTypeFromReference(
                asm.Md, (TypeReferenceHandle)handle, 0),
            _ => "",
        };
        int idx = name.IndexOf("InlineArray", StringComparison.Ordinal);
        if (idx < 0)
        {
            return -1;
        }
        int p = idx + "InlineArray".Length;
        int n = 0;
        bool any = false;
        while (p < name.Length && char.IsDigit(name[p]))
        {
            n = (n * 10) + (name[p] - '0');
            p++;
            any = true;
        }
        return any ? n : -1;
    }

    private string TypeNameFromToken(LoadedAssembly asm, int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => SignatureProvider.Instance.GetTypeFromDefinition(asm.Md, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeReference => MapWellKnown(SignatureProvider.Instance.GetTypeFromReference(asm.Md, (TypeReferenceHandle)handle, 0)),
            HandleKind.TypeSpecification => "object",
            _ => "object",
        };
    }

    private static string MapWellKnown(string fullName) => fullName switch
    {
        "System.SByte" => "i1",
        "System.Byte" => "u1",
        "System.Int16" => "i2",
        "System.UInt16" => "u2",
        "System.Int32" => "i4",
        "System.UInt32" => "u4",
        "System.Int64" => "i8",
        "System.UInt64" => "u8",
        "System.Single" => "r4",
        "System.Double" => "r8",
        "System.Boolean" => "bool",
        "System.Char" => "char",
        "System.String" => "string",
        "System.Object" => "object",
        _ => fullName,
    };

    private void ResolveEntryPoint()
    {
        foreach (var asm in _assemblies)
        {
            var cor = asm.Pe.PEHeaders.CorHeader;
            if (cor is null || cor.EntryPointTokenOrRelativeVirtualAddress == 0)
            {
                continue;
            }
            if ((cor.Flags & CorFlags.NativeEntryPoint) != 0)
            {
                continue;
            }
            int token = cor.EntryPointTokenOrRelativeVirtualAddress;
            if (asm.MethodIdxByToken.TryGetValue(token, out uint idx))
            {
                var entry = _methods[(int)idx];
                if (entry.ParamCount != 0)
                {
                    throw new InvalidOperationException(
                        "entry point must be 'static void Main()' without parameters for RustNet apps");
                }
                _entryMethod = idx;
                return;
            }
        }
    }

    // ------------------------------------------------------------------
    // Emit
    // ------------------------------------------------------------------

    public byte[] Emit()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RNX1"u8);
        w.Write((ushort)6); // version (6 = custom attributes)
        w.Write((ushort)0); // flags
        w.Write(_staticSlots);
        w.Write((uint)_strings.Count);
        foreach (string s in _strings)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s);
            w.Write((uint)bytes.Length);
            w.Write(bytes);
        }
        w.Write((uint)_types.Count);
        foreach (var t in _types)
        {
            w.Write(t.NameIdx);
            w.Write(t.FieldCount);
            w.Write(t.StaticFieldCount);
            w.Write(t.Parent);
            w.Write((ushort)t.Interfaces.Count);
            foreach (uint i in t.Interfaces)
            {
                w.Write(i);
            }
            w.Write((ushort)t.Overrides.Count);
            foreach (var (slot, impl) in t.Overrides)
            {
                w.Write(slot);
                w.Write(impl);
            }
            // Field descriptors (v5).
            w.Write((ushort)t.Fields.Count);
            foreach (var (nameIdx, fflags, fslot) in t.Fields)
            {
                w.Write(nameIdx);
                w.Write(fflags);
                w.Write(fslot);
            }
            // Custom attributes (v6).
            w.Write((ushort)t.Attrs.Count);
            foreach (var a in t.Attrs)
            {
                w.Write(a.Ctor);
                w.Write((ushort)a.Fixed.Count);
                foreach (var arg in a.Fixed)
                {
                    WriteAttrArg(w, arg);
                }
                w.Write((ushort)a.Named.Count);
                foreach (var (kind, nameIdx, arg) in a.Named)
                {
                    w.Write(kind);
                    w.Write(nameIdx);
                    WriteAttrArg(w, arg);
                }
            }
        }
        w.Write((uint)_methods.Count);
        foreach (var m in _methods)
        {
            w.Write(m.NameIdx);
            w.Write(m.OwnerType);
            w.Write(m.Flags);
            w.Write(m.ParamCount);
            w.Write(m.LocalCount);
            w.Write(m.MaxStack);
            w.Write(m.Slot);
            w.Write((uint)m.Code.Length);
            w.Write(m.Code);
            w.Write((uint)m.Eh.Count);
            foreach (var eh in m.Eh)
            {
                w.Write(eh.Kind);
                w.Write(eh.TryStart);
                w.Write(eh.TryEnd);
                w.Write(eh.HandlerStart);
                w.Write(eh.HandlerEnd);
                w.Write(eh.FilterStart);
            }
        }
        w.Write(_entryMethod ?? 0xFFFFFFFFu);
        // Debug sequence points: method idx -> (IL offset, source line).
        w.Write((uint)_debug.Count);
        foreach (var (mi, points) in _debug)
        {
            w.Write(mi);
            w.Write((uint)points.Count);
            foreach (var (il, line) in points)
            {
                w.Write(il);
                w.Write(line);
            }
        }

        // Embedded resources (RNX v4): every manifest resource embedded in
        // the compiled assemblies travels with the module. Managed apps read
        // them via RustNet.Resources.
        var resources = CollectResources();
        w.Write((uint)resources.Count);
        foreach (var (name, data) in resources)
        {
            byte[] nb = System.Text.Encoding.UTF8.GetBytes(name);
            w.Write((uint)nb.Length);
            w.Write(nb);
            w.Write((uint)data.Length);
            w.Write(data);
        }
        return ms.ToArray();
    }

    /// <summary>Read every embedded manifest resource from the loaded
    /// assemblies (the .NET managed-resource blob is length-prefixed
    /// entries in the CorHeader resources directory).</summary>
    private List<(string Name, byte[] Data)> CollectResources()
    {
        var list = new List<(string, byte[])>();
        var seen = new HashSet<string>();
        foreach (var asm in _assemblies)
        {
            var cor = asm.Pe.PEHeaders.CorHeader;
            if (cor is null || cor.ResourcesDirectory.Size == 0)
            {
                continue;
            }
            if (!asm.Pe.PEHeaders.TryGetDirectoryOffset(cor.ResourcesDirectory, out int baseOffset))
            {
                continue;
            }
            var image = asm.Pe.GetEntireImage().GetContent();
            foreach (var handle in asm.Md.ManifestResources)
            {
                var res = asm.Md.GetManifestResource(handle);
                if (!res.Implementation.IsNil)
                {
                    continue; // linked in another file — not embedded
                }
                string name = asm.Md.GetString(res.Name);
                if (!seen.Add(name))
                {
                    continue;
                }
                int at = baseOffset + (int)res.Offset;
                int len = image[at] | (image[at + 1] << 8) | (image[at + 2] << 16) | (image[at + 3] << 24);
                byte[] data = new byte[len];
                for (int i = 0; i < len; i++)
                {
                    data[i] = image[at + 4 + i];
                }
                list.Add((name, data));
            }
        }
        return list;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private uint InternString(string s)
    {
        if (_stringMap.TryGetValue(s, out uint idx))
        {
            return idx;
        }
        idx = (uint)_strings.Count;
        _strings.Add(s);
        _stringMap[s] = idx;
        return idx;
    }

    private static string Canonical(string typeName, string methodName, IEnumerable<string> paramCodes)
        => $"{typeName}::{methodName}({string.Join(",", paramCodes)})";

    private static string FullTypeName(MetadataReader md, TypeDefinition type)
    {
        string ns = md.GetString(type.Namespace);
        string name = md.GetString(type.Name);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private static string TypeRefOrDefName(MetadataReader md, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeReference:
            {
                var tref = md.GetTypeReference((TypeReferenceHandle)handle);
                string ns = md.GetString(tref.Namespace);
                string name = md.GetString(tref.Name);
                return ns.Length == 0 ? name : ns + "." + name;
            }
            case HandleKind.TypeDefinition:
                return FullTypeName(md, md.GetTypeDefinition((TypeDefinitionHandle)handle));
            case HandleKind.TypeSpecification:
                // Generic instantiation: decodes to the open generic name
                // (e.g. "System.Collections.Generic.List`1").
                return md.GetTypeSpecification((TypeSpecificationHandle)handle)
                    .DecodeSignature(SignatureProvider.Instance, null);
            default:
                return "?";
        }
    }

    private static void WriteU32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    public void Dispose()
    {
        foreach (var asm in _assemblies)
        {
            asm.Pe.Dispose();
        }
    }
}
