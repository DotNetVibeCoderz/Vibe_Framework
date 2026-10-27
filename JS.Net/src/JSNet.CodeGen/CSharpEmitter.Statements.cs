using JSNet.Semantics;
using JSNet.Syntax;

namespace JSNet.CodeGen;

public sealed partial class CSharpEmitter
{
    private void EmitStatement(Statement stmt)
    {
        switch (stmt)
        {
            case EmptyStatement or ImportDeclaration or NamespaceDeclaration or TypeAliasDeclaration:
                return;
            case BlockStatement b:
                EmitBlock(b);
                return;
            case ExpressionStatement e:
                EmitExpressionStatement(e);
                return;
            case VariableStatement v:
                EmitVariableStatement(v);
                return;
            case IfStatement i:
            {
                Line(i);
                int narrowed = BeginNarrowing(i.Test);
                _w.WriteLine($"if ({Cond(i.Test)})");
                EmitEmbedded(i.Then);
                EndNarrowing(narrowed);
                var @else = i.Else;
                while (@else is IfStatement elseIf)
                {
                    Line(elseIf);
                    int n2 = BeginNarrowing(elseIf.Test);
                    _w.WriteLine($"else if ({Cond(elseIf.Test)})");
                    EmitEmbedded(elseIf.Then);
                    EndNarrowing(n2);
                    @else = elseIf.Else;
                }
                if (@else is not null)
                {
                    _w.WriteLine("else");
                    EmitEmbedded(@else);
                }
                return;
            }
            case LabeledStatement ls:
                EmitLabeled(ls);
                return;
            case ForStatement f:
                EmitFor(f);
                return;
            case ForOfStatement fo:
                EmitForOf(fo);
                return;
            case WhileStatement w:
            {
                Line(w);
                string? continueLabel = TakeContinueLabel();
                _w.WriteLine($"while ({Cond(w.Test)})");
                EmitEmbedded(w.Body, null, continueLabel);
                return;
            }
            case DoWhileStatement d:
            {
                Line(d);
                string? continueLabel = TakeContinueLabel();
                _w.WriteLine("do");
                EmitEmbedded(d.Body, null, continueLabel);
                _w.WriteLine($"while ({Cond(d.Test)});");
                return;
            }
            case ReturnStatement r:
                Line(r);
                if (_inGenerator) _w.WriteLine("yield break;");
                else if (r.Value is null) _w.WriteLine("return;");
                else _w.WriteLine($"return {Expr(r.Value, _returnTypes.Count > 0 ? _returnTypes.Peek() : null)};");
                return;
            case YieldStatement y:
                Line(y);
                _w.WriteLine(y.IsBreak || y.Value is null ? "yield break;" : $"yield return {Expr(y.Value)};");
                return;
            case BreakStatement br:
                Line(stmt);
                _w.WriteLine(br.Label is null ? "break;" : $"goto {LabelName(br.Label, "break")};");
                return;
            case ContinueStatement co:
                Line(stmt);
                _w.WriteLine(co.Label is null ? "continue;" : $"goto {LabelName(co.Label, "continue")};");
                return;
            case ThrowStatement t:
                Line(t);
                _w.WriteLine($"throw {ThrowValue(t.Value)};");
                return;
            case TryStatement t:
                EmitTry(t);
                return;
            case SwitchStatement s:
                EmitSwitch(s);
                return;
            case UsingStatement u:
                EmitUsing(u);
                return;
            case FunctionDeclaration f:
                DeclareLocal(f.Name);
                EmitFunction(f, isStatic: false, isLocal: true);
                return;
            case ClassDeclaration or InterfaceDeclaration or EnumDeclaration or PromptDeclaration:
                return; // hoisted to the namespace (see EmitHoistedTypes)
        }
    }

    private void EmitBlock(BlockStatement b)
    {
        _w.WriteLine("{");
        _w.Indent++;
        PushLocals();
        foreach (var s in b.Statements) EmitStatement(s);
        PopLocals();
        _w.Indent--;
        _w.WriteLine("}");
    }

    /// <summary>Bodies of if/loops are always braced so lowering can safely add statements.</summary>
    private void EmitEmbedded(Statement body, IEnumerable<string>? prologue = null, string? continueLabel = null)
    {
        _w.WriteLine("{");
        _w.Indent++;
        PushLocals();
        if (prologue is not null) foreach (var p in prologue) _w.WriteLine(p);
        if (body is BlockStatement b) foreach (var s in b.Statements) EmitStatement(s);
        else EmitStatement(body);
        if (continueLabel is not null) _w.WriteLine($"{continueLabel}: ;");
        PopLocals();
        _w.Indent--;
        _w.WriteLine("}");
    }

