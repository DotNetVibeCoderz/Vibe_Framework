using System.Text;
using JSNet.Semantics;
using JSNet.Syntax;

namespace JSNet.CodeGen;

public sealed partial class CSharpEmitter
{
    /// <summary>Marker target: the expression is a call argument, so C# target-typing will apply.</summary>
    private const string ArgTarget = "\u0001arg";

    private const int PrecPrimary = 16, PrecUnary = 14, PrecConditional = 2, PrecAssign = 1;

    private static readonly HashSet<string> MathConstants = new(StringComparer.Ordinal) { "PI", "E", "LN2", "LN10", "LOG2E", "LOG10E", "SQRT2", "SQRT1_2" };

    private static int BinaryPrecedence(string op) => op switch
    {
        "*" or "/" or "%" => 13,
        "+" or "-" => 12,
        "<<" or ">>" or ">>>" => 11,
        "<" or ">" or "<=" or ">=" => 10,
        "==" or "!=" or "===" or "!==" => 9,
        "&" => 8,
        "^" => 7,
        "|" => 6,
        "&&" => 5,
        "||" => 4,
        "??" => 3,
        _ => 1,
    };

    public string Expr(Expression e, string? target = null) => E(e, target).Code;

    private string Paren(Expression e, int minPrec, string? target = null)
    {
        var (code, prec) = E(e, target);
        return prec < minPrec ? $"({code})" : code;
    }

    /// <summary>Emits an expression in boolean context, applying JavaScript truthiness when the type is not statically bool.</summary>
    private string Cond(Expression e)
    {
        switch (e)
        {
            case ParenthesizedExpression p: return Cond(p.Inner);
            case BinaryExpression { Operator: "&&" } a: return $"{CondP(a.Left, 5)} && {CondP(a.Right, 6)}";
            case BinaryExpression { Operator: "||" } o: return $"{CondP(o.Left, 4)} || {CondP(o.Right, 5)}";
            case UnaryExpression { Operator: "!" } n: return "!" + CondP(n.Operand, PrecUnary);
        }
        if (TypeOf(e) == "bool" || e is BinaryExpression { Operator: "==" or "!=" or "===" or "!==" or "<" or ">" or "<=" or ">=" } or IsTypeExpression)
            return Expr(e);
        return $"{Rt}JS.truthy({Expr(e)})";
    }

    private string CondP(Expression e, int minPrec)
    {
        // Cond() strips parentheses, so precedence must be judged on the unwrapped expression.
        while (e is ParenthesizedExpression p) e = p.Inner;
        string c = Cond(e);
        int prec = e switch
        {
            BinaryExpression b when b.Operator is "&&" or "||" => BinaryPrecedence(b.Operator),
            BinaryExpression b when TypeOf(e) == "bool" || b.Operator is "==" or "!=" or "===" or "!==" or "<" or ">" or "<=" or ">=" => BinaryPrecedence(b.Operator),
            UnaryExpression { Operator: "!" } => PrecUnary,
            AssignmentExpression or ConditionalExpression => PrecConditional,
            _ => PrecPrimary,
        };
        return prec < minPrec ? $"({c})" : c;
    }

