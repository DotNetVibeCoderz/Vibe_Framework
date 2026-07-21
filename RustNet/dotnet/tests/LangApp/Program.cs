namespace LangApp;

/// <summary>
/// Language feature exercise for runtime v0.4: inheritance, virtual
/// dispatch, interfaces, ToString overrides, casts, user generics and
/// exception filters. Runs without any device APIs so it also executes
/// under the permissive run_rnx host.
/// </summary>
public interface IShape
{
    double Area();
    string Describe();
}

public class Shape
{
    public virtual string Name()
    {
        return "shape";
    }

    public override string ToString()
    {
        return string.Concat("<", Name(), ">");
    }
}

public sealed class TagAttribute : Attribute
{
    public string Label;
    public int Rank { get; set; }

    public TagAttribute(string label)
    {
        Label = label;
    }
}

[Tag("shape", Rank = 7)]
public class Circle : Shape, IShape
{
    public double Radius;

    public string Color { get; set; }

    public Circle(double r)
    {
        Radius = r;
    }

    public override string Name()
    {
        return "circle";
    }

    public double Area()
    {
        return 3.0 * Radius * Radius;
    }

    public void Scale(double factor)
    {
        Radius = Radius * factor;
    }

    public string Describe()
    {
        return $"circle r={Radius}";
    }
}

public class Square : Shape, IShape
{
    public double Side;

    public Square(double s)
    {
        Side = s;
    }

    public override string Name()
    {
        return "square";
    }

    public double Area()
    {
        return Side * Side;
    }

    public string Describe()
    {
        return $"square s={Side}";
    }
}

public class BaseVals
{
    public int A;

    public BaseVals()
    {
        A = 10;
    }
}

public class Derived : BaseVals
{
    public int B;

    public Derived()
    {
        B = 32;
    }

    public int Sum()
    {
        return A + B;
    }
}

/// <summary>User-defined generic (erased at compile time).</summary>
public class Box<T>
{
    private T _value;

    public Box(T v)
    {
        _value = v;
    }

    public T Get()
    {
        return _value;
    }

    public void Set(T v)
    {
        _value = v;
    }
}

