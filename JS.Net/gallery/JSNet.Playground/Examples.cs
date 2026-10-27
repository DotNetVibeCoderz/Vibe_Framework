namespace JSNet.Playground;

public sealed record Example(string Title, string Code);

public static class Examples
{
    public static readonly Example[] All =
    [
        new("Hello, JS.Net", """
            // Everything here compiles and runs in your browser: JS.Net → C# → IL → .NET WebAssembly runtime.
            const languages = ["JS.Net", "C#", "F#"];
            for (const lang of languages) console.log(`Hello from ${lang}!`);
            console.log("squares:", Array.range(8).map(n => n * n));
            """),
        new("Classes & inheritance", """
            class Shape {
                constructor(public readonly name: string) {}
                area(): number { return 0; }
                describe(): string { return `${this.name}: ${this.area().toFixed(2)}`; }
            }
            class Circle extends Shape {
                constructor(private r: number) { super("circle"); }
                area(): number { return Math.PI * this.r ** 2; }
            }
            class Square extends Shape {
                constructor(private side: number) { super("square"); }
                area(): number { return this.side * this.side; }
            }
            const shapes: Shape[] = [new Circle(1.5), new Square(2)];
            shapes.forEach(s => console.log(s.describe()));
            """),
        new("Async & LINQ", """
            async function price(item: string, ms: int): Promise<number> {
                await delay(ms);
                return item.length * 1.25;
            }
            const items = ["keyboard", "mouse", "monitor"];
            const prices = await Promise.all(items.map(i => price(i, 20)));
            console.log(prices);
            const total = items.map((name, i) => ({ name: name, price: prices[i] }))
                .where(p => p.price > 6)
                .orderBy(p => p.name)
                .toList();
            console.table(total);
            """),
        new("Types, spread & regex", """
            type Point = { x: number; y: number };
            const origin: Point = { x: 0, y: 0 };
            const moved = { ...origin, y: 5 };
            console.log(moved.x, moved.y);
            const emails = "ada@gravicode.dev, linus@example.org".match(/[\w.]+@[\w.]+/g);
            console.log(emails);
            class Cat { meow(): string { return "meow"; } }
            class Dog { bark(): string { return "woof"; } }
            type Pet = Cat | Dog;
            const pets: Pet[] = [new Cat(), new Dog()];
            for (const pet of pets) {
                if (pet instanceof Cat) console.log(pet.meow());
                else if (pet instanceof Dog) console.log(pet.bark());
            }
            """),
    ];
}
