using JSNet.Syntax;

namespace JSNet.Semantics;

/// <summary>
/// Binds a set of compilation units: builds scopes, validates JavaScript rules the C# compiler would not
/// report in JS terms (const reassignment, await outside async, ...), runs the built-in analyzers and
/// infers CLR types for declarations that have no annotation.
/// </summary>
public sealed class Binder
{
    private enum SymbolKind { Let, Const, Parameter, Function, Class }

    private sealed class Symbol(string name, SymbolKind kind, Node declaration, string? type)
    {
        public string Name { get; } = name;
        public SymbolKind Kind { get; } = kind;
        public Node Declaration { get; } = declaration;
        public string? Type { get; set; } = type;
        public bool Used { get; set; }
    }

    private sealed class Scope(Scope? parent)
    {
        public Scope? Parent { get; } = parent;
        public Dictionary<string, Symbol> Symbols { get; } = new(StringComparer.Ordinal);

        public Symbol? Lookup(string name)
        {
            for (var s = this; s is not null; s = s.Parent)
                if (s.Symbols.TryGetValue(name, out var sym)) return sym;
            return null;
        }
    }

    private sealed class FunctionContext
    {
        public bool IsAsync { get; init; }
        public bool AllowAwait { get; init; }
        public ClassDeclaration? Class { get; init; }
        public bool IsConstructor { get; init; }
        public bool IsStatic { get; init; }
        public int Loops { get; set; }
        public int Switches { get; set; }
        public List<Expression?> Returns { get; } = [];
        public List<Expression> Yields { get; } = [];
    }

    private readonly SemanticModel _model;
    private readonly DiagnosticBag _diagnostics;
    private readonly bool _isLibrary;
    private SourceText _source = null!;
    private Scope _scope = new(null);
    private FunctionContext _fn = new();
    private readonly Dictionary<Node, string?> _returnCache = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Node> _inProgress = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FunctionDeclaration, SourceText> _functionSources = new(ReferenceEqualityComparer.Instance);

    private Binder(SemanticModel model, bool isLibrary)
    {
        _model = model;
        _diagnostics = model.Diagnostics;
        _isLibrary = isLibrary;
    }

    public static SemanticModel Bind(IReadOnlyList<CompilationUnit> units, DiagnosticBag diagnostics, bool isLibrary)
    {
        var symbols = new ProjectSymbols();
        var model = new SemanticModel(symbols, diagnostics);
        var binder = new Binder(model, isLibrary);
        binder.CollectDeclarations(units);
        foreach (var unit in units) binder.BindUnit(unit);
        return model;
    }

    // ------------------------------------------------------------------------------- collection

    private void CollectDeclarations(IReadOnlyList<CompilationUnit> units)
    {
        var symbols = _model.Symbols;
        foreach (var unit in units)
        {
            bool hasStatements = false;
            foreach (var stmt in unit.Statements)
            {
                switch (stmt)
                {
                    case ClassDeclaration c:
                        var info = new ClassInfo(c);
                        foreach (var m in c.Members)
                        {
                            if (m.Kind == MemberKind.Field) info.Fields[m.Name] = m;
                            else if (m.Kind != MemberKind.Constructor)
                            {
                                if (!info.Methods.TryGetValue(m.Name, out var list)) info.Methods[m.Name] = list = [];
                                list.Add(m);
                            }
                            else
                            {
                                foreach (var p in m.Parameters.Where(p => p.PropertyModifiers != Modifiers.None))
                                    info.Fields[p.Name] = new ClassMember(MemberKind.Field, p.Name) { Type = p.Type, Span = p.Span };
                            }
                        }
                        symbols.Classes[c.Name] = info;
                        break;
                    case InterfaceDeclaration i: symbols.Interfaces[i.Name] = i; break;
                    case EnumDeclaration e: symbols.Enums.Add(e.Name); break;
                    case FunctionDeclaration f:
                        symbols.Functions.TryAdd(f.Name, f);
                        _functionSources[f] = unit.Source;
                        break;
                    case VariableStatement v:
                        foreach (var d in v.Declarators)
                            if (d.Target is IdentifierPattern ip) symbols.ModuleVariables.TryAdd(ip.Name, d);
                        if (v.Declarators.Any(d => d.Target is not IdentifierPattern)) hasStatements = true;
                        break;
                    case ImportDeclaration or NamespaceDeclaration or TypeAliasDeclaration or EmptyStatement or PromptDeclaration:
                        break;
                    default:
                        hasStatements = true;
                        break;
                }
            }
            if (hasStatements) _model.EntryUnits.Add(unit);

            // Class expressions and classes declared inside functions are hoisted to the namespace.
            foreach (var node in AstWalker.DescendantsAndSelf(unit))
            {
                var nested = node switch
                {
                    ClassExpression cx => cx.Declaration,
                    ClassDeclaration cd when !unit.Statements.Contains(cd) => cd,
                    _ => null,
                };
                if (nested is null || symbols.Classes.ContainsKey(nested.Name)) continue;
                var nestedInfo = new ClassInfo(nested);
                foreach (var m in nested.Members)
                {
                    if (m.Kind == MemberKind.Field) nestedInfo.Fields[m.Name] = m;
                    else if (m.Kind != MemberKind.Constructor)
                    {
                        if (!nestedInfo.Methods.TryGetValue(m.Name, out var list)) nestedInfo.Methods[m.Name] = list = [];
                        list.Add(m);
                    }
                }
                symbols.Classes[nested.Name] = nestedInfo;
            }
        }

        if (_model.EntryUnits.Count > 1 && !_isLibrary)
        {
            foreach (var u in _model.EntryUnits.Skip(1))
                _diagnostics.Error(DiagnosticCodes.MultipleEntryPoints,
                    "Only one file in a program may contain top-level statements (it becomes the entry point).", u.Source, new TextSpan(0, 0));
        }
        if (_isLibrary)
        {
            foreach (var u in _model.EntryUnits)
            {
                var first = u.Statements.First(s => s is not (ClassDeclaration or InterfaceDeclaration or EnumDeclaration or FunctionDeclaration
                    or VariableStatement or ImportDeclaration or NamespaceDeclaration or TypeAliasDeclaration or EmptyStatement or PromptDeclaration));
                _diagnostics.Error(DiagnosticCodes.StatementsInLibrary,
                    "Top-level executable statements are not allowed in a class library. Move them into a function.", u.Source, first.Span);
            }
        }
    }