    private (string Code, int Prec) E(Expression e, string? target)
    {
        switch (e)
        {
            case LiteralExpression l: return (Literal(l), PrecPrimary);
            case TemplateExpression t: return (Template(t), PrecPrimary);
            case IdentifierExpression id: return (Identifier(id.Name), PrecPrimary);
            case ThisExpression: return ("this", PrecPrimary);
            case SuperExpression: return ("base", PrecPrimary);
            case ParenthesizedExpression p:
            {
                var inner = E(p.Inner, target);
                return inner.Prec >= PrecPrimary ? inner : ($"({inner.Code})", PrecPrimary);
            }
            case ArrayExpression a: return ArrayLiteral(a, target);
            case ObjectExpression o: return (ObjectLiteral(o, target), PrecPrimary);
            case FunctionExpression f: return (Lambda(f, forceTypedParameters: false), PrecAssign);
            case UnaryExpression u: return Unary(u);
            case AwaitExpression aw: return ("await " + Paren(aw.Operand, PrecUnary), PrecUnary);
            case BinaryExpression b: return Binary(b);
            case AssignmentExpression a: return Assignment(a);
            case ConditionalExpression c:
            {
                string t = Paren(c.WhenTrue, PrecConditional + 1, target);
                string f = Paren(c.WhenFalse, PrecConditional, target);
                return ($"{CondP(c.Test, 4)} ? {t} : {f}", PrecConditional);
            }
            case CallExpression call: return (Call(call), PrecPrimary);
            case NewExpression n: return (New(n), PrecPrimary);
            case MemberExpression m: return (Member(m), PrecPrimary);
            case IndexExpression ix:
            {
                string idx = Expr(ix.Index);
                if (TypeOf(ix.Index) is "double" or "float" or "decimal") idx = $"(int)({idx})";
                return ($"{Paren(ix.Target, PrecPrimary)}{(ix.Optional ? "?[" : "[")}{idx}]", PrecPrimary);
            }
            case AsExpression a:
            {
                if (a.Type is NamedType { Name: "const" }) return E(a.Operand, target);
                return ($"(({MapType(a.Type)}){Paren(a.Operand, PrecUnary)})", PrecPrimary);
            }
            case IsTypeExpression it: return ($"{Paren(it.Operand, 11)} is {MapType(it.Type)}", 10);
            case NonNullExpression nn: return (Paren(nn.Operand, PrecPrimary) + "!", PrecPrimary);
            case SpreadExpression sp: return (".. " + Paren(sp.Operand, PrecUnary), PrecUnary);
            case TypeOfTypeExpression tt: return ($"typeof({MapType(tt.Type)})", PrecPrimary);
        }
        return ("default", PrecPrimary);
    }

    // ------------------------------------------------------------------------------ primaries

    private static string Literal(LiteralExpression l) => l.Kind switch
    {
        LiteralKind.Number => CSharpNames.NumberLiteral(l.Text),
        LiteralKind.String => CSharpNames.StringLiteral(l.Value ?? string.Empty),
        LiteralKind.True => "true",
        LiteralKind.False => "false",
        _ => "null",
    };

    private string Template(TemplateExpression t)
    {
        if (t.Holes.Count == 0) return CSharpNames.StringLiteral(CSharpNames.DecodeTemplateChunk(t.Quasis[0]));
        var sb = new StringBuilder("$\"");
        for (int i = 0; i < t.Quasis.Count; i++)
        {
            CSharpNames.AppendEscaped(sb, CSharpNames.DecodeTemplateChunk(t.Quasis[i]), interpolated: true);
            if (i < t.Holes.Count)
            {
                var hole = t.Holes[i];
                string code = Expr(hole);
                if (TypeOf(hole) is "bool" or "bool?" or null && hole is not (LiteralExpression { Kind: LiteralKind.String or LiteralKind.Number } or TemplateExpression)) code = $"{Rt}JS.str({code})";
                sb.Append("{(").Append(code).Append(")}");
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private string Identifier(string name)
    {
        if (!IsLocal(name))
        {
            switch (name)
            {
                case "NaN": return "double.NaN";
                case "Infinity": return "double.PositiveInfinity";
                case "undefined": return "null";
                case "globalThis": return $"{Rt}Globals";
            }
        }
        // Locals named like C# type keywords (`double`, `string`) must be escaped; otherwise keep `string.Join` usable.
        return IsLocal(name) ? CSharpNames.Id(name) : CSharpNames.ExprId(name);
    }

    private (string Code, int Prec) ArrayLiteral(ArrayExpression a, string? target)
    {
        string? elementTarget = target is null or ArgTarget ? null : ElementType(target);
        string Elems() => string.Join(", ", a.Elements.Select(x => Expr(x, elementTarget)));

        if (target is not null and not ArgTarget and not "dynamic" and not "object")
            return ($"[{Elems()}]", PrecPrimary);

        string? inferred = TypeOf(a);
        if (inferred is not null)
        {
            if (a.Elements.Any(x => x is SpreadExpression)) return ($"new {inferred}([{Elems()}])", PrecPrimary);
            return ($"new {inferred} {{ {Elems()} }}", PrecPrimary);
        }
        if (a.Elements.Count == 0) return ($"new {ListT}<dynamic>()", PrecPrimary);
        if (target == ArgTarget && a.Elements.All(x => x is ObjectExpression)) return ($"new[] {{ {Elems()} }}", PrecPrimary);
        if (target == ArgTarget && !a.Elements.Any(x => x is SpreadExpression)) return ($"[{Elems()}]", PrecPrimary);
        return ($"new {ListT}<dynamic>([{Elems()}])", PrecPrimary);
    }

    private static string? ElementType(string collection)
    {
        if (collection.EndsWith("[]", StringComparison.Ordinal)) return collection[..^2];
        int lt = collection.IndexOf('<');
        if (lt > 0 && collection.EndsWith('>'))
        {
            string inner = collection[(lt + 1)..^1];
            int depth = 0;
            foreach (char ch in inner)
            {
                if (ch == '<') depth++;
                else if (ch == '>') depth--;
                else if (ch == ',' && depth == 0) return null;
            }
            return inner;
        }
        return null;
    }

    private string ObjectLiteral(ObjectExpression o, string? target)
    {
        string Prop(ObjectProperty p, bool named) => p.Shorthand && !named
            ? CSharpNames.ExprId(p.Key)
            : $"{CSharpNames.Id(p.Key)} = {ObjectValue(p.Value)}";

        if (target is not null && target != ArgTarget)
        {
            if (target == "dynamic" || target == "object")
                return $"new {Rt}JsObject {{ {string.Join(", ", o.Properties.Select(p => $"[{CSharpNames.StringLiteral(p.Key)}] = {ObjectValue(p.Value)}"))} }}";
            if (target.Contains("Dictionary<", StringComparison.Ordinal))
            {
                string valueType = target[(target.IndexOf(',') + 1)..^1].Trim();
                return $"new {target} {{ {string.Join(", ", o.Properties.Select(p => $"[{CSharpNames.StringLiteral(p.Key)}] = {Expr(p.Value, valueType)}"))} }}";
            }
            string concrete = _model.Symbols.Interfaces.ContainsKey(target) ? target + "__Data" : target;
            if (!target.StartsWith("global::", StringComparison.Ordinal) || concrete != target)
                return $"new {concrete} {{ {string.Join(", ", o.Properties.Select(p => Prop(p, named: true)))} }}";
        }
        if (o.Properties.Count == 0) return $"new {Rt}JsObject()";
        return $"new {{ {string.Join(", ", o.Properties.Select(p => Prop(p, named: false)))} }}";
    }

    private string ObjectValue(Expression value) =>
        value is FunctionExpression fe && fe.Parameters.Any(p => p.Type is null) ? Lambda(fe, forceTypedParameters: true) : Expr(value);

    private string Lambda(FunctionExpression f, bool forceTypedParameters)
    {
        PushLocals();
        DeclareParameters(f.Parameters);
        bool anyTyped = f.Parameters.Any(p => p.Type is not null) || forceTypedParameters;
        var prologue = new List<string>();
        string ps;
        if (anyTyped) ps = "(" + Parameters(f.Parameters, prologue, allowDefaults: false) + ")";
        else
        {
            var names = f.Parameters.Select((p, i) => p.Pattern is not null ? $"__p{i}" : CSharpNames.Id(p.Name)).ToList();
            for (int i = 0; i < f.Parameters.Count; i++)
                if (f.Parameters[i].Pattern is { } pat) prologue.Add(DestructureCode(pat, names[i], isDeclaration: true));
            ps = "(" + string.Join(", ", names) + ")";
        }
        string prefix = f.IsAsync ? "async " : string.Empty;
        string? returnTarget = f.ReturnType is not null ? MapType(f.ReturnType) : null;
        if (returnTarget is not null && f.IsAsync) returnTarget = TypeMapper.UnwrapTask(returnTarget);

        string result;
        if (f.Body is Expression body && prologue.Count == 0)
        {
            _returnTypes.Push(returnTarget);
            string code = body is ObjectExpression ? $"({Expr(body, returnTarget)})" : Paren(body, PrecAssign, returnTarget);
            _returnTypes.Pop();
            result = $"{prefix}{ps} => {code}";
        }
        else
        {
            var block = f.Body as BlockStatement ?? new BlockStatement([new ReturnStatement((Expression)f.Body) { Span = f.Body.Span }]);
            var saved = _w;
            _w = new CodeWriter(saved.Indent);
            bool savedGen = _inGenerator;
            _inGenerator = false;
            _w.WriteLine("{");
            _w.Indent++;
            foreach (var p in prologue) _w.WriteLine(p);
            _returnTypes.Push(returnTarget);
            foreach (var s in block.Statements) EmitStatement(s);
            _returnTypes.Pop();
            _w.Indent--;
            _w.WriteLine("}");
            string text = _w.ToString().TrimEnd('\n');
            _w = saved;
            _inGenerator = savedGen;
            result = $"{prefix}{ps} =>\n{text}";
        }
        PopLocals();
        return result;
    }

    // ---------------------------------------------------------------------------- operators

    private (string, int) Unary(UnaryExpression u)
    {
        switch (u.Operator)
        {
            case "typeof":
            {
                // `typeof MyClass` on a project type yields the CLR Type (reflection); otherwise JS semantics.
                var typeOperand = u.Operand is ParenthesizedExpression pe ? pe.Inner : u.Operand;
                if (typeOperand is IdentifierExpression tid && !IsLocal(tid.Name) && IsProjectType(tid.Name)) return ($"typeof({CSharpNames.Id(tid.Name)})", PrecPrimary);
                return ($"{Rt}JS.typeOf({Expr(u.Operand)})", PrecPrimary);
            }
            case "void": return ($"{Rt}JS.discard({Expr(u.Operand)})", PrecPrimary);
            case "delete":
                return u.Operand switch
                {
                    IndexExpression ix => ($"{Rt}JS.delete({Expr(ix.Target)}, {Expr(ix.Index)})", PrecPrimary),
                    MemberExpression m => ($"{Rt}JS.delete({Expr(m.Target)}, {CSharpNames.StringLiteral(m.Name)})", PrecPrimary),
                    _ => ("true", PrecPrimary),
                };
            case "!": return ("!" + CondP(u.Operand, PrecUnary), PrecUnary);
            case "+":
                return TypeOf(u.Operand) is "int" or "long" or "double" or "float" or "decimal"
                    ? ("+" + Paren(u.Operand, PrecUnary), PrecUnary)
                    : ($"{Rt}JS.toNumber({Expr(u.Operand)})", PrecPrimary);
        }
        if (!u.Prefix) return (Paren(u.Operand, PrecPrimary) + u.Operator, PrecPrimary);
        string operand = Paren(u.Operand, PrecUnary);
        // Avoid "- -x" turning into "--x".
        if (operand.StartsWith(u.Operator[0])) operand = $"({operand})";
        return (u.Operator + operand, PrecUnary);
    }

    private (string, int) Binary(BinaryExpression b)
    {
        string op = b.Operator switch { "===" => "==", "!==" => "!=", _ => b.Operator };
        switch (op)
        {
            case "**": return ($"global::System.Math.Pow({Expr(b.Left)}, {Expr(b.Right)})", PrecPrimary);
            case "in": return ($"{Rt}JS.hasKey({Expr(b.Right)}, {Expr(b.Left)})", PrecPrimary);
            case "&&" or "||":
            {
                if (TypeOf(b.Left) == "bool" && TypeOf(b.Right) == "bool")
                    break;
                if (op == "&&") return ($"{Rt}JS.truthy({Expr(b.Left)}) ? {Paren(b.Right, PrecConditional + 1)} : default", PrecConditional);
                string tmp = Tmp("or");
                return ($"{Paren(b.Left, 10)} is var {tmp} && {Rt}JS.truthy({tmp}) ? {tmp} : {Paren(b.Right, PrecConditional)}", PrecConditional);
            }
        }
        int prec = BinaryPrecedence(op);
        bool rightAssoc = op == "??";
        string left = Paren(b.Left, rightAssoc ? prec + 1 : prec);
        string right = Paren(b.Right, rightAssoc ? prec : prec + 1);
        return ($"{left} {op} {right}", prec);
    }

    private (string, int) Assignment(AssignmentExpression a)
    {
        if (a.Target is ArrayExpression targets)
        {
            // [a, b] = [b, a] → tuple swap / deconstruction
            string lhs = "(" + string.Join(", ", targets.Elements.Select(x => Expr(x))) + ")";
            string rhs = a.Value is ArrayExpression values && values.Elements.Count == targets.Elements.Count
                ? "(" + string.Join(", ", values.Elements.Select(x => Expr(x))) + ")"
                : Expr(a.Value);
            return ($"{lhs} = {rhs}", PrecAssign);
        }
        string target = Paren(a.Target, PrecUnary);
        string? targetType = TypeOf(a.Target);
        switch (a.Operator)
        {
            case "**=": return ($"{target} = global::System.Math.Pow({target}, {Expr(a.Value)})", PrecAssign);
            case "||=": return ($"{target} = {Rt}JS.truthy({target}) ? {target} : {Paren(a.Value, PrecConditional)}", PrecAssign);
            case "&&=": return ($"{target} = {Rt}JS.truthy({target}) ? {Paren(a.Value, PrecConditional + 1)} : {target}", PrecAssign);
        }
        return ($"{target} {a.Operator} {Paren(a.Value, PrecAssign, a.Operator == "=" ? targetType : null)}", PrecAssign);
    }

    // ------------------------------------------------------------------------- calls & members

    private bool IsProjectType(string name) =>
        _model.Symbols.Classes.ContainsKey(name) || _model.Symbols.Interfaces.ContainsKey(name) || _model.Symbols.Enums.Contains(name);

    /// <summary>Parameter types of a project function/constructor/method being called, used to target-type arguments.</summary>
    private List<Parameter>? CalleeParameters(Expression callee, int argCount)
    {
        static List<Parameter>? Pick(IEnumerable<List<Parameter>> candidates, int n)
        {
            var list = candidates.ToList();
            return list.FirstOrDefault(ps => ps.Count == n && !ps.Any(p => p.Rest)) ?? list.FirstOrDefault(ps => ps.Count >= n && !ps.Any(p => p.Rest));
        }
        switch (callee)
        {
            case IdentifierExpression id when !IsLocal(id.Name) && _model.Symbols.Functions.TryGetValue(id.Name, out var f):
                return Pick([f.Parameters], argCount);
            case MemberExpression { Target: ThisExpression } m when _class is not null && _model.Symbols.Classes.TryGetValue(_class.Name, out var self)
                && self.Methods.TryGetValue(m.Name, out var own):
                return Pick(own.Select(x => x.Parameters), argCount);
            case MemberExpression { Target: IdentifierExpression cls } m when !IsLocal(cls.Name) && _model.Symbols.Classes.TryGetValue(cls.Name, out var info)
                && info.Methods.TryGetValue(m.Name, out var statics):
                return Pick(statics.Where(x => x.IsStatic).Select(x => x.Parameters), argCount);
        }
        return null;
    }

    private List<Parameter>? ConstructorParameters(TypeNode type, int argCount)
    {
        if (type is not NamedType nt || !_model.Symbols.Classes.TryGetValue(nt.Name, out var info)) return null;
        var ctor = info.Declaration.Members.FirstOrDefault(m => m.Kind == MemberKind.Constructor && m.Parameters.Count >= argCount);
        return ctor?.Parameters;
    }

    private string Arguments(List<Expression> args, List<Parameter>? parameters = null)
    {
        string Target(int i)
        {
            if (parameters is null || i >= parameters.Count || parameters[i].Type is null) return ArgTarget;
            return MapType(parameters[i].Type);
        }
        int spread = args.FindIndex(a => a is SpreadExpression);
        if (spread < 0) return string.Join(", ", args.Select((a, i) => Expr(a, Target(i))));
        var head = args.Take(spread).Select(a => Expr(a, ArgTarget));
        string tail = "[" + string.Join(", ", args.Skip(spread).Select(a => Expr(a))) + "]";
        return string.Join(", ", head.Append(tail));
    }

    private string Call(CallExpression call)
    {
        if (call.Callee is IdentifierExpression { Name: "__sequence" })
            return $"{Rt}JS.seq({string.Join(", ", call.Args.Select(a => Expr(a)))})";

        string typeArgs = call.TypeArgs.Count > 0 ? "<" + string.Join(", ", call.TypeArgs.Select(MapType)) + ">" : string.Empty;
        string callee;
        if (call.Callee is IdentifierExpression { Name: "String" or "Number" or "Boolean" } g && !IsLocal(g.Name))
            callee = g.Name switch { "String" => $"{Rt}JS.str", "Number" => $"{Rt}JS.toNumber", _ => $"{Rt}JS.truthy" };
        else callee = Paren(call.Callee, PrecPrimary);
        var parameters = CalleeParameters(call.Callee, call.Args.Count);
        if (call.Optional) return $"{callee}?.Invoke({Arguments(call.Args, parameters)})";
        return $"{callee}{typeArgs}({Arguments(call.Args, parameters)})";
    }

    private string New(NewExpression n)
    {
        string type = n.Type is NamedType { Name: "Promise" } p
            ? (p.TypeArgs.Count == 0 ? $"{Rt}Promise" : $"{Rt}Promise<{string.Join(", ", p.TypeArgs.Select(MapType))}>")
            : MapType(n.Type);
        if (n.IsArrayCreation) return $"new {MapType(n.Type)}[{Expr(n.Args[0])}]";
        string init = string.Empty;
        if (n.Initializer is not null)
            init = " { " + string.Join(", ", n.Initializer.Properties.Select(x => $"{CSharpNames.Id(x.Key)} = {ObjectValue(x.Value)}")) + " }";
        return $"new {type}({Arguments(n.Args, ConstructorParameters(n.Type, n.Args.Count))}){init}";
    }

    private string Member(MemberExpression m)
    {
        if (m.Target is IdentifierExpression id && !IsLocal(id.Name) && !_model.Symbols.Classes.ContainsKey(id.Name))
        {
            string name = m.Name;
            bool lower = name.Length > 0 && char.IsLower(name[0]);
            switch (id.Name)
            {
                case "Math":
                    return MathConstants.Contains(name) || lower ? $"{Rt}JSMath.{CSharpNames.Id(name)}" : $"global::System.Math.{name}";
                case "Object" when lower: return $"{Rt}JSObject.{CSharpNames.Id(name)}";
                case "Array" when lower: return $"{Rt}JSArray.{CSharpNames.Id(name)}";
                case "Number": return $"{Rt}JSNumber.{CSharpNames.Id(name)}";
                case "String" when lower: return $"{Rt}JSString.{CSharpNames.Id(name)}";
                case "String": return $"string.{name}";
                case "Object": return $"object.{name}";
            }
        }
        string target = Paren(m.Target, PrecPrimary);
        // Numeric literals need parentheses before member access: (5).toString()
        if (m.Target is LiteralExpression { Kind: LiteralKind.Number }) target = $"({target})";
        return $"{target}{(m.Optional ? "?." : ".")}{CSharpNames.Id(m.Name)}";
    }
}