    private void EmitExpressionStatement(ExpressionStatement e)
    {
        var expr = e.Expression;
        if (expr is LiteralExpression { Kind: LiteralKind.String }) return; // "use strict" and other directives
        Line(e);
        switch (expr)
        {
            // Short-circuit statement idioms: `ready && start();`, `cached || load();`, `ok ? a() : b();`
            case BinaryExpression { Operator: "&&" } and:
                _w.WriteLine($"if ({Cond(and.Left)}) {{ {StatementExpr(and.Right)} }}");
                return;
            case BinaryExpression { Operator: "||" } or:
                _w.WriteLine($"if (!({Cond(or.Left)})) {{ {StatementExpr(or.Right)} }}");
                return;
            case ConditionalExpression c:
                _w.WriteLine($"if ({Cond(c.Test)}) {{ {StatementExpr(c.WhenTrue)} }} else {{ {StatementExpr(c.WhenFalse)} }}");
                return;
            case CallExpression { Callee: IdentifierExpression { Name: "__sequence" } } seq:
                foreach (var part in seq.Args) _w.WriteLine(StatementExpr(part));
                return;
        }
        _w.WriteLine(StatementExpr(expr));
    }

    private string StatementExpr(Expression expr)
    {
        while (expr is ParenthesizedExpression p) expr = p.Inner;
        string code = Expr(expr);
        bool valid = expr is AssignmentExpression or CallExpression or AwaitExpression or NewExpression
            || expr is UnaryExpression { Operator: "++" or "--" };
        if (expr is AssignmentExpression { Operator: "**=" or "||=" or "&&=" }) valid = true;
        return valid ? code + ";" : $"_ = {code};";
    }

    private void EmitVariableStatement(VariableStatement v)
    {
        foreach (var d in v.Declarators)
        {
            Line(d);
            _w.WriteLine(DeclaratorCode(v.Kind, d));
        }
    }

    private string DeclaratorCode(VariableKind kind, VariableDeclarator d)
    {
        if (d.Init is ClassExpression cx && d.Target is IdentifierPattern cip && cx.Declaration.Name == cip.Name)
            return $"// class {cip.Name} is hoisted to the namespace";
        string? annotated = d.Type is not null ? MapType(d.Type) : kind == VariableKind.Dynamic ? "dynamic" : null;
        if (d.Target is IdentifierPattern ip)
        {
            string name = CSharpNames.Id(ip.Name);
            string? init = d.Init is null ? null : InitializerCode(d.Init, annotated, d);
            DeclareLocal(ip.Name);
            if (annotated is not null) return $"{annotated} {name} = {init ?? "default!"};";
            if (_model.IsPromotedToNumber(d)) return $"double {name} = {init};";
            if (init is null) return $"dynamic {name} = null;";
            if (d.Init is LiteralExpression { Kind: LiteralKind.Null or LiteralKind.Undefined }) return $"dynamic {name} = null;";
            return $"var {name} = {init};";
        }
        // Destructuring
        string source = d.Init is null ? "default" : Expr(d.Init, annotated);
        foreach (var n in PatternNames(d.Target)) DeclareLocal(n);
        if (d.Target is ArrayPattern ap && ap.Elements.All(e => e is IdentifierPattern) && ap.Elements.Count >= 2)
            return $"var ({string.Join(", ", ap.Elements.Cast<IdentifierPattern>().Select(e => CSharpNames.Id(e.Name)))}) = {source};";
        string tmp = Tmp("d");
        return $"var {tmp} = {source};\n{new string(' ', _w.IndentColumns)}{DestructureCode(d.Target, tmp, isDeclaration: true)}";
    }

    /// <summary>Initializer for an unannotated local: lambdas get explicit dynamic parameters so <c>var</c> can infer a natural type.</summary>
    private string InitializerCode(Expression init, string? annotated, VariableDeclarator d)
    {
        if (annotated is null && init is FunctionExpression fe && fe.Parameters.Any(p => p.Type is null))
            return Lambda(fe, forceTypedParameters: true);
        return Expr(init, annotated);
    }