    // ---------------------------------------------------------------------------------- binding

    // Names (locals) and `this.x` members that receive fractional values somewhere in the current unit/class.
    private HashSet<string> _fractionalNames = new(StringComparer.Ordinal);
    private HashSet<string> _fractionalMembers = new(StringComparer.Ordinal);

    private bool LooksFractional(Expression rhs)
    {
        if (InferType(rhs) is "double" or "float") return true;
        foreach (var n in AstWalker.DescendantsAndSelf(rhs))
        {
            switch (n)
            {
                case LiteralExpression { Kind: LiteralKind.Number } l when NumberType(l.Text) is "double" or "float": return true;
                case BinaryExpression { Operator: "/" or "**" }: return true;
                case MemberExpression { Target: IdentifierExpression { Name: "Math" } }: return true;
                case FunctionExpression: return false;
            }
        }
        return false;
    }

    private void CollectFractionalAssignments(Node root, HashSet<string> names, HashSet<string> members)
    {
        foreach (var n in AstWalker.DescendantsAndSelf(root))
        {
            if (n is not AssignmentExpression { Operator: "=" or "+=" or "-=" or "*=" or "/=" } a) continue;
            if (a.Operator != "/=" && !LooksFractional(a.Value)) continue;
            if (a.Target is IdentifierExpression id) names.Add(id.Name);
            else if (a.Target is MemberExpression { Target: ThisExpression } m) members.Add(m.Name);
        }
    }

    private bool ShouldPromote(Expression? init, string name, HashSet<string> set) =>
        init is LiteralExpression { Kind: LiteralKind.Number } or UnaryExpression { Operator: "-", Operand: LiteralExpression { Kind: LiteralKind.Number } }
        && InferType(init) is "int" or "long" && set.Contains(name);

    private void BindUnit(CompilationUnit unit)
    {
        _source = unit.Source;
        _fractionalNames = new HashSet<string>(StringComparer.Ordinal);
        _fractionalMembers = new HashSet<string>(StringComparer.Ordinal);
        CollectFractionalAssignments(unit, _fractionalNames, _fractionalMembers);
        _scope = new Scope(null);
        bool isEntry = _model.EntryUnits.Contains(unit) && !_isLibrary;
        _fn = new FunctionContext { IsAsync = true, AllowAwait = isEntry };

        foreach (var stmt in unit.Statements)
        {
            if (stmt is FunctionDeclaration f) Declare(f.Name, SymbolKind.Function, f, null);
            else if (stmt is ClassDeclaration c) Declare(c.Name, SymbolKind.Class, c, c.Name);
        }

        var unitScope = _scope;
        BindStatements(unit.Statements);
        if (isEntry) ReportUnused(unitScope);
    }

    private void BindStatements(List<Statement> statements)
    {
        bool unreachableReported = false;
        for (int i = 0; i < statements.Count; i++)
        {
            var stmt = statements[i];
            BindStatement(stmt);
            if (!unreachableReported && i < statements.Count - 1 && stmt is ReturnStatement or ThrowStatement or BreakStatement or ContinueStatement
                && statements[i + 1] is not (FunctionDeclaration or EmptyStatement))
            {
                _diagnostics.Warning(DiagnosticCodes.UnreachableCode, "Unreachable code detected.", _source, statements[i + 1].Span);
                unreachableReported = true;
            }
        }
    }

