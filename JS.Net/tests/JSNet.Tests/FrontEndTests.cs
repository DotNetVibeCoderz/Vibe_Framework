using JSNet.Compiler;
using JSNet.Parsing;
using JSNet.Semantics;
using JSNet.Syntax;

namespace JSNet.Tests;

public class LexerTests
{
    private static List<Token> Lex(string code) => Lexer.Tokenize(new SourceText(code), new DiagnosticBag());

    [Fact]
    public void ClosingAngleBracketsAreSingleTokens()
    {
        var tokens = Lex("List<List<int>>");
        Assert.Equal(new[] { "List", "<", "List", "<", "int", ">", ">", "" }, tokens.Select(t => t.Text));
    }

    [Fact]
    public void StringsDecodeEscapes()
    {
        var t = Lex("'a\\nb\\u0041'")[0];
        Assert.Equal(TokenKind.String, t.Kind);
        Assert.Equal("a\nbA", t.Value);
    }

    [Fact]
    public void TemplatesKeepRawBodyIncludingNestedBraces()
    {
        var t = Lex("`x ${ {a: 1}.a } y`")[0];
        Assert.Equal(TokenKind.Template, t.Kind);
        Assert.Equal("x ${ {a: 1}.a } y", t.Value);
    }

    [Fact]
    public void NewLineFlagSupportsAsi()
    {
        var tokens = Lex("a\nb");
        Assert.False(tokens[0].NewLineBefore);
        Assert.True(tokens[1].NewLineBefore);
    }

    [Theory]
    [InlineData("0x1F")]
    [InlineData("1_000")]
    [InlineData("3.14")]
    [InlineData("1e9")]
    [InlineData("10m")]
    public void NumbersAreSingleTokens(string number)
    {
        var tokens = Lex(number);
        Assert.Equal(TokenKind.Number, tokens[0].Kind);
        Assert.Equal(number, tokens[0].Text);
    }

    [Fact]
    public void ReportsUnterminatedString()
    {
        var bag = new DiagnosticBag();
        Lexer.Tokenize(new SourceText("'abc"), bag);
        Assert.Contains(bag, d => d.Code == DiagnosticCodes.UnterminatedString);
    }
}

public class ParserTests
{
    private static (CompilationUnit Unit, DiagnosticBag Diagnostics) Parse(string code)
    {
        var bag = new DiagnosticBag();
        return (Parser.ParseUnit(new SourceText(code, "test.jsn"), bag), bag);
    }

    [Fact]
    public void ParsesClassWithMembers()
    {
        var (unit, bag) = Parse("""
            export class User extends Base implements IUser {
                name: string = "x";
                private age?: number;
                constructor(public id: int) { super(); }
                get display(): string { return this.name; }
                static create(): User { return new User(1); }
            }
            """);
        Assert.Empty(bag);
        var cls = Assert.IsType<ClassDeclaration>(unit.Statements[0]);
        Assert.Equal("User", cls.Name);
        Assert.Equal(5, cls.Members.Count);
        Assert.True((cls.Modifiers & Modifiers.Export) != 0);
        Assert.Contains(cls.Members, m => m.Kind == MemberKind.Getter);
        Assert.Contains(cls.Members, m => m is { Kind: MemberKind.Method, IsStatic: true });
    }

    [Fact]
    public void DistinguishesArrowFunctionsFromParentheses()
    {
        var (unit, bag) = Parse("const f = (a: number, b: number): number => a + b; const g = (1 + 2) * 3;");
        Assert.Empty(bag);
        var first = ((VariableStatement)unit.Statements[0]).Declarators[0].Init;
        var second = ((VariableStatement)unit.Statements[1]).Declarators[0].Init;
        Assert.IsType<FunctionExpression>(first);
        Assert.IsType<BinaryExpression>(second);
    }

    [Fact]
    public void ParsesGenericCallTypeArguments()
    {
        var (unit, bag) = Parse("const x = parse<User>(json); const y = a < b;");
        Assert.Empty(bag);
        var call = Assert.IsType<CallExpression>(((VariableStatement)unit.Statements[0]).Declarators[0].Init);
        Assert.Single(call.TypeArgs);
        Assert.IsType<BinaryExpression>(((VariableStatement)unit.Statements[1]).Declarators[0].Init);
    }

    [Fact]
    public void BracketAttributeVersusArrayLiteral()
    {
        var (unit, bag) = Parse("""
            [HttpGet("{id}")]
            function get(id: int) {}
            [1, 2].forEach(x => x);
            """);
        Assert.Empty(bag);
        var fn = Assert.IsType<FunctionDeclaration>(unit.Statements[0]);
        Assert.Equal("HttpGet", Assert.Single(fn.Attributes).Name);
        Assert.IsType<ExpressionStatement>(unit.Statements[1]);
    }

    [Fact]
    public void ParsesDecoratorsAndImports()
    {
        var (unit, bag) = Parse("""
            import System.Text;
            import { List as L } from "System.Collections.Generic";
            import * as IO from "System.IO";
            import { helper } from "./helper";
            @Service
            class S {}
            """);
        Assert.Empty(bag);
        var imports = unit.Statements.OfType<ImportDeclaration>().Select(i => i.Kind).ToArray();
        Assert.Equal(new[] { ImportKind.Namespace, ImportKind.Named, ImportKind.Alias, ImportKind.Module }, imports);
        Assert.Equal("Service", unit.Statements.OfType<ClassDeclaration>().Single().Attributes.Single().Name);
    }

