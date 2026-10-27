using JSNet.Scripting;

namespace JSNet.Tests;

/// <summary>Compiles JS.Net with the in-memory Roslyn backend, runs it and checks the console output.</summary>
public class EndToEndTests
{
    private static async Task<string> Run(string code)
    {
        var result = await JsNetScript.RunAsync(code, "e2e.jsn", new ScriptOptions { CaptureOutput = true });
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + result.Exception + "\n--- C# ---\n" +
            string.Join("\n", result.GeneratedFiles.Select(f => f.Text)));
        return result.Output.Replace("\r\n", "\n").TrimEnd();
    }

    [Theory]
    [InlineData("console.log(1 + 2);", "3")]
    [InlineData("console.log(`a${1 + 1}b`);", "a2b")]
    [InlineData("console.log(`${true}`);", "true")]
    [InlineData("console.log([1, 2, 3].map(x => x * 2));", "[ 2, 4, 6 ]")]
    [InlineData("console.log([1, 2, 3, 4].filter(x => x % 2 == 0).reduce((a, b) => a + b, 0));", "6")]
    [InlineData("console.log({ a: 1, b: 'x' });", "{ a: 1, b: 'x' }")]
    [InlineData("console.log(typeof 1, typeof 'a', typeof true, typeof null);", "number string boolean object")]
    [InlineData("console.log(2 ** 10);", "1024")]
    [InlineData("console.log(Math.max(3, 9, 4), Math.floor(2.7), Math.round(2.5));", "9 2 3")]
    [InlineData("console.log('abc'.toUpperCase().length, 'a-b-c'.split('-'));", "3 [ 'a', 'b', 'c' ]")]
    [InlineData("console.log('' || 'fallback', null ?? 'nn', 0 || 5);", "fallback nn 5")]
    [InlineData("let s = ''; if (!s) { console.log('empty is falsy'); }", "empty is falsy")]
    [InlineData("console.log(JSON.stringify({ x: 1, y: [true, null] }));", "{\"x\":1,\"y\":[true,null]}")]
    [InlineData("console.log(parseInt('42px') + 1, parseFloat('3.5') * 2);", "43 7")]
    [InlineData("const [a, b] = [1, 2]; console.log(a, b);", "1 2")]
    [InlineData("const { x, y } = { x: 1, y: 2 }; console.log(x + y);", "3")]
    [InlineData("let a = 1; let b = 2; [a, b] = [b, a]; console.log(a, b);", "2 1")]
    [InlineData("let x = 0; let y = 1; let z = false; console.log((x != 0 || y != 0) && z, x != 0 || (y != 0 && z));", "false false")]
    [InlineData("const t = true; if ((t || false) && !t) { console.log('wrong'); } else { console.log('right'); }", "right")]
    public async Task Expressions(string code, string expected) => Assert.Equal(expected, await Run(code));

    [Fact]
    public async Task ClassesInheritanceAndOverrides()
    {
        string output = await Run("""
            class Animal {
                constructor(public name: string) {}
                speak(): string { return `${this.name} makes a sound`; }
            }
            class Dog extends Animal {
                constructor(name: string) { super(name); }
                speak(): string { return `${this.name} barks`; }
            }
            const pets: Animal[] = [new Animal("Cat"), new Dog("Rex")];
            for (const p of pets) console.log(p.speak());
            console.log(pets[1] instanceof Dog);
            """);
        Assert.Equal("Cat makes a sound\nRex barks\ntrue", output);
    }

    [Fact]
    public async Task GettersSettersAndStatics()
    {
        string output = await Run("""
            class Temperature {
                static count = 0;
                private celsius = 0;
                constructor() { Temperature.count++; }
                get fahrenheit(): number { return this.celsius * 9 / 5 + 32; }
                set fahrenheit(f: number) { this.celsius = (f - 32) * 5 / 9; }
            }
            const t = new Temperature();
            t.fahrenheit = 212;
            console.log(t.fahrenheit, Temperature.count);
            """);
        Assert.Equal("212 1", output);
    }

    [Fact]
    public async Task AsyncAwaitAndPromises()
    {
        string output = await Run("""
            async function square(x: int): Promise<int> {
                await delay(5);
                return x * x;
            }
            const results = await Promise.all([square(2), square(3)]);
            console.log(results);
            const p = new Promise<string>(resolve => setTimeout(() => resolve("done"), 5));
            console.log(await p);
            """);
        Assert.Equal("[ 4, 9 ]\ndone", output);
    }

    [Fact]
    public async Task GeneratorsAndLinq()
    {
        string output = await Run("""
            function* fib(n: int): Iterable<int> {
                let a = 0, b = 1;
                for (let i = 0; i < n; i++) {
                    yield a;
                    [a, b] = [b, a + b];
                }
            }
            console.log(fib(10).where(x => x % 2 == 0).select(x => x * 10).toList());
            """);
        Assert.Equal("[ 0, 20, 80, 340 ]", output);
    }

    [Fact]
    public async Task InterfacesAndObjectLiterals()
    {
        string output = await Run("""
            interface Point { x: number; y: number; }
            function len(p: Point): number { return Math.sqrt(p.x * p.x + p.y * p.y); }
            const p: Point = { x: 3, y: 4 };
            console.log(len(p));
            """);
        Assert.Equal("5", output);
    }

    [Fact]
    public async Task SwitchWithFallthroughAndEnums()
    {
        string output = await Run("""
            enum Level { Low, Mid, High }
            function describe(l: Level): string {
                switch (l) {
                    case Level.Low: return "low";
                    case Level.Mid:
                    case Level.High: return "mid-or-high";
                }
                return "?";
            }
            console.log(describe(Level.Low), describe(Level.High));
            let score = 75;
            switch (true) {
                case score >= 90: console.log("A"); break;
                case score >= 70: console.log("B"); break;
                default: console.log("C");
            }
            """);
        Assert.Equal("low mid-or-high\nB", output);
    }

    [Fact]
    public async Task ExceptionsAndErrors()
    {
        string output = await Run("""
            function risky(n: int): int {
                if (n < 0) throw new RangeError("negative");
                return n;
            }
            try { risky(-1); } catch (e) { console.log(e.name, e.message); } finally { console.log("finally"); }
            try { throw "plain string"; } catch (e) { console.log(e.message); }
            """);
        Assert.Equal("RangeError negative\nfinally\nplain string", output);
    }

    [Fact]
    public async Task ClrInteropAndGenerics()
    {
        string output = await Run("""
            import System.Text;
            class Stack<T> {
                private items = new Array<T>();
                push(item: T): void { this.items.push(item); }
                pop(): T { return this.items.pop(); }
                get size(): int { return this.items.length; }
            }
            const s = new Stack<string>();
            s.push("a"); s.push("b");
            const sb = new StringBuilder();
            sb.Append(s.pop()).Append(s.size);
            console.log(sb.ToString(), String.Join("|", ["x", "y"]));
            const dict = new Map<string, int>();
            dict.set("one", 1);
            for (const [k, v] of dict) console.log(k, v);
            """);
        Assert.Equal("b1 x|y\none 1", output);
    }

    [Fact]
    public async Task ClosuresCaptureTopLevelVariables()
    {
        string output = await Run("""
            let counter = 0;
            function increment() { counter++; }
            increment(); increment();
            const add = (a: number) => (b: number) => a + b;
            console.log(counter, add(2)(3));
            """);
        Assert.Equal("2 5", output);
    }

    [Fact]
    public async Task CompileErrorsAreReportedAgainstJsnFile()
    {
        var result = await JsNetScript.RunAsync("let x: number = 'text';", "bad.jsn");
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.StartsWith("bad.jsn(1,", StringComparison.Ordinal) && d.Contains("CS0029"));
    }
}