    private void BindStatement(Statement stmt)
    {
        foreach (var a in stmt.Attributes) foreach (var arg in a.Args) BindExpression(arg);
        switch (stmt)
        {
            case BlockStatement b:
                PushScope();
                HoistFunctions(b.Statements);
                BindStatements(b.Statements);
                PopScope();
                break;
            case ExpressionStatement e: BindExpression(e.Expression); break;
            case VariableStatement v: BindVariableStatement(v); break;
            case IfStatement i:
                BindExpression(i.Test);
                BindStatement(i.Then);
                if (i.Else is not null) BindStatement(i.Else);
                break;
            case ForStatement f:
                PushScope();
                if (f.Init is VariableStatement vs) BindVariableStatement(vs);
                else if (f.Init is Expression ie) BindExpression(ie);
                if (f.Test is not null) BindExpression(f.Test);
                if (f.Update is not null) BindExpression(f.Update);
                BindLoopBody(f.Body);
                // Loop counters are frequently only read by the update/test clauses.
                MarkAllUsed(_scope);
                PopScope();
                break;
            case ForOfStatement fo:
                BindExpression(fo.Iterable);
                PushScope();
                string? elementType = fo.Type is not null ? TypeMapper.Map(fo.Type) : fo.IsIn ? "string" : ElementTypeOf(InferType(fo.Iterable));
                DeclarePattern(fo.Target, fo.Kind == VariableKind.Const ? SymbolKind.Const : SymbolKind.Let, fo, elementType);
                BindLoopBody(fo.Body);
                MarkAllUsed(_scope);
                PopScope();
                break;
            case WhileStatement w:
                BindExpression(w.Test);
                BindLoopBody(w.Body);
                break;
            case DoWhileStatement d:
                BindLoopBody(d.Body);
                BindExpression(d.Test);
                break;
            case ReturnStatement r:
                if (r.Value is not null) BindExpression(r.Value);
                _fn.Returns.Add(r.Value);
                break;
            case YieldStatement y:
                if (y.Value is not null)
                {
                    BindExpression(y.Value);
                    _fn.Yields.Add(y.Value);
                }
                break;
            case LabeledStatement ls:
                BindStatement(ls.Body);
                break;
            case BreakStatement { Label: not null }:
            case ContinueStatement { Label: not null }:
                break;
            case BreakStatement:
                if (_fn.Loops == 0 && _fn.Switches == 0)
                    _diagnostics.Error(DiagnosticCodes.BreakOutsideLoop, "'break' is only valid inside a loop or switch.", _source, stmt.Span);
                break;
            case ContinueStatement:
                if (_fn.Loops == 0)
                    _diagnostics.Error(DiagnosticCodes.BreakOutsideLoop, "'continue' is only valid inside a loop.", _source, stmt.Span);
                break;
            case ThrowStatement t: BindExpression(t.Value); break;
            case TryStatement t:
                BindStatement(t.Block);
                foreach (var c in t.Catches)
                {
                    PushScope();
                    if (c.Name is not null)
                    {
                        var sym = Declare(c.Name, SymbolKind.Let, c, c.Type is null ? "global::System.Exception" : TypeMapper.Map(c.Type));
                        sym.Used = true;
                    }
                    BindStatement(c.Body);
                    PopScope();
                }
                if (t.Finally is not null) BindStatement(t.Finally);
                break;
            case SwitchStatement s:
                BindExpression(s.Discriminant);
                _fn.Switches++;
                PushScope();
                foreach (var c in s.Cases)
                {
                    if (c.Test is not null) BindExpression(c.Test);
                    BindStatements(c.Body);
                }
                PopScope();
                _fn.Switches--;
                break;
            case UsingStatement u:
                PushScope();
                BindVariableStatement(u.Declaration);
                MarkAllUsed(_scope);
                if (u.Body is not null) BindStatement(u.Body);
                PopScope();
                break;
            case FunctionDeclaration f: BindFunctionDeclaration(f); break;
            case ClassDeclaration c: BindClass(c); break;
            case PromptDeclaration p:
                foreach (var field in p.Fields) _model.Types[field] = TypeMapper.Map(field.Type, _diagnostics, _source);
                break;
            case EnumDeclaration en:
                foreach (var m in en.Members) if (m.Value is not null) BindExpression(m.Value);
                break;
        }
    }

    private void BindLoopBody(Statement body)
    {
        _fn.Loops++;
        BindStatement(body);
        _fn.Loops--;
    }

    private void HoistFunctions(List<Statement> statements)
    {
        foreach (var s in statements)
            if (s is FunctionDeclaration f) Declare(f.Name, SymbolKind.Function, f, null);
    }

    private void BindVariableStatement(VariableStatement v)
    {
        foreach (var d in v.Declarators)
        {
            if (d.Init is not null) BindExpression(d.Init);
            string? type = d.Type is not null ? TypeMapper.Map(d.Type, _diagnostics, _source)
                : v.Kind == VariableKind.Dynamic ? "dynamic"
                : d.Init is not null ? InferType(d.Init) : null;
            if (d.Type is null && v.Kind != VariableKind.Const && d.Target is IdentifierPattern pip && ShouldPromote(d.Init, pip.Name, _fractionalNames))
            {
                type = "double";
                _model.PromotedNumbers.Add(d);
            }
            if (type is not null && d.Type is null) _model.Types[d] = type;
            DeclarePattern(d.Target, v.Kind == VariableKind.Const ? SymbolKind.Const : SymbolKind.Let, d, type);
            // `const Foo = class { }` declares a type, not a variable that must be read.
            if (d.Init is ClassExpression && d.Target is IdentifierPattern cip && _scope.Lookup(cip.Name) is { } classSym) classSym.Used = true;
        }
    }

