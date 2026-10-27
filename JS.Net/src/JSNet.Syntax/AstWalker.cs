namespace JSNet.Syntax;

/// <summary>Enumerates the children of AST nodes; used by analyzers, the emitter and the language server.</summary>
public static class AstWalker
{
    public static IEnumerable<Node> Children(Node node)
    {
        switch (node)
        {
            case CompilationUnit u: foreach (var s in u.Statements) yield return s; break;
            case BlockStatement b: foreach (var s in b.Statements) yield return s; break;
            case ExpressionStatement e: yield return e.Expression; break;
            case VariableStatement v: foreach (var d in v.Declarators) yield return d; break;
            case VariableDeclarator d:
                yield return d.Target;
                if (d.Init is not null) yield return d.Init;
                break;
            case IfStatement i:
                yield return i.Test;
                yield return i.Then;
                if (i.Else is not null) yield return i.Else;
                break;
            case ForStatement f:
                if (f.Init is not null) yield return f.Init;
                if (f.Test is not null) yield return f.Test;
                if (f.Update is not null) yield return f.Update;
                yield return f.Body;
                break;
            case ForOfStatement fo:
                yield return fo.Target;
                yield return fo.Iterable;
                yield return fo.Body;
                break;
            case WhileStatement w: yield return w.Test; yield return w.Body; break;
            case DoWhileStatement dw: yield return dw.Body; yield return dw.Test; break;
            case ReturnStatement r: if (r.Value is not null) yield return r.Value; break;
            case YieldStatement y: if (y.Value is not null) yield return y.Value; break;
            case ThrowStatement t: yield return t.Value; break;
            case TryStatement t:
                yield return t.Block;
                foreach (var c in t.Catches) yield return c.Body;
                if (t.Finally is not null) yield return t.Finally;
                break;
            case SwitchStatement s:
                yield return s.Discriminant;
                foreach (var c in s.Cases)
                {
                    if (c.Test is not null) yield return c.Test;
                    foreach (var st in c.Body) yield return st;
                }
                break;
            case UsingStatement us:
                yield return us.Declaration;
                if (us.Body is not null) yield return us.Body;
                break;
            case FunctionDeclaration fd:
                foreach (var p in fd.Parameters) yield return p;
                if (fd.Body is not null) yield return fd.Body;
                break;
            case ClassDeclaration c:
                foreach (var m in c.Members) yield return m;
                break;
            case InterfaceDeclaration id:
                foreach (var m in id.Members) yield return m;
                break;
            case ClassMember m:
                foreach (var p in m.Parameters) yield return p;
                if (m.Init is not null) yield return m.Init;
                if (m.Body is not null) yield return m.Body;
                break;
            case Parameter p:
                if (p.Default is not null) yield return p.Default;
                break;
            case EnumDeclaration en:
                foreach (var m in en.Members) if (m.Value is not null) yield return m.Value;
                break;
            case PromptDeclaration pr:
                if (pr.Template is not null) yield return pr.Template;
                break;
            case TemplateExpression te: foreach (var h in te.Holes) yield return h; break;
            case ArrayExpression a: foreach (var e in a.Elements) yield return e; break;
            case ObjectExpression o: foreach (var p in o.Properties) yield return p.Value; break;
            case FunctionExpression f:
                foreach (var p in f.Parameters) yield return p;
                yield return f.Body;
                break;
            case UnaryExpression u: yield return u.Operand; break;
            case AwaitExpression aw: yield return aw.Operand; break;
            case BinaryExpression bi: yield return bi.Left; yield return bi.Right; break;
            case AssignmentExpression asg: yield return asg.Target; yield return asg.Value; break;
            case ConditionalExpression ce: yield return ce.Test; yield return ce.WhenTrue; yield return ce.WhenFalse; break;
            case CallExpression call:
                yield return call.Callee;
                foreach (var a in call.Args) yield return a;
                break;
            case NewExpression n:
                foreach (var a in n.Args) yield return a;
                if (n.Initializer is not null) yield return n.Initializer;
                break;
            case MemberExpression me: yield return me.Target; break;
            case IndexExpression ie: yield return ie.Target; yield return ie.Index; break;
            case AsExpression ae: yield return ae.Operand; break;
            case IsTypeExpression ite: yield return ite.Operand; break;
            case NonNullExpression nn: yield return nn.Operand; break;
            case SpreadExpression sp: yield return sp.Operand; break;
            case ParenthesizedExpression pe: yield return pe.Inner; break;
            case ClassExpression cx: yield return cx.Declaration; break;
            case RefArgumentExpression ra: if (ra.Target is not null) yield return ra.Target; break;
            case LabeledStatement ls: yield return ls.Body; break;
        }
    }

    public static IEnumerable<Node> DescendantsAndSelf(Node node)
    {
        var stack = new Stack<Node>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (var c in Children(n)) stack.Push(c);
        }
    }
}
