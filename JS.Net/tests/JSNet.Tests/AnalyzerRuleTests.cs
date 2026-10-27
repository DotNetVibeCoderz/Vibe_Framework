using JSNet.Analyzers;
using JSNet.Compiler;
using JSNet.Scripting;
using JSNet.Syntax;

namespace JSNet.Tests;

/// <summary>Custom analyzer rules written in JS.Net (`rule` declarations, design §22).</summary>
public class AnalyzerRuleTests
{
    private const string Rules = """
        rule NoConsoleLog {
            id: "APP001";
            severity: "warning";
            description: "console.log should not ship in libraries";
            check(node: Node, ctx: RuleContext): void {
                if (node instanceof CallExpression && node.Callee instanceof MemberExpression) {
                    const member = node.Callee as MemberExpression;
                    if (ctx.text(member) == "console.log") ctx.report(node, "Use a logger instead of console.log");
                }
            }
        }

        rule MaxParameters {
            id: "APP002";
            severity: "error";
            check(node: Node, ctx: RuleContext): void {
                if (node instanceof FunctionDeclaration && node.Parameters.Count > 3) ctx.report(node, `'${node.Name}' has too many parameters`);
            }
        }

        console.log("rules compiled");
        """;

    private static IReadOnlyList<Rule> CompileRules()
    {
        var compiled = JsNetScript.Compile([new SourceText(Rules, "rules.jsn")],
            new ScriptOptions { AdditionalReferences = [typeof(Rule).Assembly, typeof(Node).Assembly] });
        Assert.True(compiled.Assembly is not null, string.Join("\n", compiled.Diagnostics) + "\n" + string.Join("\n", compiled.Files.Select(f => f.Text)));
        return RuleRunner.FromAssembly(compiled.Assembly!);
    }

    [Fact]
    public void RulesWrittenInJsNetReportDiagnostics()
    {
        var rules = CompileRules();
        Assert.Equal(["APP001", "APP002"], rules.Select(r => r.id).OrderBy(x => x));

        var target = new SourceText("""
            export function add(a: int, b: int): int { return a + b; }
            export function tooMany(a: int, b: int, c: int, d: int): int { console.log("debug"); return a; }
            """, "lib.jsn");
        var result = JsNetCompiler.Compile([target], new JsNetCompilationOptions { IsLibrary = true, Rules = rules });

        var app001 = Assert.Single(result.Diagnostics, d => d.Code == "APP001");
        Assert.Equal(DiagnosticSeverity.Warning, app001.Severity);
        Assert.Equal("Use a logger instead of console.log", app001.Message);
        var app002 = Assert.Single(result.Diagnostics, d => d.Code == "APP002");
        Assert.Equal(DiagnosticSeverity.Error, app002.Severity);
        Assert.Contains("tooMany", app002.Message);
        Assert.False(result.Success);
    }
}