    private string DestructureCode(Pattern pattern, string source, bool isDeclaration)
    {
        var lines = new List<string>();
        void Walk(Pattern p, string src)
        {
            switch (p)
            {
                case IdentifierPattern ip:
                    DeclareLocal(ip.Name);
                    lines.Add(isDeclaration ? $"var {CSharpNames.Id(ip.Name)} = {src};" : $"{CSharpNames.Id(ip.Name)} = {src};");
                    break;
                case ObjectPattern op:
                    foreach (var (key, value, def) in op.Properties)
                    {
                        string access = $"{src}.{CSharpNames.Id(key)}";
                        if (def is not null) access = $"({access} ?? {Expr(def)})";
                        Walk(value, access);
                    }
                    break;
                case ArrayPattern ap:
                    for (int i = 0; i < ap.Elements.Count; i++)
                        if (ap.Elements[i] is { } e) Walk(e, $"{src}[{i}]");
                    break;
            }
        }
        Walk(pattern, source);
        return string.Join("\n" + new string(' ', _w.IndentColumns), lines);
    }

    // ------------------------------------------------------------------ labels

    private string? _pendingContinueLabel;

    private static string LabelName(string label, string kind) => $"__{label}_{kind}";

    private string? TakeContinueLabel()
    {
        var l = _pendingContinueLabel;
        _pendingContinueLabel = null;
        return l;
    }

    /// <summary><c>outer: for (...) { ... continue outer; ... break outer; }</c> → C# goto labels.</summary>
    private void EmitLabeled(LabeledStatement ls)
    {
        bool isLoop = ls.Body is ForStatement or ForOfStatement or WhileStatement or DoWhileStatement;
        if (isLoop) _pendingContinueLabel = LabelName(ls.Label, "continue");
        _w.WriteLine("{");
        _w.Indent++;
        EmitStatement(ls.Body);
        _w.WriteLine($"{LabelName(ls.Label, "break")}: ;");
        _w.Indent--;
        _w.WriteLine("}");
    }

    private void EmitFor(ForStatement f)
    {
        Line(f);
        string? continueLabel = TakeContinueLabel();
        _w.WriteLine("{");
        _w.Indent++;
        PushLocals();
        string init = string.Empty;
        if (f.Init is VariableStatement vs)
        {
            if (vs.Declarators.Count == 1 && vs.Declarators[0].Target is IdentifierPattern) init = DeclaratorCode(vs.Kind, vs.Declarators[0]).TrimEnd(';');
            else foreach (var d in vs.Declarators) _w.WriteLine(DeclaratorCode(vs.Kind, d));
        }
        else if (f.Init is Expression ie) init = SequenceCode(ie);
        string test = f.Test is null ? string.Empty : Cond(f.Test);
        string update = f.Update is null ? string.Empty : SequenceCode(f.Update);
        _w.WriteLine($"for ({init}; {test}; {update})");
        EmitEmbedded(f.Body, null, continueLabel);
        PopLocals();
        _w.Indent--;
        _w.WriteLine("}");
    }

    private string SequenceCode(Expression e) =>
        e is CallExpression { Callee: IdentifierExpression { Name: "__sequence" } } seq
            ? string.Join(", ", seq.Args.Select(a => Expr(a)))
            : Expr(e);

    private void EmitForOf(ForOfStatement fo)
    {
        Line(fo);
        string? continueLabel = TakeContinueLabel();
        string iterable = fo.IsIn ? $"{Rt}JS.keys({Expr(fo.Iterable)})" : Expr(fo.Iterable);
        string keyword = fo.IsAwait ? "await foreach" : "foreach";
        PushLocals();
        foreach (var n in PatternNames(fo.Target)) DeclareLocal(n);
        switch (fo.Target)
        {
            case IdentifierPattern ip:
                string type = fo.Type is not null ? MapType(fo.Type) : "var";
                _w.WriteLine($"{keyword} ({type} {CSharpNames.Id(ip.Name)} in {iterable})");
                EmitEmbedded(fo.Body, null, continueLabel);
                break;
            case ArrayPattern ap when ap.Elements.Count >= 2 && ap.Elements.All(e => e is IdentifierPattern):
                _w.WriteLine($"{keyword} (var ({string.Join(", ", ap.Elements.Cast<IdentifierPattern>().Select(e => CSharpNames.Id(e.Name)))}) in {iterable})");
                EmitEmbedded(fo.Body, null, continueLabel);
                break;
            default:
                string tmp = Tmp("it");
                _w.WriteLine($"{keyword} (var {tmp} in {iterable})");
                _w.Indent++;
                string prologue = DestructureCode(fo.Target, tmp, isDeclaration: true).Replace("\n" + new string(' ', _w.IndentColumns), "\n" + new string(' ', _w.IndentColumns + 4), StringComparison.Ordinal);
                _w.Indent--;
                EmitEmbedded(fo.Body, [prologue], continueLabel);
                break;
        }
        PopLocals();
    }

