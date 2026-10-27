using JSNet.Syntax;

namespace JSNet.Parsing;

/// <summary>
/// Recursive-descent parser producing the JS.Net AST. Supports automatic semicolon insertion,
/// speculative parsing (arrow functions, generic call type arguments) and error recovery at
/// statement boundaries so a single typo does not hide every later diagnostic.
/// </summary>
public sealed class Parser
{
    private sealed class SpeculationFailed : Exception;

    private static readonly HashSet<string> ModifierWords = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "static", "readonly", "abstract", "override",
        "virtual", "sealed", "partial", "async", "declare", "export", "default",
    };

    private static readonly HashSet<string> DeclarationStarters = new(StringComparer.Ordinal)
    {
        "function", "class", "interface", "enum", "struct", "record", "const", "let", "var", "ai", "prompt", "type", "namespace",
    };

    private readonly SourceText _source;
    private readonly DiagnosticBag _diagnostics;
    private readonly List<Token> _tokens;
    private readonly List<ObjectTypeNode> _objectTypes = [];
    private int _pos;
    private int _speculating;
    private bool _noIn;

    public Parser(SourceText source, DiagnosticBag diagnostics, int start = 0, int end = -1)
    {
        _source = source;
        _diagnostics = diagnostics;
        _tokens = Lexer.Tokenize(source, diagnostics, start, end);
    }

    public static CompilationUnit ParseUnit(SourceText source, DiagnosticBag diagnostics) =>
        new Parser(source, diagnostics).ParseCompilationUnit();

    // ----------------------------------------------------------------------------- utilities

    private Token Cur => _tokens[_pos];
    private Token PeekTok(int n = 1) => _tokens[Math.Min(_pos + n, _tokens.Count - 1)];
    private bool AtEnd => Cur.Kind == TokenKind.EndOfFile;
    private bool At(string text) => Cur.Is(text);
    private bool AtIdent(string text) => Cur.Kind == TokenKind.Identifier && Cur.Text == text;
    private bool Adjacent(int i) => _tokens[_pos + i].Span.Start == _tokens[_pos + i - 1].Span.End;

    private Token Advance()
    {
        var t = Cur;
        if (_pos < _tokens.Count - 1) _pos++;
        return t;
    }

    private bool Eat(string text)
    {
        if (!At(text)) return false;
        Advance();
        return true;
    }

    private Token Expect(string text)
    {
        if (At(text)) return Advance();
        Error(DiagnosticCodes.ExpectedToken, $"Expected '{text}' but found '{Describe(Cur)}'.", Cur.Span);
        return new Token(TokenKind.Bad, text, Cur.Span, false);
    }

    private string ExpectIdentifier(bool allowKeywords = false)
    {
        if (Cur.Kind == TokenKind.Identifier || (allowKeywords && Cur.Kind == TokenKind.Keyword)) return Advance().Text;
        Error(DiagnosticCodes.ExpectedToken, $"Expected identifier but found '{Describe(Cur)}'.", Cur.Span);
        if (!AtEnd && !At("}") && !At(";") && !At(")")) Advance();
        return "__error";
    }

    private static string Describe(Token t) => t.Kind == TokenKind.EndOfFile ? "end of file" : t.Text;

    private void Error(string code, string message, TextSpan span)
    {
        if (_speculating > 0) throw new SpeculationFailed();
        _diagnostics.Error(code, message, _source, span);
    }

    private T Finish<T>(T node, int start) where T : Node
    {
        int end = _pos > 0 ? _tokens[_pos - 1].Span.End : start;
        node.Span = TextSpan.FromBounds(start, Math.Max(start, end));
        return node;
    }

    /// <summary>Automatic semicolon insertion: accept ';', a line break, '}' or end of file.</summary>
    private void ConsumeSemicolon()
    {
        if (Eat(";")) return;
        if (Cur.NewLineBefore || At("}") || AtEnd) return;
        Error(DiagnosticCodes.ExpectedToken, $"Expected ';' but found '{Describe(Cur)}'.", Cur.Span);
    }

    private bool TrySpeculate<T>(Func<T> parse, out T result)
    {
        int saved = _pos;
        _speculating++;
        try
        {
            result = parse();
            return true;
        }
        catch (SpeculationFailed)
        {
            _pos = saved;
            result = default!;
            return false;
        }
        finally
        {
            _speculating--;
        }
    }

    private void Synchronize()
    {
        while (!AtEnd)
        {
            if (Eat(";")) return;
            if (At("}")) return;
            if (Cur.NewLineBefore) return;
            Advance();
        }
    }

    // -------------------------------------------------------------------------- compilation unit

    public CompilationUnit ParseCompilationUnit()
    {
        var statements = new List<Statement>();
        while (!AtEnd)
        {
            int before = _pos;
            var stmt = ParseStatementSafe(topLevel: true);
            if (_namespaceBlock && stmt is BlockStatement { Statements: var inner })
            {
                statements.AddRange(inner);
                _namespaceBlock = false;
            }
            else if (stmt is not null) statements.Add(stmt);
            if (_pos == before) Advance();
        }
        var unit = new CompilationUnit(_source, statements) { Span = new TextSpan(0, _source.Length), ObjectTypes = _objectTypes };
        return unit;
    }

    private bool _namespaceBlock;

    private Statement? ParseStatementSafe(bool topLevel)
    {
        int errors = _diagnostics.Count;
        int start = _pos;
        var stmt = ParseStatement(topLevel);
        if (_diagnostics.Count > errors && _pos == start) Synchronize();
        return stmt;
    }

    // -------------------------------------------------------------------------------- statements

    public Statement ParseStatement(bool topLevel = false)
    {
        int start = Cur.Span.Start;
        var attributes = ParseAttributes();
        var modifiers = ParseDeclarationModifiers();

        Statement stmt = ParseStatementCore(topLevel, modifiers, attributes.Count > 0 || modifiers != Modifiers.None);
        if (attributes.Count > 0) stmt.Attributes = attributes;
        ApplyModifiers(stmt, modifiers);
        if (stmt.Span.Length == 0) Finish(stmt, start);
        return stmt;
    }

    private static void ApplyModifiers(Statement stmt, Modifiers modifiers)
    {
        if (modifiers == Modifiers.None) return;
        switch (stmt)
        {
            case FunctionDeclaration f: f.Modifiers |= modifiers; break;
            case ClassDeclaration c: c.Modifiers |= modifiers; break;
            case InterfaceDeclaration i: i.Modifiers |= modifiers; break;
            case EnumDeclaration e: e.Modifiers |= modifiers; break;
            case VariableStatement v: v.Modifiers |= modifiers; break;
            case PromptDeclaration p: p.Modifiers |= modifiers; break;
        }
    }

    private Statement ParseStatementCore(bool topLevel, Modifiers modifiers, bool declarationOnly)
    {
        int start = Cur.Span.Start;
        var t = Cur;

        if (t.Kind == TokenKind.Keyword)
        {
            switch (t.Text)
            {
                case "function": return ParseFunctionDeclaration(start, modifiers, isAi: false);
                case "class": return ParseClass(start, ClassKind.Class);
                case "interface": return ParseInterface(start);
                case "enum": return ParseEnum(start);
                case "import": return ParseImport(start);
                case "let": case "var": return ParseVariableStatementWithSemicolon(start);
                case "const":
                    if (PeekTok().Text == "enum") { Advance(); return ParseEnum(start); }
                    return ParseVariableStatementWithSemicolon(start);
            }
        }

        if (t.Kind == TokenKind.Identifier)
        {
            var next = PeekTok();
            switch (t.Text)
            {
                case "struct" when next.Kind == TokenKind.Identifier: return ParseClass(start, ClassKind.Struct);
                case "record" when next.Kind == TokenKind.Identifier: return ParseClass(start, ClassKind.Record);
                case "type" when next.Kind == TokenKind.Identifier && PeekTok(2).Is("="): return ParseTypeAlias(start);
                case "namespace" when next.Kind == TokenKind.Identifier: return ParseNamespace(start);
                case "ai" when next.IsKeyword("function"):
                    Advance();
                    return ParseFunctionDeclaration(start, modifiers, isAi: true);
                case "prompt" when next.Kind == TokenKind.Identifier && PeekTok(2).Is("{"): return ParsePrompt(start);
                case "dynamic" when next.Kind == TokenKind.Identifier && !next.NewLineBefore: return ParseVariableStatementWithSemicolon(start);
                case "using" when next.Is("(") || (next.Kind is TokenKind.Identifier && PeekTok(2).Is("=")) || next.Text is "const" or "let":
                    return ParseUsing(start);
            }
        }

        if (declarationOnly)
        {
            Error(DiagnosticCodes.InvalidDeclaration, $"Expected a declaration after modifiers/attributes but found '{Describe(Cur)}'.", Cur.Span);
            return Finish(new EmptyStatement(), start);
        }

        if (t.Kind == TokenKind.Keyword)
        {
            switch (t.Text)
            {
                case "if": return ParseIf(start);
                case "for": return ParseFor(start);
                case "while":
                {
                    Advance();
                    Expect("(");
                    var test = ParseExpression();
                    Expect(")");
                    return Finish(new WhileStatement(test, ParseStatement()), start);
                }
                case "do":
                {
                    Advance();
                    var body = ParseStatement();
                    Expect("while");
                    Expect("(");
                    var test = ParseExpression();
                    Expect(")");
                    ConsumeSemicolon();
                    return Finish(new DoWhileStatement(body, test), start);
                }
                case "return":
                {
                    Advance();
                    Expression? value = null;
                    if (!At(";") && !At("}") && !AtEnd && !Cur.NewLineBefore) value = ParseExpression();
                    ConsumeSemicolon();
                    return Finish(new ReturnStatement(value), start);
                }
                case "yield":
                {
                    Advance();
                    if (AtIdent("break")) { Advance(); ConsumeSemicolon(); return Finish(new YieldStatement(null, true), start); }
                    if (At("break")) { Advance(); ConsumeSemicolon(); return Finish(new YieldStatement(null, true), start); }
                    var value = ParseExpression();
                    ConsumeSemicolon();
                    return Finish(new YieldStatement(value, false), start);
                }
                case "break":
                {
                    Advance();
                    string? label = Cur.Kind == TokenKind.Identifier && !Cur.NewLineBefore ? Advance().Text : null;
                    ConsumeSemicolon();
                    return Finish(new BreakStatement { Label = label }, start);
                }
                case "continue":
                {
                    Advance();
                    string? label = Cur.Kind == TokenKind.Identifier && !Cur.NewLineBefore ? Advance().Text : null;
                    ConsumeSemicolon();
                    return Finish(new ContinueStatement { Label = label }, start);
                }
                case "throw":
                {
                    Advance();
                    var value = ParseExpression();
                    ConsumeSemicolon();
                    return Finish(new ThrowStatement(value), start);
                }
                case "try": return ParseTry(start);
                case "switch": return ParseSwitch(start);
                case "export":
                    // export { a, b }; / export * from "x"; → no-op: everything in an assembly is visible.
                    Advance();
                    while (!AtEnd && !At(";") && !Cur.NewLineBefore) Advance();
                    Eat(";");
                    return Finish(new EmptyStatement(), start);
            }
        }

        if (t.Kind == TokenKind.Identifier && PeekTok().Is(":"))
        {
            string label = Advance().Text;
            Advance(); // ':'
            return Finish(new LabeledStatement(label, ParseStatement()), start);
        }
        if (At("{")) return ParseBlock();
        if (Eat(";")) return Finish(new EmptyStatement(), start);

        var expr = ParseExpression();
        ConsumeSemicolon();
        return Finish(new ExpressionStatement(expr), start);
    }

    private List<AttributeNode> ParseAttributes()
    {
        var list = new List<AttributeNode>();
        while (true)
        {
            if (At("@") && PeekTok().IsIdentifierLike)
            {
                int start = Advance().Span.Start;
                list.Add(ParseAttributeBody(start));
            }
            else if (At("[") && LooksLikeAttributeList())
            {
                Advance();
                do
                {
                    int start = Cur.Span.Start;
                    string? target = null;
                    if (Cur.IsIdentifierLike && PeekTok().Is(":") && PeekTok(2).IsIdentifierLike)
                    {
                        target = Advance().Text;
                        Advance();
                    }
                    var attr = ParseAttributeBody(start);
                    list.Add(target is null ? attr : new AttributeNode(attr.Name, attr.Args) { Target = target, Span = attr.Span });
                } while (Eat(","));
                Expect("]");
            }
            else return list;
        }
    }

    private AttributeNode ParseAttributeBody(int start)
    {
        string name = ParseQualifiedName();
        var args = new List<Expression>();
        if (At("(")) args = ParseArguments();
        return Finish(new AttributeNode(name, args), start);
    }

    /// <summary>
    /// '[' starts an attribute list only when the matching ']' is followed by something that begins a declaration
    /// or class member; otherwise it is an array literal / destructuring.
    /// </summary>
    private bool LooksLikeAttributeList()
    {
        if (!PeekTok().IsIdentifierLike) return false;
        int depth = 0;
        for (int i = _pos; i < _tokens.Count; i++)
        {
            var t = _tokens[i];
            if (t.Is("[")) depth++;
            else if (t.Is("]") && --depth == 0)
            {
                var after = _tokens[Math.Min(i + 1, _tokens.Count - 1)];
                if (after.Kind == TokenKind.EndOfFile) return false;
                if (after.Is("=") || after.Is(".") || after.Is("(") || after.Is(";") || after.Is(")") || after.Is(",")) return false;
                if (!after.NewLineBefore && !after.Is("@") && !after.Is("[")) return after.IsIdentifierLike && (ModifierWords.Contains(after.Text) || DeclarationStarters.Contains(after.Text));
                return after.IsIdentifierLike || after.Is("@") || after.Is("[") || after.Is("*");
            }
            else if (t.Kind == TokenKind.EndOfFile) return false;
        }
        return false;
    }

    private Modifiers ParseDeclarationModifiers()
    {
        var mods = Modifiers.None;
        while (Cur.IsIdentifierLike && ModifierWords.Contains(Cur.Text))
        {
            // Only treat as a modifier when a declaration follows (e.g. `async` could be an identifier).
            int i = _pos;
            while (_tokens[i].IsIdentifierLike && ModifierWords.Contains(_tokens[i].Text)) i++;
            var target = _tokens[i];
            bool declFollows = target.IsIdentifierLike && (DeclarationStarters.Contains(target.Text) || target.Text == "dynamic");
            if (!declFollows) break;
            mods |= Advance().Text switch
            {
                "public" => Modifiers.Public,
                "private" => Modifiers.Private,
                "protected" => Modifiers.Protected,
                "internal" => Modifiers.Internal,
                "static" => Modifiers.Static,
                "readonly" => Modifiers.Readonly,
                "abstract" => Modifiers.Abstract,
                "override" => Modifiers.Override,
                "virtual" => Modifiers.Virtual,
                "sealed" => Modifiers.Sealed,
                "partial" => Modifiers.Partial,
                "async" => Modifiers.Async,
                "declare" => Modifiers.Declare,
                "export" => Modifiers.Export,
                _ => Modifiers.None, // default
            };
        }
        return mods;
    }

    private BlockStatement ParseBlock()
    {
        int start = Expect("{").Span.Start;
        var list = new List<Statement>();
        while (!At("}") && !AtEnd)
        {
            int before = _pos;
            var stmt = ParseStatementSafe(topLevel: false);
            if (stmt is not null) list.Add(stmt);
            if (_pos == before) Advance();
        }
        Expect("}");
        return Finish(new BlockStatement(list), start);
    }

    private Statement ParseVariableStatementWithSemicolon(int start)
    {
        var stmt = ParseVariableStatement(start);
        ConsumeSemicolon();
        return Finish(stmt, start);
    }

    private VariableStatement ParseVariableStatement(int start)
    {
        var kindTok = Advance();
        var kind = kindTok.Text switch
        {
            "const" => VariableKind.Const,
            "var" => VariableKind.Var,
            "dynamic" => VariableKind.Dynamic,
            _ => VariableKind.Let,
        };
        var declarators = new List<VariableDeclarator>();
        do
        {
            int dStart = Cur.Span.Start;
            var target = ParseBindingPattern();
            TypeNode? type = null;
            if (Eat("!")) { }
            if (Eat(":")) type = ParseType();
            Expression? init = null;
            if (Eat("=")) init = ParseAssignment();
            if (init is ClassExpression { Declaration: var cd } && target is IdentifierPattern named && cd.Name.StartsWith("__Class", StringComparison.Ordinal))
                cd.Name = named.Name;
            declarators.Add(Finish(new VariableDeclarator(target, type, init), dStart));
        } while (Eat(","));
        return Finish(new VariableStatement(kind, declarators), start);
    }

    private Pattern ParseBindingPattern()
    {
        int start = Cur.Span.Start;
        if (Eat("{"))
        {
            var props = new List<(string, Pattern, Expression?)>();
            while (!At("}") && !AtEnd)
            {
                string key = ExpectIdentifier(allowKeywords: true);
                Pattern value = new IdentifierPattern(key);
                if (Eat(":")) value = ParseBindingPattern();
                Expression? def = null;
                if (Eat("=")) def = ParseAssignment();
                props.Add((key, value, def));
                if (!Eat(",")) break;
            }
            Expect("}");
            return Finish(new ObjectPattern(props), start);
        }
        if (Eat("["))
        {
            var elems = new List<Pattern?>();
            while (!At("]") && !AtEnd)
            {
                if (At(",")) { Advance(); elems.Add(null); continue; }
                elems.Add(ParseBindingPattern());
                if (!Eat(",")) break;
            }
            Expect("]");
            return Finish(new ArrayPattern(elems), start);
        }
        return Finish(new IdentifierPattern(ExpectIdentifier()), start);
    }

    private Statement ParseUsing(int start)
    {
        Advance(); // using
        if (Eat("("))
        {
            var decl = ParseUsingDeclaration();
            Expect(")");
            var body = ParseStatement();
            return Finish(new UsingStatement(decl, body), start);
        }
        var d = ParseUsingDeclaration();
        ConsumeSemicolon();
        return Finish(new UsingStatement(d, null), start);
    }

    private VariableStatement ParseUsingDeclaration()
    {
        int start = Cur.Span.Start;
        if (Cur.Text is "const" or "let" or "var") return ParseVariableStatement(start);
        string name = ExpectIdentifier();
        TypeNode? type = null;
        if (Eat(":")) type = ParseType();
        Expect("=");
        var init = ParseAssignment();
        return Finish(new VariableStatement(VariableKind.Const, [new VariableDeclarator(new IdentifierPattern(name), type, init)]), start);
    }

    private Statement ParseIf(int start)
    {
        Advance();
        Expect("(");
        var test = ParseExpression();
        Expect(")");
        var then = ParseStatement();
        Statement? @else = null;
        if (Eat("else")) @else = ParseStatement();
        return Finish(new IfStatement(test, then, @else), start);
    }

    private Statement ParseFor(int start)
    {
        Advance(); // for
        bool isAwait = false;
        if (AtIdent("await") || At("await")) { Advance(); isAwait = true; }
        Expect("(");

        Node? init = null;
        if (Cur.Text is "let" or "const" or "var" && Cur.Kind == TokenKind.Keyword)
        {
            int declStart = Cur.Span.Start;
            var kindTok = Advance();
            var kind = kindTok.Text == "const" ? VariableKind.Const : kindTok.Text == "var" ? VariableKind.Var : VariableKind.Let;
            var target = ParseBindingPattern();
            TypeNode? type = null;
            if (Eat(":")) type = ParseType();
            if (AtIdent("of") || At("in"))
            {
                bool isIn = Advance().Text == "in";
                var iterable = isIn ? ParseExpression() : ParseAssignment();
                Expect(")");
                var body = ParseStatement();
                return Finish(new ForOfStatement(kind, target, type, iterable, body, isIn) { IsAwait = isAwait }, start);
            }
            var declarators = new List<VariableDeclarator>();
            _noIn = true;
            Expression? first = Eat("=") ? ParseAssignment() : null;
            declarators.Add(new VariableDeclarator(target, type, first));
            while (Eat(","))
            {
                var t2 = ParseBindingPattern();
                TypeNode? ty2 = Eat(":") ? ParseType() : null;
                declarators.Add(new VariableDeclarator(t2, ty2, Eat("=") ? ParseAssignment() : null));
            }
            _noIn = false;
            init = Finish(new VariableStatement(kind, declarators), declStart);
        }
        else if (Cur.Kind == TokenKind.Identifier && (PeekTok().Is("of") || PeekTok().Is("in")))
        {
            var target = new IdentifierPattern(Advance().Text);
            bool isIn = Advance().Text == "in";
            var iterable = ParseExpression();
            Expect(")");
            var body = ParseStatement();
            return Finish(new ForOfStatement(VariableKind.Let, target, null, iterable, body, isIn) { IsAwait = isAwait }, start);
        }
        else if (!At(";"))
        {
            _noIn = true;
            init = ParseExpression();
            _noIn = false;
        }

        Expect(";");
        var test = At(";") ? null : ParseExpression();
        Expect(";");
        var update = At(")") ? null : ParseExpression();
        Expect(")");
        var forBody = ParseStatement();
        return Finish(new ForStatement(init, test, update, forBody), start);
    }

    private Statement ParseTry(int start)
    {
        Advance();
        var block = ParseBlock();
        var catches = new List<CatchClause>();
        while (At("catch"))
        {
            int cStart = Advance().Span.Start;
            string? name = null;
            TypeNode? type = null;
            if (Eat("("))
            {
                name = ExpectIdentifier();
                if (Eat(":")) type = ParseType();
                Expect(")");
            }
            var body = ParseBlock();
            catches.Add(Finish(new CatchClause(name, type, body), cStart));
        }
        BlockStatement? fin = null;
        if (Eat("finally")) fin = ParseBlock();
        if (catches.Count == 0 && fin is null)
            Error(DiagnosticCodes.ExpectedToken, "Expected 'catch' or 'finally' after try block.", Cur.Span);
        return Finish(new TryStatement(block, catches, fin), start);
    }

    private Statement ParseSwitch(int start)
    {
        Advance();
        Expect("(");
        var disc = ParseExpression();
        Expect(")");
        Expect("{");
        var cases = new List<SwitchCase>();
        while (!At("}") && !AtEnd)
        {
            int cStart = Cur.Span.Start;
            Expression? test = null;
            if (Eat("case")) test = ParseExpression();
            else Expect("default");
            Expect(":");
            var body = new List<Statement>();
            while (!At("case") && !At("default") && !At("}") && !AtEnd)
            {
                int before = _pos;
                var s = ParseStatementSafe(false);
                if (s is not null) body.Add(s);
                if (_pos == before) Advance();
            }
            cases.Add(Finish(new SwitchCase(test, body), cStart));
        }
        Expect("}");
        return Finish(new SwitchStatement(disc, cases), start);
    }

    // ------------------------------------------------------------------------------ declarations

    private FunctionDeclaration ParseFunctionDeclaration(int start, Modifiers modifiers, bool isAi)
    {
        Expect("function");
        bool generator = Eat("*");
        string name = ExpectIdentifier();
        var typeParams = ParseTypeParameters();
        var parameters = ParseParameters();
        TypeNode? ret = Eat(":") ? ParseReturnType() : null;
        BlockStatement? body = null;
        if (At("{")) body = ParseBlock();
        else ConsumeSemicolon();
        return Finish(new FunctionDeclaration(name, parameters, body)
        {
            TypeParameters = typeParams,
            ReturnType = ret,
            IsGenerator = generator,
            IsAi = isAi,
            Modifiers = modifiers,
        }, start);
    }

    /// <summary>Return types may be TypeScript type predicates (<c>x is T</c>), which lower to bool.</summary>
    private TypeNode ParseReturnType()
    {
        if (Cur.Kind == TokenKind.Identifier && PeekTok().Is("is"))
        {
            int s = Cur.Span.Start;
            Advance(); Advance();
            ParseType();
            return Finish(new NamedType("boolean", []), s);
        }
        return ParseType();
    }

    private List<TypeParameter> ParseTypeParameters()
    {
        var list = new List<TypeParameter>();
        if (!At("<")) return list;
        Advance();
        while (!At(">") && !AtEnd)
        {
            int s = Cur.Span.Start;
            string name = ExpectIdentifier();
            TypeNode? constraint = null;
            if (Eat("extends")) constraint = ParseType();
            if (Eat("=")) ParseType(); // defaults are ignored (not a CLR concept)
            list.Add(Finish(new TypeParameter(name, constraint), s));
            if (!Eat(",")) break;
        }
        Expect(">");
        return list;
    }

    private List<Parameter> ParseParameters()
    {
        Expect("(");
        var list = new List<Parameter>();
        while (!At(")") && !AtEnd)
        {
            list.Add(ParseParameter());
            if (!Eat(",")) break;
        }
        Expect(")");
        return list;
    }

    private Parameter ParseParameter()
    {
        int start = Cur.Span.Start;
        var attrs = ParseAttributes();
        var mods = Modifiers.None;
        while (Cur.Kind == TokenKind.Identifier && Cur.Text is "public" or "private" or "protected" or "readonly" or "internal"
               && (PeekTok().Kind == TokenKind.Identifier || PeekTok().Is("readonly")))
        {
            mods |= Advance().Text switch
            {
                "public" => Modifiers.Public,
                "private" => Modifiers.Private,
                "protected" => Modifiers.Protected,
                "internal" => Modifiers.Internal,
                _ => Modifiers.Readonly,
            };
        }
        string? refKind = null;
        if (Cur.Kind == TokenKind.Identifier && Cur.Text is "out" or "ref" or "in" && PeekTok().Kind == TokenKind.Identifier)
            refKind = Advance().Text;
        bool rest = Eat("...");
        Pattern? pattern = null;
        string name;
        if (At("{") || At("["))
        {
            pattern = ParseBindingPattern();
            name = $"__p{start}";
        }
        else name = ExpectIdentifier(allowKeywords: Cur.Text == "this");
        bool optional = Eat("?");
        TypeNode? type = Eat(":") ? ParseType() : null;
        Expression? def = Eat("=") ? ParseAssignment() : null;
        return Finish(new Parameter(name)
        {
            Pattern = pattern,
            Type = type,
            Default = def,
            Optional = optional,
            Rest = rest,
            PropertyModifiers = mods,
            Attributes = attrs,
            RefKind = refKind,
        }, start);
    }

    private ClassDeclaration ParseClass(int start, ClassKind kind, bool nameOptional = false)
    {
        Advance(); // class/struct/record
        string name = nameOptional && Cur.Kind != TokenKind.Identifier ? string.Empty : ExpectIdentifier();
        var typeParams = ParseTypeParameters();
        TypeNode? extends = null;
        var implements = new List<TypeNode>();
        if (Eat("extends")) extends = ParseType();
        if (Eat("implements"))
        {
            do implements.Add(ParseType()); while (Eat(","));
        }
        var members = ParseMembers(isInterface: false, className: name);
        return Finish(new ClassDeclaration(name, members)
        {
            Kind = kind,
            TypeParameters = typeParams,
            Extends = extends,
            Implements = implements,
        }, start);
    }

    private InterfaceDeclaration ParseInterface(int start)
    {
        Advance();
        string name = ExpectIdentifier();
        var typeParams = ParseTypeParameters();
        var extends = new List<TypeNode>();
        if (Eat("extends"))
        {
            do extends.Add(ParseType()); while (Eat(","));
        }
        var members = ParseMembers(isInterface: true, className: name);
        return Finish(new InterfaceDeclaration(name, members) { TypeParameters = typeParams, Extends = extends }, start);
    }

    private List<ClassMember> ParseMembers(bool isInterface, string className)
    {
        Expect("{");
        var members = new List<ClassMember>();
        while (!At("}") && !AtEnd)
        {
            int before = _pos;
            if (Eat(";")) continue;
            int errors = _diagnostics.Count;
            var m = ParseMember(isInterface);
            if (m is not null) members.Add(m);
            if (_diagnostics.Count > errors && _pos == before) Synchronize();
            if (_pos == before) Advance();
        }
        Expect("}");
        return members;
    }

    private ClassMember? ParseMember(bool isInterface)
    {
        int start = Cur.Span.Start;
        var attrs = ParseAttributes();
        var mods = Modifiers.None;
        while (Cur.IsIdentifierLike && ModifierWords.Contains(Cur.Text) && Cur.Text is not ("export" or "default"))
        {
            var next = PeekTok();
            if (next.Is("(") || next.Is(":") || next.Is("=") || next.Is(";") || next.Is("?") || next.Is("<") || next.Is("}")) break;
            mods |= Advance().Text switch
            {
                "public" => Modifiers.Public,
                "private" => Modifiers.Private,
                "protected" => Modifiers.Protected,
                "internal" => Modifiers.Internal,
                "static" => Modifiers.Static,
                "readonly" => Modifiers.Readonly,
                "abstract" => Modifiers.Abstract,
                "override" => Modifiers.Override,
                "virtual" => Modifiers.Virtual,
                "sealed" => Modifiers.Sealed,
                "async" => Modifiers.Async,
                "declare" => Modifiers.Declare,
                _ => Modifiers.None,
            };
        }

        bool generator = Eat("*");
        MemberKind kind = MemberKind.Method;
        if ((AtIdent("get") || AtIdent("set")) && PeekTok().IsIdentifierLike && !PeekTok().Is("("))
        {
            kind = Advance().Text == "get" ? MemberKind.Getter : MemberKind.Setter;
        }

        if (!Cur.IsIdentifierLike && Cur.Kind != TokenKind.String)
        {
            Error(DiagnosticCodes.UnexpectedToken, $"Unexpected '{Describe(Cur)}' in type body.", Cur.Span);
            return null;
        }
        string name = Cur.Kind == TokenKind.String ? Advance().Value! : Advance().Text;
        if (name == "constructor" && At("(")) kind = MemberKind.Constructor;
        bool optional = Eat("?");
        Eat("!");

        if (At("(") || At("<"))
        {
            var typeParams = ParseTypeParameters();
            var parameters = ParseParameters();
            TypeNode? ret = Eat(":") ? ParseReturnType() : null;
            BlockStatement? body = null;
            if (At("{")) body = ParseBlock();
            else ConsumeSemicolon();
            return Finish(new ClassMember(kind, name)
            {
                Modifiers = mods,
                Type = ret,
                Parameters = parameters,
                TypeParameters = typeParams,
                Body = body,
                IsGenerator = generator,
                Attributes = attrs,
                Optional = optional,
            }, start);
        }

        TypeNode? type = Eat(":") ? ParseType() : null;
        Expression? init = Eat("=") ? ParseAssignment() : null;
        if (!(isInterface && Eat(","))) ConsumeSemicolon();
        return Finish(new ClassMember(MemberKind.Field, name)
        {
            Modifiers = mods,
            Type = type,
            Init = init,
            Optional = optional,
            Attributes = attrs,
        }, start);
    }

    private EnumDeclaration ParseEnum(int start)
    {
        Expect("enum");
        string name = ExpectIdentifier();
        Expect("{");
        var members = new List<EnumMember>();
        while (!At("}") && !AtEnd)
        {
            int s = Cur.Span.Start;
            string m = Cur.Kind == TokenKind.String ? Advance().Value! : ExpectIdentifier(allowKeywords: true);
            Expression? value = Eat("=") ? ParseAssignment() : null;
            members.Add(Finish(new EnumMember(m, value), s));
            if (!Eat(",")) break;
        }
        Expect("}");
        return Finish(new EnumDeclaration(name, members), start);
    }

    private Statement ParseTypeAlias(int start)
    {
        Advance(); // type
        string name = ExpectIdentifier();
        Expect("=");
        var type = ParseType();
        ConsumeSemicolon();
        return Finish(new TypeAliasDeclaration(name, type), start);
    }

    private Statement ParseNamespace(int start)
    {
        Advance();
        string name = ParseQualifiedName();
        if (At("{"))
        {
            // Block form: `namespace A.B { ... }` → applies to the whole file (one namespace per file).
            var ns = Finish(new NamespaceDeclaration(name), start);
            var block = ParseBlock();
            block.Statements.Insert(0, ns);
            _namespaceBlock = true;
            return block;
        }
        ConsumeSemicolon();
        return Finish(new NamespaceDeclaration(name), start);
    }

    private Statement ParsePrompt(int start)
    {
        Advance(); // prompt
        string name = ExpectIdentifier();
        Expect("{");
        var fields = new List<ClassMember>();
        Expression? template = null;
        TypeNode? output = null;
        while (!At("}") && !AtEnd)
        {
            int s = Cur.Span.Start;
            string key = ExpectIdentifier(allowKeywords: true);
            Expect(":");
            if (key == "template")
            {
                template = ParseAssignment();
            }
            else if (key == "output")
            {
                output = ParseType();
            }
            else
            {
                var type = ParseType();
                fields.Add(Finish(new ClassMember(MemberKind.Field, key) { Type = type }, s));
            }
            if (!Eat(";")) Eat(",");
        }
        Expect("}");
        return Finish(new PromptDeclaration(name, fields, template, output), start);
    }

    private Statement ParseImport(int start)
    {
        Advance(); // import
        if (Cur.Kind == TokenKind.String)
        {
            string side = Advance().Value!;
            ConsumeSemicolon();
            return Finish(new ImportDeclaration(ImportKind.Module, side), start);
        }
        if (AtIdent("static"))
        {
            Advance();
            string ns = ParseQualifiedName();
            ConsumeSemicolon();
            return Finish(new ImportDeclaration(ImportKind.Static, ns), start);
        }
        if (Eat("*"))
        {
            ExpectContextual("as");
            string alias = ExpectIdentifier();
            ExpectContextual("from");
            string module = ExpectModuleString();
            ConsumeSemicolon();
            return Finish(new ImportDeclaration(IsRelative(module) ? ImportKind.Module : ImportKind.Alias, module) { Alias = alias }, start);
        }
        if (Eat("{"))
        {
            var names = new List<(string, string)>();
            while (!At("}") && !AtEnd)
            {
                string n = ExpectIdentifier(allowKeywords: true);
                string alias = n;
                if (AtIdent("as")) { Advance(); alias = ExpectIdentifier(); }
                names.Add((n, alias));
                if (!Eat(",")) break;
            }
            Expect("}");
            ExpectContextual("from");
            string module = ExpectModuleString();
            ConsumeSemicolon();
            return Finish(new ImportDeclaration(IsRelative(module) ? ImportKind.Module : ImportKind.Named, module) { Names = names }, start);
        }

        string qualified = ParseQualifiedName();
        if (Eat("="))
        {
            // import X = A.B.C;
            string target = ParseQualifiedName();
            ConsumeSemicolon();
            return Finish(new ImportDeclaration(ImportKind.Alias, target) { Alias = qualified }, start);
        }
        if (AtIdent("from"))
        {
            // import Default from "Ns" → alias.
            Advance();
            string module = ExpectModuleString();
            ConsumeSemicolon();
            return Finish(new ImportDeclaration(IsRelative(module) ? ImportKind.Module : ImportKind.Alias, module) { Alias = qualified }, start);
        }
        ConsumeSemicolon();
        return Finish(new ImportDeclaration(ImportKind.Namespace, qualified), start);
    }

    private static bool IsRelative(string module) => module.StartsWith('.') || module.StartsWith('/');

    private void ExpectContextual(string word)
    {
        if (Cur.IsIdentifierLike && Cur.Text == word) { Advance(); return; }
        Error(DiagnosticCodes.ExpectedToken, $"Expected '{word}' but found '{Describe(Cur)}'.", Cur.Span);
    }

    private string ExpectModuleString()
    {
        if (Cur.Kind == TokenKind.String) return Advance().Value!;
        Error(DiagnosticCodes.ExpectedToken, "Expected module string.", Cur.Span);
        return string.Empty;
    }

    private string ParseQualifiedName()
    {
        var name = ExpectIdentifier(allowKeywords: true);
        while (At(".") && PeekTok().IsIdentifierLike)
        {
            Advance();
            name += "." + Advance().Text;
        }
        return name;
    }

    // ------------------------------------------------------------------------------------- types

    public TypeNode ParseType(bool allowNullableSuffix = true)
    {
        int start = Cur.Span.Start;
        Eat("|");
        var first = ParsePostfixType(allowNullableSuffix);
        if (!At("|")) return first;
        var members = new List<TypeNode> { first };
        while (Eat("|")) members.Add(ParsePostfixType(allowNullableSuffix));
        var nonNull = members.Where(m => m is not NamedType { Name: "null" or "undefined" }).ToList();
        if (nonNull.Count == 1 && nonNull.Count != members.Count) return Finish(new NullableType(nonNull[0]), start);
        if (nonNull.Count > 0 && nonNull.All(m => m is NamedType { Name: "string" })) return nonNull[0];
        return Finish(new UnionType(members), start);
    }

    private TypeNode ParsePostfixType(bool allowNullableSuffix)
    {
        int start = Cur.Span.Start;
        var type = ParsePrimaryType();
        while (true)
        {
            if (At("[") && PeekTok().Is("]") && !Cur.NewLineBefore)
            {
                Advance(); Advance();
                type = Finish(new ArrayType(type), start);
            }
            else if (allowNullableSuffix && At("?") && IsNullableSuffix())
            {
                Advance();
                type = Finish(new NullableType(type), start);
            }
            else return type;
        }
    }

    /// <summary>A '?' directly after a type (<c>string?</c>) followed by a token that cannot start an expression.</summary>
    private bool IsNullableSuffix()
    {
        if (Cur.Span.Start != _tokens[_pos - 1].Span.End) return false;
        var n = PeekTok();
        return n.Is("=") || n.Is(",") || n.Is(")") || n.Is(";") || n.Is(">") || n.Is("{") || n.Is("]") || n.Is("|") || n.Is("=>") || n.NewLineBefore || n.Kind == TokenKind.EndOfFile;
    }

    private TypeNode ParsePrimaryType()
    {
        int start = Cur.Span.Start;
        if (At("("))
        {
            if (LooksLikeFunctionType())
            {
                var parameters = ParseParameters();
                Expect("=>");
                var ret = ParseType();
                return Finish(new FunctionType(parameters, ret), start);
            }
            Advance();
            var inner = ParseType();
            Expect(")");
            return inner;
        }
        if (Eat("["))
        {
            var elems = new List<TypeNode>();
            while (!At("]") && !AtEnd)
            {
                if (Cur.Kind == TokenKind.Identifier && PeekTok().Is(":")) { Advance(); Advance(); }
                elems.Add(ParseType());
                if (!Eat(",")) break;
            }
            Expect("]");
            return Finish(new TupleType(elems), start);
        }
        if (Cur.Kind == TokenKind.String)
        {
            Advance();
            return Finish(new NamedType("string", []), start);
        }
        if (Cur.Kind == TokenKind.Number)
        {
            Advance();
            return Finish(new NamedType("number", []), start);
        }
        if (At("{"))
        {
            var members = ParseMembers(isInterface: true, className: string.Empty);
            if (members.Any(m => m.Kind != MemberKind.Field))
                Error(DiagnosticCodes.UnsupportedFeature, "Object type literals may only declare properties; use an interface for methods.", Cur.Span);
            var objectType = Finish(new ObjectTypeNode(members.Where(m => m.Kind == MemberKind.Field).ToList()), start);
            _objectTypes.Add(objectType);
            return objectType;
        }
        if (Cur.Is("typeof")) Advance();

        string name = ParseQualifiedName();
        var typeArgs = new List<TypeNode>();
        if (At("<"))
        {
            Advance();
            while (!At(">") && !AtEnd)
            {
                typeArgs.Add(ParseType());
                if (!Eat(",")) break;
            }
            Expect(">");
        }
        return Finish(new NamedType(name, typeArgs), start);
    }

    private bool LooksLikeFunctionType()
    {
        var n = PeekTok();
        if (n.Is(")")) return PeekTok(2).Is("=>");
        if (n.Is("...")) return true;
        if (n.IsIdentifierLike)
        {
            var n2 = PeekTok(2);
            return n2.Is(":") || n2.Is(",") || n2.Is("?") || (n2.Is(")") && PeekTok(3).Is("=>"));
        }
        return false;
    }

    // ------------------------------------------------------------------------------- expressions

    public Expression ParseExpression()
    {
        int start = Cur.Span.Start;
        var expr = ParseAssignment();
        if (!At(",")) return expr;
        // Comma operator: only meaningful in for-updates; lower to a sequence via a helper call.
        var list = new List<Expression> { expr };
        while (Eat(",")) list.Add(ParseAssignment());
        return Finish(new CallExpression(new IdentifierExpression("__sequence"), [], list, false), start);
    }

    private static readonly HashSet<string> AssignmentOps = new(StringComparer.Ordinal)
    {
        "=", "+=", "-=", "*=", "/=", "%=", "**=", "<<=", "&=", "|=", "^=", "&&=", "||=", "??=",
    };

    public Expression ParseAssignment()
    {
        int start = Cur.Span.Start;
        if (TryParseArrow(out var arrow)) return arrow!;
        if ((At("async") || AtIdent("async")) && PeekTok().IsKeyword("function") && !PeekTok().NewLineBefore)
        {
            Advance();
            return ParseFunctionExpression(start, isAsync: true);
        }

        var left = ParseConditional();

        // '>>=' and '>>>=' are lexed as separate tokens.
        if (At(">") && Adjacent(1) && PeekTok().Is(">"))
        {
            if (PeekTok(2).Is("=") && _tokens[_pos + 2].Span.Start == _tokens[_pos + 1].Span.End)
            {
                Advance(); Advance(); Advance();
                return Finish(new AssignmentExpression(">>=", left, ParseAssignment()), start);
            }
        }
        if (Cur.Kind == TokenKind.Punctuator && AssignmentOps.Contains(Cur.Text))
        {
            string op = Advance().Text;
            var value = ParseAssignment();
            return Finish(new AssignmentExpression(op, left, value), start);
        }
        return left;
    }

    private bool TryParseArrow(out Expression? result)
    {
        result = null;
        int start = Cur.Span.Start;
        bool isAsync = false;
        int offset = 0;
        if ((AtIdent("async")) && !PeekTok().NewLineBefore && (PeekTok().Is("(") || (PeekTok().Kind == TokenKind.Identifier && PeekTok(2).Is("=>"))))
        {
            isAsync = true;
            offset = 1;
        }
        var first = PeekTok(offset);
        if (first.Kind == TokenKind.Identifier && PeekTok(offset + 1).Is("=>"))
        {
            if (isAsync) Advance();
            var pStart = Cur.Span.Start;
            var param = Finish(new Parameter(Advance().Text), pStart);
            Advance(); // =>
            result = FinishArrow(start, [param], null, isAsync);
            return true;
        }
        if (!first.Is("(") && !first.Is("<")) return false;

        int saved = _pos;
        if (isAsync) Advance();
        if (TrySpeculate(() =>
            {
                ParseTypeParameters();
                var ps = ParseParameters();
                TypeNode? ret = null;
                if (Eat(":")) ret = ParseReturnType();
                if (!At("=>")) throw new SpeculationFailed();
                return (ps, ret);
            }, out var header))
        {
            Advance(); // =>
            result = FinishArrow(start, header.ps, header.ret, isAsync);
            return true;
        }
        _pos = saved;
        return false;
    }

    private Expression FinishArrow(int start, List<Parameter> parameters, TypeNode? ret, bool isAsync)
    {
        Node body = At("{") ? ParseBlock() : ParseAssignment();
        return Finish(new FunctionExpression(parameters, body) { IsArrow = true, IsAsync = isAsync, ReturnType = ret }, start);
    }

    private Expression ParseFunctionExpression(int start, bool isAsync)
    {
        Expect("function");
        Eat("*");
        string? name = Cur.Kind == TokenKind.Identifier ? Advance().Text : null;
        ParseTypeParameters();
        var parameters = ParseParameters();
        TypeNode? ret = Eat(":") ? ParseReturnType() : null;
        var body = ParseBlock();
        return Finish(new FunctionExpression(parameters, body) { IsAsync = isAsync, ReturnType = ret, Name = name }, start);
    }

    private Expression ParseConditional()
    {
        int start = Cur.Span.Start;
        var test = ParseBinary(0);
        if (!At("?")) return test;
        Advance();
        bool savedNoIn = _noIn;
        _noIn = false;
        var whenTrue = ParseAssignment();
        _noIn = savedNoIn;
        Expect(":");
        var whenFalse = ParseAssignment();
        return Finish(new ConditionalExpression(test, whenTrue, whenFalse), start);
    }

    /// <summary>Returns the binary operator at the cursor (joining split '>' tokens), its token count and precedence.</summary>
    private (string Op, int Tokens, int Prec) PeekBinaryOperator()
    {
        var t = Cur;
        if (t.Kind == TokenKind.Punctuator && t.Text == ">")
        {
            bool adj1 = _pos + 1 < _tokens.Count && _tokens[_pos + 1].Span.Start == t.Span.End;
            if (adj1 && PeekTok().Is(">"))
            {
                bool adj2 = _tokens[_pos + 2].Span.Start == _tokens[_pos + 1].Span.End;
                if (adj2 && PeekTok(2).Is(">"))
                {
                    if (_tokens[_pos + 3].Span.Start == _tokens[_pos + 2].Span.End && PeekTok(3).Is("=")) return (string.Empty, 0, -1);
                    return (">>>", 3, 11);
                }
                if (adj2 && PeekTok(2).Is("=")) return (string.Empty, 0, -1); // >>=
                return (">>", 2, 11);
            }
            if (adj1 && PeekTok().Is("=")) return (">=", 2, 10);
            return (">", 1, 10);
        }
        if (t.Kind == TokenKind.Identifier && (t.Text is "as" or "is") && !t.NewLineBefore) return (t.Text, 1, 10);
        if (t.Kind is not (TokenKind.Punctuator or TokenKind.Keyword)) return (string.Empty, 0, -1);
        int prec = t.Text switch
        {
            "??" => 1,
            "||" => 2,
            "&&" => 3,
            "|" => 4,
            "^" => 5,
            "&" => 6,
            "==" or "!=" or "===" or "!==" => 7,
            "<" or "<=" or "instanceof" => 10,
            "in" => _noIn ? -1 : 10,
            "<<" => 11,
            "+" or "-" => 12,
            "*" or "/" or "%" => 13,
            "**" => 14,
            _ => -1,
        };
        return prec < 0 ? (string.Empty, 0, -1) : (t.Text, 1, prec);
    }

    private Expression ParseBinary(int minPrec)
    {
        int start = Cur.Span.Start;
        var left = ParseUnary();
        while (true)
        {
            var (op, count, prec) = PeekBinaryOperator();
            if (prec < 0 || prec < minPrec) break;
            for (int i = 0; i < count; i++) Advance();
            if (op is "as" or "is")
            {
                var type = ParseType(allowNullableSuffix: false);
                left = Finish(op == "as" ? (Expression)new AsExpression(left, type) : new IsTypeExpression(left, type), start);
                continue;
            }
            if (op == "instanceof")
            {
                var type = ParseType(allowNullableSuffix: false);
                left = Finish(new IsTypeExpression(left, type), start);
                continue;
            }
            // '**' is right-associative; everything else is left-associative.
            var right = op == "**" ? ParseBinary(prec) : ParseBinary(prec + 1);
            left = Finish(new BinaryExpression(op, left, right), start);
        }
        return left;
    }

    private Expression ParseUnary()
    {
        int start = Cur.Span.Start;
        var t = Cur;
        if (t.Kind == TokenKind.Punctuator && t.Text is "!" or "-" or "+" or "~" or "++" or "--")
        {
            Advance();
            var operand = ParseUnary();
            return Finish(new UnaryExpression(t.Text, operand), start);
        }
        if (t.Kind == TokenKind.Keyword && t.Text is "typeof" or "void" or "delete")
        {
            Advance();
            var operand = ParseUnary();
            return Finish(new UnaryExpression(t.Text, operand), start);
        }
        if ((t.Kind == TokenKind.Identifier && t.Text == "await") && !PeekTok().Is("=>") && !PeekTok().Is(")") && !PeekTok().Is(";") && !PeekTok().Is("."))
        {
            Advance();
            var operand = ParseUnary();
            return Finish(new AwaitExpression(operand), start);
        }
        return ParsePostfix();
    }

    private Expression ParsePostfix()
    {
        int start = Cur.Span.Start;
        var expr = ParseCallOrMember();
        while (true)
        {
            if ((At("++") || At("--")) && !Cur.NewLineBefore)
            {
                expr = Finish(new UnaryExpression(Advance().Text, expr, prefix: false), start);
            }
            else return expr;
        }
    }

    private Expression ParseCallOrMember()
    {
        int start = Cur.Span.Start;
        Expression expr = At("new") ? ParseNew() : ParsePrimary();
        while (true)
        {
            if (At("."))
            {
                Advance();
                string name = Cur.IsIdentifierLike ? Advance().Text : ExpectIdentifier(allowKeywords: true);
                expr = Finish(new MemberExpression(expr, name, optional: false), start);
            }
            else if (At("?."))
            {
                Advance();
                if (At("(")) expr = Finish(new CallExpression(expr, [], ParseArguments(), optional: true), start);
                else if (Eat("["))
                {
                    var idx = ParseExpression();
                    Expect("]");
                    expr = Finish(new IndexExpression(expr, idx, optional: true), start);
                }
                else expr = Finish(new MemberExpression(expr, ExpectIdentifier(allowKeywords: true), optional: true), start);
            }
            else if (At("[") && !Cur.NewLineBefore)
            {
                Advance();
                var idx = ParseExpression();
                Expect("]");
                expr = Finish(new IndexExpression(expr, idx, optional: false), start);
            }
            else if (At("("))
            {
                expr = Finish(new CallExpression(expr, [], ParseArguments(), optional: false), start);
            }
            else if (At("<") && TrySpeculate(ParseTypeArgumentsForCall, out var typeArgs))
            {
                expr = Finish(new CallExpression(expr, typeArgs, ParseArguments(), optional: false), start);
            }
            else if (At("!") && !Cur.NewLineBefore && !(PeekTok().Is("=") && Adjacent(1)))
            {
                // Non-null assertion: `value!`
                Advance();
                expr = Finish(new NonNullExpression(expr), start);
            }
            else if (Cur.Kind == TokenKind.Template && !Cur.NewLineBefore)
            {
                // Tagged template: tag`...` → tag(templateString)
                var tpl = ParseTemplate(Advance());
                expr = Finish(new CallExpression(expr, [], [tpl], optional: false), start);
            }
            else return expr;
        }
    }

    private List<TypeNode> ParseTypeArgumentsForCall()
    {
        Expect("<");
        var list = new List<TypeNode>();
        while (!At(">") && !AtEnd)
        {
            list.Add(ParseType());
            if (!Eat(",")) break;
        }
        Expect(">");
        if (!At("(")) throw new SpeculationFailed();
        return list;
    }

    private List<Expression> ParseArguments()
    {
        Expect("(");
        var args = new List<Expression>();
        bool savedNoIn = _noIn;
        _noIn = false;
        while (!At(")") && !AtEnd)
        {
            int s = Cur.Span.Start;
            if (Eat("...")) args.Add(Finish(new SpreadExpression(ParseAssignment()), s));
            else if (Cur.Kind == TokenKind.Identifier && Cur.Text is "out" or "ref" or "in" && PeekTok().IsIdentifierLike && !PeekTok().Is("=>"))
            {
                string modifier = Advance().Text;
                if (Cur.Text is "let" or "const" or "var" && Cur.Kind == TokenKind.Keyword)
                {
                    Advance();
                    string name = ExpectIdentifier();
                    TypeNode? type = Eat(":") ? ParseType() : null;
                    args.Add(Finish(new RefArgumentExpression(modifier, null, name, type), s));
                }
                else args.Add(Finish(new RefArgumentExpression(modifier, ParseUnary(), null, null), s));
            }
            else args.Add(ParseAssignment());
            if (!Eat(",")) break;
        }
        _noIn = savedNoIn;
        Expect(")");
        return args;
    }

    private Expression ParseNew()
    {
        int start = Expect("new").Span.Start;
        var type = ParsePostfixType(allowNullableSuffix: false);
        if (At("[") && !PeekTok().Is("]"))
        {
            Advance();
            var size = ParseExpression();
            Expect("]");
            return Finish(new NewExpression(type, [size]) { IsArrayCreation = true }, start);
        }
        var args = At("(") ? ParseArguments() : [];
        ObjectExpression? init = null;
        if (At("{") && !Cur.NewLineBefore) init = (ObjectExpression)ParseObjectLiteral();
        return Finish(new NewExpression(type, args) { Initializer = init }, start);
    }

    private Expression ParsePrimary()
    {
        int start = Cur.Span.Start;
        var t = Cur;
        switch (t.Kind)
        {
            case TokenKind.Number:
                Advance();
                return Finish(new LiteralExpression(LiteralKind.Number, t.Text), start);
            case TokenKind.String:
                Advance();
                return Finish(new LiteralExpression(LiteralKind.String, t.Text, t.Value), start);
            case TokenKind.Template:
                Advance();
                return ParseTemplate(t);
            case TokenKind.Regex:
                Advance();
                return Finish(new RegexExpression(t.Value ?? string.Empty, t.Text), start);
            case TokenKind.Identifier:
                Advance();
                return Finish(new IdentifierExpression(t.Text), start);
            case TokenKind.Keyword:
                switch (t.Text)
                {
                    case "true": Advance(); return Finish(new LiteralExpression(LiteralKind.True, "true"), start);
                    case "false": Advance(); return Finish(new LiteralExpression(LiteralKind.False, "false"), start);
                    case "null": Advance(); return Finish(new LiteralExpression(LiteralKind.Null, "null"), start);
                    case "undefined": Advance(); return Finish(new LiteralExpression(LiteralKind.Undefined, "undefined"), start);
                    case "this": Advance(); return Finish(new ThisExpression(), start);
                    case "super": Advance(); return Finish(new SuperExpression(), start);
                    case "function": return ParseFunctionExpression(start, isAsync: false);
                    case "class":
                    {
                        var decl = ParseClass(start, ClassKind.Class, nameOptional: true);
                        if (decl.Name.Length == 0) decl.Name = $"__Class{start}";
                        return Finish(new ClassExpression(decl), start);
                    }
                }
                break;
            case TokenKind.Punctuator:
                switch (t.Text)
                {
                    case "(":
                    {
                        Advance();
                        bool savedNoIn = _noIn;
                        _noIn = false;
                        var inner = ParseExpression();
                        _noIn = savedNoIn;
                        Expect(")");
                        return Finish(new ParenthesizedExpression(inner), start);
                    }
                    case "[":
                    {
                        Advance();
                        var elems = new List<Expression>();
                        while (!At("]") && !AtEnd)
                        {
                            int s = Cur.Span.Start;
                            if (Eat("...")) elems.Add(Finish(new SpreadExpression(ParseAssignment()), s));
                            else elems.Add(ParseAssignment());
                            if (!Eat(",")) break;
                        }
                        Expect("]");
                        return Finish(new ArrayExpression(elems), start);
                    }
                    case "{":
                        return ParseObjectLiteral();
                }
                break;
        }

        Error(DiagnosticCodes.UnexpectedToken, $"Unexpected '{Describe(t)}'.", t.Span);
        if (!AtEnd && !At(";") && !At("}") && !At(")")) Advance();
        return Finish(new LiteralExpression(LiteralKind.Null, "null"), start);
    }

    private Expression ParseObjectLiteral()
    {
        int start = Expect("{").Span.Start;
        var props = new List<ObjectProperty>();
        while (!At("}") && !AtEnd)
        {
            int s = Cur.Span.Start;
            if (Eat("..."))
            {
                props.Add(Finish(new ObjectProperty("...", ParseAssignment(), false) { IsSpread = true }, s));
            }
            else
            {
                bool isAsync = AtIdent("async") && PeekTok().IsIdentifierLike;
                if (isAsync) Advance();
                string key = Cur.Kind == TokenKind.String ? Advance().Value! : Cur.Kind == TokenKind.Number ? Advance().Text : ExpectIdentifier(allowKeywords: true);
                if (At("("))
                {
                    var parameters = ParseParameters();
                    TypeNode? ret = Eat(":") ? ParseReturnType() : null;
                    var body = ParseBlock();
                    var fn = Finish(new FunctionExpression(parameters, body) { IsArrow = true, IsAsync = isAsync, ReturnType = ret }, s);
                    props.Add(Finish(new ObjectProperty(key, fn, false), s));
                }
                else if (Eat(":"))
                {
                    props.Add(Finish(new ObjectProperty(key, ParseAssignment(), false), s));
                }
                else
                {
                    props.Add(Finish(new ObjectProperty(key, Finish(new IdentifierExpression(key), s), true), s));
                }
            }
            if (!Eat(",")) break;
        }
        Expect("}");
        return Finish(new ObjectExpression(props), start);
    }

    /// <summary>Splits a template body into literal chunks and <c>${...}</c> holes, parsing each hole in place.</summary>
    private Expression ParseTemplate(Token token)
    {
        string body = token.Value ?? string.Empty;
        int bodyStart = token.Span.Start + 1;
        var quasis = new List<string>();
        var holes = new List<Expression>();
        var chunk = new System.Text.StringBuilder();
        int i = 0;
        while (i < body.Length)
        {
            char c = body[i];
            if (c == '\\' && i + 1 < body.Length)
            {
                chunk.Append(c).Append(body[i + 1]);
                i += 2;
                continue;
            }
            if (c == '$' && i + 1 < body.Length && body[i + 1] == '{')
            {
                int exprStart = i + 2;
                int depth = 1, j = exprStart;
                while (j < body.Length && depth > 0)
                {
                    char d = body[j];
                    if (d == '{') depth++;
                    else if (d == '}') { depth--; if (depth == 0) break; }
                    else if (d is '"' or '\'')
                    {
                        j++;
                        while (j < body.Length && body[j] != d) { if (body[j] == '\\') j++; j++; }
                    }
                    else if (d == '`')
                    {
                        j++;
                        int nd = 0;
                        while (j < body.Length && !(body[j] == '`' && nd == 0))
                        {
                            if (body[j] == '\\') j++;
                            else if (body[j] == '$' && j + 1 < body.Length && body[j + 1] == '{') { nd++; j++; }
                            else if (body[j] == '}' && nd > 0) nd--;
                            j++;
                        }
                    }
                    j++;
                }
                quasis.Add(chunk.ToString());
                chunk.Clear();
                var sub = new Parser(_source, _diagnostics, bodyStart + exprStart, bodyStart + j);
                holes.Add(sub.ParseExpression());
                i = j + 1;
                continue;
            }
            chunk.Append(c);
            i++;
        }
        quasis.Add(chunk.ToString());
        return Finish(new TemplateExpression(quasis, holes), token.Span.Start);
    }
}
