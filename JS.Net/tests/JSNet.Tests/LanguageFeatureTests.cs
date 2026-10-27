using JSNet.Scripting;

namespace JSNet.Tests;

/// <summary>Language features added after 0.1 (object type literals, spread, regex, class expressions, labels, out/ref, unions).</summary>
public class LanguageFeatureTests
{
    private static async Task<string> Run(string code)
    {
        var result = await JsNetScript.RunAsync(code, "features.jsn", new ScriptOptions { CaptureOutput = true });
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + result.Exception + "\n--- C# ---\n" +
            string.Join("\n", result.GeneratedFiles.Select(f => f.Text)));
        return result.Output.Replace("\r\n", "\n").TrimEnd();
    }

    [Fact]
    public async Task ObjectTypeLiteralsGenerateRecords() => Assert.Equal("5 { x: 3, y: 4 } true", await Run("""
        type Point = { x: number; y: number };
        function len(p: { x: number, y: number }): number { return Math.sqrt(p.x * p.x + p.y * p.y); }
        const p: Point = { x: 3, y: 4 };
        const q: { x: number; y: number } = { x: 3, y: 4 };
        console.log(len(p), p, p == q);
        """));

    [Fact]
    public async Task ObjectSpreadMergesLaterPropertiesLast() => Assert.Equal("Ada lead core\ndark 14", await Run("""
        const base = { name: "Ada", role: "dev" };
        const merged = { ...base, role: "lead", team: "core" };
        console.log(merged.name, merged.role, merged.team);
        class Settings { theme: string = "light"; size: int = 12; }
        const s: Settings = { ...{ theme: "dark" }, size: 14 };
        console.log(s.theme, s.size);
        """));

    [Fact]
    public async Task RegexLiteralsAndDivisionCoexist() => Assert.Equal("true [ '10-20', '10', '20' ] a#b#c#\n[ 'a', 'b', 'c' ] true 2.5", await Run("""
        const re = /(\d+)-(\d+)/;
        console.log(re.test("10-20"), re.exec("a 10-20 b"), "a1b22c333".replace(/\d+/g, "#"));
        const ten = 10.0;
        console.log("a, b;c".split(/[,;]\s*/), "x".match(/y/) == null, ten / 4);
        """));

    [Fact]
    public async Task ClassExpressionsAndNestedClassesAreHoisted() => Assert.Equal("hi Bo 2", await Run("""
        const Greeter = class {
            constructor(public name: string) {}
            hi(): string { return `hi ${this.name}`; }
        };
        function makeCounter(): int {
            class Counter { n: int = 0; inc(): int { return ++this.n; } }
            const c = new Counter();
            c.inc();
            return c.inc();
        }
        console.log(new Greeter("Bo").hi(), makeCounter());
        """));

    [Fact]
    public async Task LabelledBreakAndContinue() => Assert.Equal("[ '00', '01', '10', '11', '20', '21' ]", await Run("""
        const found = new Array<string>();
        outer: for (let i = 0; i < 4; i++) {
            for (let j = 0; j < 4; j++) {
                if (j == 2) continue outer;
                if (i == 3) break outer;
                found.push(`${i}${j}`);
            }
        }
        console.log(found);
        """));

    [Fact]
    public async Task OutAndRefParametersAndArguments() => Assert.Equal("2 1 43 3 2", await Run("""
        function swap(ref a: int, ref b: int): void { const t = a; a = b; b = t; }
        function divide(x: int, y: int, out rem: int): int { rem = x % y; return x / y; }
        let a = 1;
        let b = 2;
        swap(ref a, ref b);
        let r = 0;
        const q = divide(17, 5, out r);
        if (int.TryParse("42", out let parsed)) console.log(a, b, parsed + 1, q, r);
        """));

    [Fact]
    public async Task ClassUnionsNarrowWithInstanceof() => Assert.Equal("[ '3.14', '4.00', '0.00' ]", await Run("""
        class Circle { constructor(public radius: number) {} }
        class Square { constructor(public side: number) {} }
        type Shape = Circle | Square;
        function area(shape: Shape): number {
            if (shape instanceof Circle) return Math.PI * shape.radius ** 2;
            if (shape instanceof Square && shape.side > 0) return shape.side * shape.side;
            return 0;
        }
        const shapes: Shape[] = [new Circle(1), new Square(2), new Square(0)];
        console.log(shapes.map(x => area(x).toFixed(2)));
        """));

    [Fact]
    public async Task FieldsCanBePassedByRef() => Assert.Equal("2 1 5", await Run("""
        import System.Threading;
        class Counter {
            hits: int = 0;
            private misses: int = 0;
            hit(): int { Interlocked.Increment(ref this.hits); return Interlocked.Increment(ref this.hits); }
            miss(): int { return Interlocked.Increment(ref this.misses); }
            parse(s: string): int { int.TryParse(s, out this.hits); return this.hits; }
        }
        const c = new Counter();
        console.log(c.hit(), c.miss(), c.parse("5"));
        """));
}