public static class Program
{
    public static void Main()
    {
        Console.WriteLine("LangApp starting");

        // Virtual dispatch through a base-typed array + ToString override.
        Shape[] shapes = new Shape[2];
        shapes[0] = new Circle(2.0);
        shapes[1] = new Square(3.0);
        for (int i = 0; i < shapes.Length; i++)
        {
            Console.WriteLine(string.Concat("name=", shapes[i].Name(), " str=", shapes[i].ToString()));
        }

        // Reflection: object.GetType() + Type members (v0.9).
        var ct = shapes[0].GetType();
        Console.WriteLine(string.Concat("reflect name=", ct.Name, " base=", ct.BaseType.Name));
        Console.WriteLine(string.Concat("reflect str=", "x".GetType().Name));
        // Member enumeration: GetMethods / GetMethod / MethodInfo.Name.
        var found = ct.GetMethod("Area");
        string area = found == null ? "null" : found.Name;
        bool hasMethods = ct.GetMethods().Length > 0;
        Console.WriteLine($"reflect method={ct.GetMethod("Name").Name} area={area} has={hasMethods}");
        // typeof(T): ldtoken + Type.GetTypeFromHandle; identity matches GetType().
        Type tc = typeof(Circle);
        Console.WriteLine(string.Concat("typeof name=", tc.Name, " ns=", tc.Namespace));
        string same = tc == shapes[0].GetType() ? "yes" : "no";
        Console.WriteLine(string.Concat("typeof same=", same, " int=", typeof(int).Name));
        // MethodInfo.Invoke: non-void no-arg (Area), then void + boxed arg (Scale).
        var areaM = tc.GetMethod("Area");
        var scaleM = tc.GetMethod("Scale");
        Circle rc = new Circle(2.0);
        object a0 = areaM.Invoke(rc, null);          // 3*2*2 = 12
        scaleM.Invoke(rc, new object[] { 2.0 });     // Radius -> 4
        object a1 = areaM.Invoke(rc, null);          // 3*4*4 = 48
        Console.WriteLine($"invoke area={a0} scaled={a1}");
        // Type.GetFields + FieldInfo.Name/GetValue/SetValue (rc.Radius == 4 now).
        var radiusF = tc.GetField("Radius");
        object rv = radiusF.GetValue(rc);            // 4
        radiusF.SetValue(rc, 5.0);                   // Radius -> 5
        int fieldCount = tc.GetFields().Length;      // public: Radius
        Console.WriteLine($"field name={radiusF.Name} val={rv} now={rc.Radius} count={fieldCount}");
        // Type.GetProperties + PropertyInfo.Name/GetValue/SetValue.
        var colorP = tc.GetProperty("Color");
        colorP.SetValue(rc, "red");
        object cv = colorP.GetValue(rc);
        int propCount = tc.GetProperties().Length;   // Color
        Console.WriteLine($"prop name={colorP.Name} val={cv} count={propCount}");
        // Custom attributes: [Tag("shape", Rank = 7)] on Circle.
        object[] attrs = tc.GetCustomAttributes(false);
        TagAttribute tag = (TagAttribute)attrs[0];
        Console.WriteLine($"attr count={attrs.Length} label={tag.Label} rank={tag.Rank}");

        // Interface dispatch.
        IShape[] ishapes = new IShape[2];
        ishapes[0] = new Circle(1.0);
        ishapes[1] = new Square(2.0);
        double total = 0;
        for (int i = 0; i < ishapes.Length; i++)
        {
            total = total + ishapes[i].Area();
        }
        Console.WriteLine($"total={total}");
        Console.WriteLine(ishapes[1].Describe());

        // isinst / castclass along the chain.
        Shape sh = shapes[0];
        if (sh is Circle)
        {
            Console.WriteLine("is-circle");
        }
        Circle c = (Circle)sh;
        Console.WriteLine($"radius={c.Radius}");
        object o = shapes[1];
        Circle maybe = o as Circle;
        Console.WriteLine(maybe == null ? "as-miss" : "as-bug");
        if (shapes[1] is IShape)
        {
            Console.WriteLine("iface-isinst");
        }

        // Inherited fields + implicit base ctor.
        Console.WriteLine($"sum={new Derived().Sum()}");

        // User generics (erased instantiation).
        Box<int> bi = new Box<int>(41);
        bi.Set(bi.Get() + 1);
        Box<string> bs = new Box<string>("gen");
        Console.WriteLine(string.Concat("box=", bi.Get().ToString(), ",", bs.Get()));

        // Exception filters: first rejects, second matches on the message.
        try
        {
            Fail();
        }
        catch (Exception ex) when (ex.Message == "other")
        {
            Console.WriteLine("wrong-filter");
        }
        catch (Exception ex) when (ex.Message == "code2")
        {
            Console.WriteLine("filtered-2");
        }
        catch
        {
            Console.WriteLine("fallback");
        }

        // string.Concat with 5 parts -> inline-array ReadOnlySpan lowering.
        string many = string.Concat(shapes[0].Name(), "|", shapes[1].Name(), "|", "end");
        Console.WriteLine($"concat5={many}");

        // async/await over the green-thread scheduler.
        Task flow = Flow();
        flow.Wait();

        Console.WriteLine("LangApp finished");
    }

    private static void Fail()
    {
        throw new Exception("code2");
    }

    private static async Task Flow()
    {
        int a = await Compute(21);
        Console.WriteLine($"async a={a}");
        await Task.Delay(10);
        int b = await Compute(a) + await Task.FromResult(2);
        Console.WriteLine($"async b={b}");
        try
        {
            await FailAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine(string.Concat("async caught=", ex.Message));
        }
        Console.WriteLine("async done");
    }

    private static async Task<int> Compute(int x)
    {
        await Task.Delay(20);
        return x * 2;
    }

    private static async Task FailAsync()
    {
        await Task.Delay(5);
        throw new Exception("async-boom");
    }
}