    private void DeclarePattern(Pattern pattern, SymbolKind kind, Node decl, string? type)
    {
        switch (pattern)
        {
            case IdentifierPattern ip:
                Declare(ip.Name, kind, decl, type, pattern.Span);
                break;
            case ObjectPattern op:
                foreach (var (_, value, def) in op.Properties)
                {
                    if (def is not null) BindExpression(def);
                    DeclarePattern(value, kind, decl, null);
                }
                break;
            case ArrayPattern ap:
                foreach (var e in ap.Elements)
                    if (e is not null) DeclarePattern(e, kind, decl, ElementTypeOf(type));
                break;
        }
    }

    private Symbol Declare(string name, SymbolKind kind, Node decl, string? type, TextSpan? span = null)
    {
        if (_scope.Symbols.TryGetValue(name, out var existing))
        {
            bool bothHoisted = existing.Kind is SymbolKind.Function or SymbolKind.Class && ReferenceEquals(existing.Declaration, decl);
            if (!bothHoisted && kind != SymbolKind.Function && existing.Kind != SymbolKind.Function)
                _diagnostics.Error(DiagnosticCodes.DuplicateDeclaration, $"'{name}' is already declared in this scope.", _source, span ?? decl.Span);
            if (bothHoisted || existing.Kind == SymbolKind.Function) return existing;
        }
        var sym = new Symbol(name, kind, decl, type);
        _scope.Symbols[name] = sym;
        return sym;
    }

    private void PushScope() => _scope = new Scope(_scope);

    private void PopScope()
    {
        ReportUnused(_scope);
        _scope = _scope.Parent ?? _scope;
    }

    private void ReportUnused(Scope scope)
    {
        foreach (var sym in scope.Symbols.Values)
        {
            if (sym.Used || sym.Kind is not (SymbolKind.Let or SymbolKind.Const) || sym.Name.StartsWith('_')) continue;
            if (sym.Declaration is VariableDeclarator d)
                _diagnostics.Warning(DiagnosticCodes.UnusedVariable, $"Variable '{sym.Name}' is declared but never used.", _source, d.Target.Span);
        }
    }

    private static void MarkAllUsed(Scope scope)
    {
        foreach (var s in scope.Symbols.Values) s.Used = true;
    }

    // -------------------------------------------------------------------------- functions & classes

    private void BindFunctionDeclaration(FunctionDeclaration f)
    {
        var saved = _fn;
        _fn = new FunctionContext { IsAsync = f.IsAsync, AllowAwait = f.IsAsync || f.IsAi, Class = saved.Class };
        PushScope();
        BindParameters(f.Parameters, warnUntyped: true);
        if (f.Body is not null)
        {
            HoistFunctions(f.Body.Statements);
            BindStatements(f.Body.Statements);
        }
        PopScope();
        var ctx = _fn;
        _fn = saved;

        if (f.ReturnType is null && !f.IsAi)
            _model.Types[f] = ComputeReturnType(f, ctx, f.IsAsync, f.IsGenerator);
    }

    private void BindParameters(List<Parameter> parameters, bool warnUntyped)
    {
        foreach (var p in parameters)
        {
            if (p.Default is not null) BindExpression(p.Default);
            string? type = p.Type is not null ? TypeMapper.Map(p.Type, _diagnostics, _source) : null;
            if (type is null && p.Default is not null) type = InferType(p.Default);
            if (p.Type is null && warnUntyped && p.Default is null)
                _diagnostics.Warning(DiagnosticCodes.DynamicFallback,
                    $"Parameter '{p.Name}' has no type annotation and is compiled as 'dynamic'. Add a type for full CLR performance.", _source, p.Span);
            if (p.Type is null && type is not null) _model.Types[p] = type;
            if (p.Pattern is not null) DeclarePattern(p.Pattern, SymbolKind.Parameter, p, null);
            else Declare(p.Name, SymbolKind.Parameter, p, p.Rest && type is not null && !type.EndsWith("[]", StringComparison.Ordinal) ? type : type).Used = true;
        }
    }

    private void BindClass(ClassDeclaration c)
    {
        var savedFn = _fn;
        PushScope();
        foreach (var m in c.Members)
        {
            foreach (var a in m.Attributes) foreach (var arg in a.Args) BindExpression(arg);
            if (m.Kind == MemberKind.Field)
            {
                _fn = new FunctionContext { Class = c, IsStatic = m.IsStatic };
                if (m.Init is not null) BindExpression(m.Init);
                if (m.Type is null)
                {
                    string? t = m.Init is not null ? InferType(m.Init) : null;
                    if (ShouldPromote(m.Init, m.Name, _fractionalMembers)) t = "double";
                    _model.Types[m] = t ?? "dynamic";
                    if (t is null)
                        _diagnostics.Warning(DiagnosticCodes.DynamicFallback,
                            $"Field '{m.Name}' has no type annotation or inferable initializer and is compiled as 'dynamic'.", _source, m.Span);
                }
                continue;
            }

            bool isCtor = m.Kind == MemberKind.Constructor;
            _fn = new FunctionContext { IsAsync = m.IsAsync, AllowAwait = m.IsAsync, Class = c, IsConstructor = isCtor, IsStatic = m.IsStatic };
            PushScope();
            BindParameters(m.Parameters, warnUntyped: m.Kind != MemberKind.Setter);
            if (m.Body is not null)
            {
                if (isCtor) ValidateSuperCall(c, m.Body);
                HoistFunctions(m.Body.Statements);
                BindStatements(m.Body.Statements);
            }
            PopScope();
            if (m.Type is null && m.Kind is MemberKind.Method or MemberKind.Getter && m.Body is not null)
                _model.Types[m] = ComputeReturnType(m, _fn, m.IsAsync, m.IsGenerator);
        }
        PopScope();
        _fn = savedFn;
    }