    [Fact]
    public void AutomaticSemicolonInsertion()
    {
        var (unit, bag) = Parse("let a = 1\nlet b = 2\nconsole.log(a + b)");
        Assert.Empty(bag);
        Assert.Equal(3, unit.Statements.Count);
    }

    [Fact]
    public void ParsesAiFunctionAndPrompt()
    {
        var (unit, bag) = Parse("""
            ai function summarize(text: string): Promise<string> { `Summarize: ${text}` }
            prompt Translator { text: string; lang: string; output: string; template: `Translate ${text} to ${lang}`; }
            """);
        Assert.Empty(bag);
        Assert.True(Assert.IsType<FunctionDeclaration>(unit.Statements[0]).IsAi);
        var prompt = Assert.IsType<PromptDeclaration>(unit.Statements[1]);
        Assert.Equal(2, prompt.Fields.Count);
        Assert.NotNull(prompt.Template);
    }

    [Fact]
    public void RecoversAfterSyntaxError()
    {
        var (unit, bag) = Parse("let = ;\nconsole.log('still parsed');");
        Assert.NotEmpty(bag);
        Assert.Contains(unit.Statements, s => s is ExpressionStatement);
    }
}

public class SemanticTests
{
    private static JsNetCompilationResult Compile(string code, bool library = false) =>
        JsNetCompiler.Compile([new SourceText(code, "test.jsn")], new JsNetCompilationOptions { IsLibrary = library });

    [Fact]
    public void ConstReassignmentIsAnError() =>
        Assert.Contains(Compile("const a = 1; a = 2; console.log(a);").Diagnostics, d => d.Code == DiagnosticCodes.ConstReassignment);

    [Fact]
    public void AwaitOutsideAsyncIsAnError() =>
        Assert.Contains(Compile("function f() { await delay(1); }").Diagnostics, d => d.Code == DiagnosticCodes.AwaitOutsideAsync);

    [Fact]
    public void TopLevelAwaitIsAllowedInPrograms() =>
        Assert.DoesNotContain(Compile("await delay(1);").Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

    [Fact]
    public void UnusedVariableWarning() =>
        Assert.Contains(Compile("function f() { let unused = 1; }").Diagnostics, d => d.Code == DiagnosticCodes.UnusedVariable);

    [Fact]
    public void IntegerDivisionWarning() =>
        Assert.Contains(Compile("let a = 7; let b = 2; console.log(a / b);").Diagnostics, d => d.Code == DiagnosticCodes.IntegerDivision);

    private const string Shapes = """
        class Circle { constructor(public r: number) {} }
        class Square { constructor(public s: number) {} }
        class Triangle { constructor(public b: number) {} }
        type Shape = Circle | Square | Triangle;

        """;

    [Fact]
    public void UnionChainsReportMissingMembers()
    {
        var d = Assert.Single(Compile(Shapes + """
            function name(shape: Shape): string {
                if (shape instanceof Circle) return "circle";
                else if (shape instanceof Square) return "square";
                return "?";
            }
            console.log(name(new Circle(1)));
            """).Diagnostics, d => d.Code == DiagnosticCodes.UnhandledUnionMember);
        Assert.Contains("'Triangle'", d.Message);
    }

    [Fact]
    public void CompleteUnionChainsAndElseAreQuiet()
    {
        var result = Compile(Shapes + """
            function a(shape: Shape): string {
                if (shape instanceof Circle) return "c"; else if (shape instanceof Square) return "s"; else if (shape instanceof Triangle) return "t";
                return "?";
            }
            function b(shape: Shape): string {
                if (shape instanceof Circle) return "c"; else if (shape instanceof Square) return "s"; else return "other";
            }
            console.log(a(new Circle(1)), b(new Square(2)));
            """);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == DiagnosticCodes.UnhandledUnionMember);
    }

    [Fact]
    public void BreakOutsideLoopIsAnError() =>
        Assert.Contains(Compile("function f() { break; }").Diagnostics, d => d.Code == DiagnosticCodes.BreakOutsideLoop);

    [Fact]
    public void LibrariesRejectTopLevelStatements() =>
        Assert.Contains(Compile("console.log(1);", library: true).Diagnostics, d => d.Code == DiagnosticCodes.StatementsInLibrary);

    [Fact]
    public void InfersReturnTypes()
    {
        var result = Compile("function add(a: number, b: number) { return a + b; }\nfunction name() { return 'x'; }");
        string code = result.Files[0].Text;
        Assert.Contains("double add(double a, double b)", code);
        Assert.Contains("string name()", code);
    }

    [Theory]
    [InlineData("number", "double")]
    [InlineData("string[]", "string[]")]
    [InlineData("Array<int>", "global::System.Collections.Generic.List<int>")]
    [InlineData("Map<string, number>", "global::System.Collections.Generic.Dictionary<string, double>")]
    [InlineData("Promise<void>", "global::System.Threading.Tasks.Task")]
    [InlineData("Promise<User>", "global::System.Threading.Tasks.Task<User>")]
    [InlineData("string | null", "string?")]
    [InlineData("(x: number) => string", "global::System.Func<double, string>")]
    [InlineData("() => void", "global::System.Action")]
    [InlineData("[int, string]", "(int, string)")]
    public void MapsTypes(string jsnet, string csharp)
    {
        var parser = new Parser(new SourceText(jsnet), new DiagnosticBag());
        Assert.Equal(csharp, TypeMapper.Map(parser.ParseType()));
    }
}