    private void EmitTry(TryStatement t)
    {
        Line(t);
        _w.WriteLine("try");
        EmitEmbedded(t.Block);
        foreach (var c in t.Catches)
        {
            Line(c);
            if (c.Name is null) _w.WriteLine("catch");
            else
            {
                string type = c.Type is null ? "global::System.Exception" : MapType(c.Type);
                _w.WriteLine($"catch ({type} {CSharpNames.Id(c.Name)})");
            }
            PushLocals();
            if (c.Name is not null) DeclareLocal(c.Name);
            EmitEmbedded(c.Body);
            PopLocals();
        }
        if (t.Finally is not null)
        {
            _w.WriteLine("finally");
            EmitEmbedded(t.Finally);
        }
    }

    private string ThrowValue(Expression value)
    {
        if (value is NewExpression) return Expr(value);
        string? type = TypeOf(value);
        if (type == "string" || value is LiteralExpression or TemplateExpression) return $"new {Rt}Error({Expr(value)})";
        return $"{Rt}JS.toException({Expr(value)})";
    }

    private static bool IsConstantCase(Expression e) => e switch
    {
        LiteralExpression => true,
        UnaryExpression { Operator: "-", Operand: LiteralExpression } => true,
        MemberExpression { Target: IdentifierExpression or MemberExpression } => true,
        ParenthesizedExpression p => IsConstantCase(p.Inner),
        _ => false,
    };

    private static bool Terminates(List<Statement> body)
    {
        if (body.Count == 0) return false;
        return body[^1] switch
        {
            BreakStatement or ReturnStatement or ThrowStatement or ContinueStatement => true,
            BlockStatement b => Terminates(b.Statements),
            IfStatement { Else: not null } i => Terminates([i.Then]) && Terminates([i.Else]),
            _ => false,
        };
    }

    private void EmitSwitch(SwitchStatement s)
    {
        Line(s);
        if (s.Cases.All(c => c.Test is null || IsConstantCase(c.Test)))
        {
            _w.WriteLine($"switch ({Expr(s.Discriminant)})");
            _w.WriteLine("{");
            _w.Indent++;
            // Group stacked labels (`case 1: case 2: ...`) into one section.
            var sections = new List<(List<Expression?> Labels, List<Statement> Body)>();
            var pending = new List<Expression?>();
            foreach (var c in s.Cases)
            {
                pending.Add(c.Test);
                if (c.Body.Count == 0) continue;
                sections.Add((pending, c.Body));
                pending = [];
            }
            if (pending.Count > 0) sections.Add((pending, []));
            for (int i = 0; i < sections.Count; i++)
            {
                var (labels, body) = sections[i];
                foreach (var l in labels) _w.WriteLine(l is null ? "default:" : $"case {Expr(l)}:");
                _w.WriteLine("{");
                _w.Indent++;
                PushLocals();
                foreach (var st in body) EmitStatement(st);
                PopLocals();
                if (!Terminates(body))
                {
                    if (i + 1 < sections.Count)
                    {
                        var next = sections[i + 1].Labels[0];
                        _w.WriteLine(next is null ? "goto default;" : $"goto case {Expr(next)};");
                    }
                    else _w.WriteLine("break;");
                }
                _w.Indent--;
                _w.WriteLine("}");
            }
            _w.Indent--;
            _w.WriteLine("}");
            return;
        }

        // Non-constant cases (e.g. `switch (true) { case x > 5: ... }`) lower to an if-chain inside do/while(false)
        // so `break` keeps its meaning.
        string tmp = Tmp("sw");
        _w.WriteLine("do");
        _w.WriteLine("{");
        _w.Indent++;
        _w.WriteLine($"var {tmp} = {Expr(s.Discriminant)};");
        bool first = true;
        SwitchCase? @default = null;
        foreach (var c in s.Cases)
        {
            if (c.Test is null) { @default = c; continue; }
            string test = s.Discriminant is LiteralExpression { Kind: LiteralKind.True } ? Cond(c.Test) : $"{tmp} == {Paren(c.Test, 10)}";
            _w.WriteLine($"{(first ? "if" : "else if")} ({test})");
            EmitEmbedded(new BlockStatement(c.Body));
            first = false;
        }
        if (@default is not null)
        {
            if (!first) _w.WriteLine("else");
            EmitEmbedded(new BlockStatement(@default.Body));
        }
        _w.Indent--;
        _w.WriteLine("} while (false);");
    }

    private void EmitUsing(UsingStatement u)
    {
        Line(u);
        var d = u.Declaration.Declarators[0];
        string decl = DeclaratorCode(VariableKind.Let, d).TrimEnd(';');
        if (u.Body is null) _w.WriteLine($"using {decl};");
        else
        {
            _w.WriteLine($"using ({decl})");
            EmitEmbedded(u.Body);
        }
    }
}