    private void ValidateSuperCall(ClassDeclaration c, BlockStatement body)
    {
        for (int i = 0; i < body.Statements.Count; i++)
        {
            if (body.Statements[i] is ExpressionStatement { Expression: CallExpression { Callee: SuperExpression } } s)
            {
                if (c.Extends is null)
                    _diagnostics.Error(DiagnosticCodes.SuperOutsideDerived, "'super(...)' is only valid in a class that extends another class.", _source, s.Span);
                else if (i != 0)
                    _diagnostics.Error(DiagnosticCodes.SuperCallPosition, "'super(...)' must be the first statement in the constructor.", _source, s.Span);
            }
        }
    }

    private string ComputeReturnType(Node fn, FunctionContext ctx, bool isAsync, bool isGenerator)
    {
        if (isGenerator)
        {
            string elem = UnifyTypes(ctx.Yields.Select(InferTypeOrNull)) ?? "dynamic";
            return isAsync ? $"global::System.Collections.Generic.IAsyncEnumerable<{elem}>" : $"global::System.Collections.Generic.IEnumerable<{elem}>";
        }
        var values = ctx.Returns.Where(r => r is not null).ToList();
        if (values.Count == 0) return isAsync ? "global::System.Threading.Tasks.Task" : "void";
        string? unified = UnifyTypes(values.Select(InferTypeOrNull));
        if (unified is null)
        {
            _diagnostics.Warning(DiagnosticCodes.DynamicFallback,
                "Return type could not be inferred and is compiled as 'dynamic'. Add a return type annotation for full CLR performance.", _source, fn.Span);
            unified = "dynamic";
        }
        return isAsync ? $"global::System.Threading.Tasks.Task<{unified}>" : unified;
    }

    /// <summary>Uses the type recorded while the expression's scope was live, falling back to fresh inference.</summary>
    private string? InferTypeOrNull(Expression? e) => e switch
    {
        null => null,
        FunctionExpression fn => InferType(fn) ?? "?", // Types[fn] holds the return type, not the delegate type
        _ => _model.GetInferredType(e) ?? InferType(e) ?? "?",
    };

    private static string? UnifyTypes(IEnumerable<string?> types)
    {
        string? result = null;
        bool sawNull = false;
        foreach (var t in types)
        {
            if (t is null) { sawNull = true; continue; }
            if (t == "?") return null;
            if (result is null) result = t;
            else if (result != t)
            {
                string? promoted = PromoteNumeric(result, t);
                if (promoted is null) return null;
                result = promoted;
            }
        }
        if (result is null) return sawNull ? null : null;
        return sawNull && IsValueType(result) ? result + "?" : result;
    }

    private static bool IsValueType(string t) => t is "int" or "long" or "double" or "float" or "decimal" or "bool" or "char" or "byte" or "short";

    // -------------------------------------------------------------------------------- expressions

    private void BindExpression(Expression expr)
    {
        BindExpressionCore(expr);
        // Recorded for the emitter: drives JS truthiness lowering and numeric index casts.
        if (expr is not FunctionExpression && InferType(expr) is { } type) _model.Types[expr] = type;
    }

