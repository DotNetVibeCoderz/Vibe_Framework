namespace {{Namespace}};

public sealed record Question(string Text, string[] Choices, int Answer, string Why);

public sealed record Topic(string Id, string Title, string Blurb, string Icon, Question[] Questions);

/// <summary>The question banks. Add a topic by appending to <see cref="Topics"/>.</summary>
public static class QuestionBank
{
    public static readonly Topic[] Topics =
    {
        new("geo", "World capitals", "Ten capitals, from the obvious to the often-mistaken.", "map", new Question[]
        {
            new("What is the capital of Australia?", new[] { "Sydney", "Canberra", "Melbourne", "Perth" }, 1, "Canberra was purpose-built as a compromise between Sydney and Melbourne."),
            new("What is the capital of Canada?", new[] { "Toronto", "Vancouver", "Ottawa", "Montreal" }, 2, "Ottawa, on the border of Ontario and Quebec."),
            new("What is the capital of Indonesia (until the move to Nusantara)?", new[] { "Surabaya", "Bandung", "Jakarta", "Yogyakarta" }, 2, "Jakarta; the government is moving to Nusantara in East Kalimantan."),
            new("What is the capital of Brazil?", new[] { "Rio de Janeiro", "São Paulo", "Brasília", "Salvador" }, 2, "Brasília replaced Rio de Janeiro in 1960."),
            new("What is the capital of Turkey?", new[] { "Istanbul", "Ankara", "Izmir", "Bursa" }, 1, "Ankara has been the capital since 1923."),
            new("What is the capital of Switzerland?", new[] { "Zurich", "Geneva", "Bern", "Basel" }, 2, "Bern is the 'federal city'; Switzerland has no de jure capital."),
            new("What is the capital of Nigeria?", new[] { "Lagos", "Abuja", "Kano", "Ibadan" }, 1, "Abuja replaced Lagos in 1991."),
            new("What is the capital of New Zealand?", new[] { "Auckland", "Christchurch", "Wellington", "Hamilton" }, 2, "Wellington, at the southern tip of the North Island."),
            new("What is the capital of Morocco?", new[] { "Casablanca", "Marrakesh", "Fez", "Rabat" }, 3, "Rabat; Casablanca is the largest city."),
            new("What is the capital of Vietnam?", new[] { "Ho Chi Minh City", "Hanoi", "Da Nang", "Hue" }, 1, "Hanoi; Hue was the imperial capital until 1945."),
        }),
        new("sci", "Everyday science", "Physics and chemistry you can see around the house.", "zap", new Question[]
        {
            new("Why does ice float on water?", new[] { "It contains air", "It is less dense than liquid water", "Surface tension holds it up", "It is colder" }, 1, "Hydrogen bonds hold ice in an open lattice, about 9% less dense than liquid water."),
            new("What is the boiling point of water at sea level, in kelvin?", new[] { "273.15 K", "100 K", "373.15 K", "212 K" }, 2, "100 °C is 373.15 K."),
            new("Which gas do plants take in for photosynthesis?", new[] { "Oxygen", "Nitrogen", "Carbon dioxide", "Hydrogen" }, 2, "CO₂ and water become glucose and oxygen, powered by light."),
            new("A 1000 W kettle runs for 3 minutes. How much energy does it use?", new[] { "3 kJ", "180 kJ", "3 MJ", "500 Wh" }, 1, "1000 W × 180 s = 180,000 J = 180 kJ, which is 50 Wh."),
            new("Why is the sky blue?", new[] { "It reflects the ocean", "Shorter wavelengths scatter more in air", "Ozone is blue", "Sunlight is blue" }, 1, "Rayleigh scattering goes as 1/λ⁴, so blue scatters far more than red."),
            new("What does pH 7 mean?", new[] { "Acidic", "Neutral", "Basic", "Saturated" }, 1, "At 25 °C pure water has pH 7: equal H⁺ and OH⁻."),
            new("Which metal is liquid at room temperature?", new[] { "Mercury", "Gallium", "Sodium", "Tin" }, 0, "Mercury melts at −38.8 °C; gallium melts at 29.8 °C, just above room temperature."),
            new("Roughly how long does sunlight take to reach Earth?", new[] { "8 seconds", "8 minutes", "8 hours", "1 second" }, 1, "About 499 seconds: 150 million km at 300,000 km/s."),
        }),
        new("cs", "C# and .NET", "For people building with Rustzor.", "cpu", new Question[]
        {
            new("Which keyword declares a value type?", new[] { "class", "struct", "record", "interface" }, 1, "struct; `record struct` is also a value type, plain `record` is a class."),
            new("What does `await` do?", new[] { "Blocks the thread", "Starts a new thread", "Suspends the method until the task completes", "Cancels a task" }, 2, "The method returns to its caller and resumes when the awaited task finishes."),
            new("What does `x ?? y` return?", new[] { "x if x is not null, otherwise y", "y if x is true", "The larger of x and y", "Always y" }, 0, "The null-coalescing operator."),
            new("Which LINQ method keeps elements that match a predicate?", new[] { "Select", "Where", "Any", "First" }, 1, "Where filters; Select projects."),
            new("In Blazor and Rustzor, which attribute marks a component parameter?", new[] { "[Input]", "[Prop]", "[Parameter]", "[Bind]" }, 2, "[Parameter], set by the parent in markup."),
            new("What runs C# in the browser in Rustzor?", new[] { "A JavaScript transpiler", "RustNetRuntime compiled to WebAssembly", "Mono", "A server over SignalR" }, 1, "The IL runs on RustNetRuntime, a Rust runtime compiled to WebAssembly, or is AOT-compiled to WebAssembly."),
        }),
    };
}