    private void BindExpressionCore(Expression expr)
    {
        switch (expr)
        {
            case IdentifierExpression id:
                var sym = _scope.Lookup(id.Name);
                if (sym is not null) sym.Used = true;
                break;
            case AssignmentExpression a:
                CheckConstAssignment(a.Target, a.Span);
                if (a.Target is not IdentifierExpression) BindExpression(a.Target);
                else if (a.Operator != "=") BindExpression(a.Target);
                BindExpression(a.Value);
                break;
            case UnaryExpression u:
                if (u.Operator is "++" or "--") CheckConstAssignment(u.Operand, u.Span);
                BindExpression(u.Operand);
                break;
            case AwaitExpression aw:
                if (!_fn.AllowAwait)
                    _diagnostics.Error(DiagnosticCodes.AwaitOutsideAsync, "'await' is only valid in async functions and the program's top level.", _source, aw.Span);
                BindExpression(aw.Operand);
                break;
            case BinaryExpression b:
                BindExpression(b.Left);
                BindExpression(b.Right);
                if (b.Operator == "/" && InferType(b.Left) is "int" or "long" && InferType(b.Right) is "int" or "long")
                    _diagnostics.Warning(DiagnosticCodes.IntegerDivision,
                        "Both operands are integers, so '/' performs integer division (CLR semantics). Use 'number' or a decimal literal for fractional results.", _source, b.Span);
                break;
            case ConditionalExpression c:
                BindExpression(c.Test);
                BindExpression(c.WhenTrue);
                BindExpression(c.WhenFalse);
                break;
            case CallExpression call:
                BindExpression(call.Callee);
                foreach (var arg in call.Args) BindExpression(arg);
                break;
            case NewExpression n:
                foreach (var arg in n.Args) BindExpression(arg);
                if (n.Initializer is not null) foreach (var p in n.Initializer.Properties) BindExpression(p.Value);
                break;
            case MemberExpression m: BindExpression(m.Target); break;
            case IndexExpression ix:
                BindExpression(ix.Target);
                BindExpression(ix.Index);
                break;
            case ArrayExpression arr:
                foreach (var e in arr.Elements) BindExpression(e);
                break;
            case ObjectExpression obj:
                foreach (var p in obj.Properties) BindExpression(p.Value);
                break;
            case TemplateExpression t:
                foreach (var h in t.Holes) BindExpression(h);
                break;
            case SpreadExpression s: BindExpression(s.Operand); break;
            case ParenthesizedExpression p: BindExpression(p.Inner); break;
            case AsExpression a2: BindExpression(a2.Operand); break;
            case IsTypeExpression i2: BindExpression(i2.Operand); break;
            case NonNullExpression nn: BindExpression(nn.Operand); break;
            case SuperExpression:
                if (_fn.Class?.Extends is null)
                    _diagnostics.Error(DiagnosticCodes.SuperOutsideDerived, "'super' is only valid in a class that extends another class.", _source, expr.Span);
                break;
            case FunctionExpression f: BindFunctionExpression(f); break;
            case ClassExpression cx: BindClass(cx.Declaration); break;
            case RegexExpression: break;
            case RefArgumentExpression ra:
                if (ra.Target is not null)
                {
                    BindExpression(ra.Target);
                    if (ra.Target is IdentifierExpression rid && _scope.Lookup(rid.Name) is { } rsym) rsym.Used = true;
                }
                else if (ra.DeclaredName is not null)
                    Declare(ra.DeclaredName, SymbolKind.Let, ra, ra.DeclaredType is null ? null : TypeMapper.Map(ra.DeclaredType)).Used = true;
                break;
        }
    }

    private void BindFunctionExpression(FunctionExpression f)
    {
        var saved = _fn;
        _fn = new FunctionContext { IsAsync = f.IsAsync, AllowAwait = f.IsAsync, Class = saved.Class, IsStatic = saved.IsStatic, Loops = 0 };
        PushScope();
        BindParameters(f.Parameters, warnUntyped: false);
        if (f.Body is BlockStatement b)
        {
            HoistFunctions(b.Statements);
            BindStatements(b.Statements);
        }
        else if (f.Body is Expression e)
        {
            BindExpression(e);
            _fn.Returns.Add(e);
        }
        PopScope();
        if (f.ReturnType is null)
        {
            string? r = UnifyTypes(_fn.Returns.Where(x => x is not null).Select(InferTypeOrNull));
            if (r is not null) _model.Types[f] = f.IsAsync ? $"global::System.Threading.Tasks.Task<{r}>" : r;
        }
        _fn = saved;
    }

    private void CheckConstAssignment(Expression target, TextSpan span)
    {
        if (target is IdentifierExpression id && _scope.Lookup(id.Name) is { Kind: SymbolKind.Const })
            _diagnostics.Error(DiagnosticCodes.ConstReassignment, $"Cannot assign to '{id.Name}' because it is a constant.", _source, span);
    }

    // ------------------------------------------------------------------------------ type inference

    /// <summary>Best-effort static type of an expression as C# type text; null when unknown.</summary>
    public string? InferType(Expression expr)
    {
        switch (expr)
        {
            case LiteralExpression l:
                return l.Kind switch
                {
                    LiteralKind.Number => NumberType(l.Text),
                    LiteralKind.String => "string",
                    LiteralKind.True or LiteralKind.False => "bool",
                    _ => null,
                };
            case TemplateExpression: return "string";
            case ParenthesizedExpression p: return InferType(p.Inner);
            case IdentifierExpression id:
            {
                var sym = _scope.Lookup(id.Name);
                if (sym is not null) return sym.Kind == SymbolKind.Class ? null : sym.Type;
                if (_model.Symbols.ModuleVariables.TryGetValue(id.Name, out var d))
                    return d.Type is not null ? TypeMapper.Map(d.Type) : _model.GetInferredType(d);
                return null;
            }
            case ThisExpression: return _fn.Class?.Name;
            case NewExpression n: return n.IsArrayCreation ? TypeMapper.Map(n.Type) + "[]" : TypeMapper.Map(n.Type);
            case AsExpression a: return TypeMapper.Map(a.Type);
            case IsTypeExpression: return "bool";
            case NonNullExpression nn: return InferType(nn.Operand)?.TrimEnd('?');
            case AwaitExpression aw:
            {
                var t = InferType(aw.Operand);
                if (t is null) return null;
                return TypeMapper.UnwrapTask(t);
            }
            case UnaryExpression u:
                return u.Operator switch
                {
                    "!" or "delete" => "bool",
                    "typeof" => "string",
                    "-" or "+" or "~" or "++" or "--" => InferType(u.Operand),
                    _ => null,
                };
            case BinaryExpression b:
            {
                switch (b.Operator)
                {
                    case "==" or "!=" or "===" or "!==" or "<" or ">" or "<=" or ">=" or "&&" or "||" or "in":
                        return b.Operator is "&&" or "||" ? (InferType(b.Left) == "bool" ? "bool" : null) : "bool";
                    case "??": return InferType(b.Left)?.TrimEnd('?') ?? InferType(b.Right);
                    case "**": return "double";
                }
                var l = InferType(b.Left);
                var r = InferType(b.Right);
                if (b.Operator == "+" && (l == "string" || r == "string")) return "string";
                if (l is null || r is null) return null;
                return PromoteNumeric(l, r);
            }
            case AssignmentExpression asg: return InferType(asg.Value);
            case ConditionalExpression c:
            {
                var t = InferType(c.WhenTrue);
                var f = InferType(c.WhenFalse);
                if (t == f) return t;
                if (t is not null && c.WhenFalse is LiteralExpression { Kind: LiteralKind.Null }) return t;
                return t is not null && f is not null ? PromoteNumeric(t, f) : null;
            }
            case ArrayExpression arr:
            {
                if (arr.Elements.Count == 0) return null;
                var elem = UnifyTypes(arr.Elements.Select(e => e is SpreadExpression sp ? ElementTypeOf(InferType(sp.Operand)) ?? "?" : InferTypeOrNull(e)));
                return elem is null ? null : $"global::System.Collections.Generic.List<{elem}>";
            }
            case MemberExpression m:
                return m.Name switch
                {
                    "length" or "size" => "int",
                    _ => FieldType(m),
                };
            case CallExpression call: return InferCallType(call);
            case FunctionExpression fn when fn.Parameters.All(p => p.Type is not null && !p.Rest && p.Pattern is null):
            {
                var ps = fn.Parameters.Select(p => TypeMapper.Map(p.Type)).ToList();
                string? ret = fn.ReturnType is not null ? TypeMapper.Map(fn.ReturnType) : _model.GetInferredType(fn);
                if (ret is null && fn.Body is not BlockStatement) return null;
                if (ret is null || ret == "void") return ps.Count == 0 ? "global::System.Action" : $"global::System.Action<{string.Join(", ", ps)}>";
                return $"global::System.Func<{string.Join(", ", ps.Append(ret))}>";
            }
        }
        return null;
    }

    /// <summary>Last generic argument of <c>Func&lt;..., R&gt;</c>.</summary>
    private static string? FuncReturnType(string? delegateType)
    {
        const string prefix = "global::System.Func<";
        if (delegateType is null || !delegateType.StartsWith(prefix, StringComparison.Ordinal)) return null;
        string inner = delegateType[prefix.Length..^1];
        int depth = 0, lastComma = -1;
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] is '<' or '(') depth++;
            else if (inner[i] is '>' or ')') depth--;
            else if (inner[i] == ',' && depth == 0) lastComma = i;
        }
        return inner[(lastComma + 1)..].Trim();
    }

    private string? FieldType(MemberExpression m)
    {
        string? owner = m.Target is ThisExpression ? _fn.Class?.Name : InferType(m.Target);
        if (owner is null || !_model.Symbols.Classes.TryGetValue(owner, out var info)) return null;
        if (info.Fields.TryGetValue(m.Name, out var field))
            return field.Type is not null ? TypeMapper.Map(field.Type) : _model.GetInferredType(field);
        if (info.Methods.TryGetValue(m.Name, out var acc) && acc.FirstOrDefault(a => a.Kind == MemberKind.Getter) is { } getter)
            return getter.Type is not null ? TypeMapper.Map(getter.Type) : null;
        return null;
    }

    private static readonly HashSet<string> StringMethods = new(StringComparer.Ordinal)
    {
        "toString", "toUpperCase", "toLowerCase", "trim", "trimStart", "trimEnd", "join", "substring", "padStart",
        "padEnd", "replace", "replaceAll", "toFixed", "charAt", "repeat", "stringify", "ToString", "inspect", "toLocaleString",
    };

    private static readonly HashSet<string> BoolMethods = new(StringComparer.Ordinal)
    {
        "includes", "startsWith", "endsWith", "some", "every", "has", "isArray", "isNaN", "isFinite", "isInteger", "delete", "any", "all",
    };

    private static readonly HashSet<string> IntMethods = new(StringComparer.Ordinal) { "indexOf", "lastIndexOf", "push", "findIndex", "count", "localeCompare" };

    private string? InferCallType(CallExpression call)
    {
        switch (call.Callee)
        {
            case IdentifierExpression id:
            {
                if (_scope.Lookup(id.Name) is { Kind: not SymbolKind.Function } local) return FuncReturnType(local.Type);
                if (_model.Symbols.Functions.TryGetValue(id.Name, out var f)) return ResolveFunctionReturn(f);
                return id.Name switch
                {
                    "parseInt" => "int",
                    "parseFloat" or "Number" => "double",
                    "String" => "string",
                    "Boolean" or "isNaN" => "bool",
                    _ => null,
                };
            }
            case MemberExpression m:
            {
                if (m.Target is IdentifierExpression { Name: "Math" }) return m.Name is "round" or "floor" or "ceil" or "trunc" ? "double" : "double";
                if (m.Target is IdentifierExpression { Name: "JSON" } && m.Name == "stringify") return "string";
                if (StringMethods.Contains(m.Name)) return "string";
                if (BoolMethods.Contains(m.Name)) return "bool";
                if (IntMethods.Contains(m.Name)) return "int";
                if (m.Name is "split") return "string[]";
                string? owner = m.Target is ThisExpression ? _fn.Class?.Name
                    : m.Target is IdentifierExpression cid && _model.Symbols.Classes.ContainsKey(cid.Name) && _scope.Lookup(cid.Name) is null or { Kind: SymbolKind.Class } ? cid.Name
                    : InferType(m.Target);
                if (owner is not null && _model.Symbols.Classes.TryGetValue(owner, out var info) && info.Methods.TryGetValue(m.Name, out var methods))
                {
                    var method = methods.FirstOrDefault(x => x.Kind == MemberKind.Method && x.Parameters.Count == call.Args.Count) ?? methods[0];
                    if (method.Type is not null)
                    {
                        string t = TypeMapper.Map(method.Type);
                        return method.IsAsync && !t.Contains("Task", StringComparison.Ordinal) ? $"global::System.Threading.Tasks.Task<{t}>" : t;
                    }
                    return _model.GetInferredType(method);
                }
                return null;
            }
        }
        return null;
    }

    private string? ResolveFunctionReturn(FunctionDeclaration f)
    {
        if (f.ReturnType is not null)
        {
            string t = TypeMapper.Map(f.ReturnType);
            if (f.IsAsync && !t.Contains("Task", StringComparison.Ordinal)) t = t == "void" ? "global::System.Threading.Tasks.Task" : $"global::System.Threading.Tasks.Task<{t}>";
            return t;
        }
        if (_model.GetInferredType(f) is { } inferred) return inferred;
        if (_returnCache.TryGetValue(f, out var cached)) return cached;
        if (!_inProgress.Add(f)) return null;

        // Infer on demand for functions declared later in the project (with a throwaway diagnostics bag).
        var saveScope = _scope;
        var saveFn = _fn;
        var saveSource = _source;
        var tempBinder = new Binder(new SemanticModel(_model.Symbols, new DiagnosticBag()), _isLibrary);
        foreach (var kv in _model.Types) tempBinder._model.Types[kv.Key] = kv.Value;
        tempBinder._source = _functionSources.TryGetValue(f, out var src) ? src : _source;
        foreach (var kv in _functionSources) tempBinder._functionSources[kv.Key] = kv.Value;
        foreach (var n in _inProgress) tempBinder._inProgress.Add(n);
        tempBinder.BindFunctionDeclaration(f);
        string? result = tempBinder._model.GetInferredType(f);
        _scope = saveScope;
        _fn = saveFn;
        _source = saveSource;
        _inProgress.Remove(f);
        _returnCache[f] = result;
        return result;
    }

    private static string NumberType(string text)
    {
        string t = text.Replace("_", "", StringComparison.Ordinal);
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || t.StartsWith("0b", StringComparison.OrdinalIgnoreCase) || t.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
            return t.EndsWith('L') || t.EndsWith('l') ? "long" : "int";
        char last = t[^1];
        switch (last)
        {
            case 'm' or 'M': return "decimal";
            case 'f' or 'F': return "float";
            case 'd' or 'D': return "double";
            case 'L' or 'l': return "long";
        }
        if (t.Contains('.') || t.Contains('e') || t.Contains('E')) return "double";
        return long.TryParse(t, out var v) && (v > int.MaxValue || v < int.MinValue) ? "long" : "int";
    }

    private static string? PromoteNumeric(string a, string b)
    {
        static int Rank(string t) => t switch
        {
            "byte" or "short" or "char" => 1,
            "int" => 2,
            "long" => 3,
            "float" => 4,
            "double" => 5,
            "decimal" => 6,
            _ => -1,
        };
        if (a == b) return a;
        int ra = Rank(a), rb = Rank(b);
        if (ra < 0 || rb < 0) return null;
        if ((a == "decimal" && b is "double" or "float") || (b == "decimal" && a is "double" or "float")) return null;
        return ra >= rb ? (ra == 1 ? "int" : a) : (rb == 1 ? "int" : b);
    }

    private static string? ElementTypeOf(string? collection)
    {
        if (collection is null) return null;
        if (collection.EndsWith("[]", StringComparison.Ordinal)) return collection[..^2];
        if (collection == "string") return "char";
        int lt = collection.IndexOf('<');
        if (lt > 0 && collection.EndsWith('>') && !collection[(lt + 1)..^1].Contains(',')) return collection[(lt + 1)..^1];
        return null;
    }
}
