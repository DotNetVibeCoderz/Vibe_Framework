using System.Globalization;
using System.Text;

namespace Rustzor.Interop;

public enum JsonKind
{
    Null,
    Bool,
    Number,
    String,
    Array,
    Object,
}

/// <summary>
/// A parsed JSON value. Small on purpose: System.Text.Json is a framework
/// assembly the browser runtime does not carry, and everything crossing the
/// bridge is JSON, so this is on the hot path of every browser call.
/// </summary>
public sealed class JsonValue
{
    public static readonly JsonValue Null = new(JsonKind.Null);
    public static readonly JsonValue True = new(JsonKind.Bool) { _bool = true };
    public static readonly JsonValue False = new(JsonKind.Bool) { _bool = false };

    public JsonKind Kind { get; }
    private bool _bool;
    private double _number;
    private string? _string;
    private List<JsonValue>? _items;
    private Dictionary<string, JsonValue>? _props;
    private List<string>? _order;

    private JsonValue(JsonKind kind) => Kind = kind;

    public static JsonValue From(string? s) => s is null ? Null : new JsonValue(JsonKind.String) { _string = s };
    public static JsonValue From(double d) => new(JsonKind.Number) { _number = d };
    public static JsonValue From(bool b) => b ? True : False;
    public static JsonValue NewArray() => new(JsonKind.Array) { _items = new List<JsonValue>() };
    public static JsonValue NewObject() => new(JsonKind.Object) { _props = new Dictionary<string, JsonValue>(), _order = new List<string>() };

    public bool IsNull => Kind == JsonKind.Null;
    public bool IsObject => Kind == JsonKind.Object;
    public bool IsArray => Kind == JsonKind.Array;
    public bool IsString => Kind == JsonKind.String;
    public bool IsNumber => Kind == JsonKind.Number;
    public bool IsBool => Kind == JsonKind.Bool;

    public int Count => _items?.Count ?? _props?.Count ?? 0;

    public JsonValue this[int index] => _items is not null && index >= 0 && index < _items.Count ? _items[index] : Null;

    public JsonValue this[string name]
    {
        get
        {
            if (_props is not null && _props.TryGetValue(name, out var v)) return v;
            return Null;
        }
        set
        {
            if (_props is null) throw new InvalidOperationException("not a JSON object");
            if (!_props.ContainsKey(name)) _order!.Add(name);
            _props[name] = value;
        }
    }

    public bool Has(string name) => _props is not null && _props.ContainsKey(name);

    public IEnumerable<string> Keys => _order ?? (IEnumerable<string>)Array.Empty<string>();

    public IEnumerable<JsonValue> Items => _items ?? (IEnumerable<JsonValue>)Array.Empty<JsonValue>();

    public void Add(JsonValue value)
    {
        if (_items is null) throw new InvalidOperationException("not a JSON array");
        _items.Add(value);
    }

    public string AsString() => Kind switch
    {
        JsonKind.String => _string!,
        JsonKind.Null => "",
        JsonKind.Bool => _bool ? "true" : "false",
        JsonKind.Number => FormatNumber(_number),
        _ => ToJson(),
    };

    public string? AsStringOrNull() => Kind == JsonKind.Null ? null : AsString();

    public double AsDouble() => Kind switch
    {
        JsonKind.Number => _number,
        JsonKind.Bool => _bool ? 1 : 0,
        JsonKind.String => double.TryParse(_string, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0,
        _ => 0,
    };

    public int AsInt() => (int)AsDouble();
    public long AsLong() => (long)AsDouble();

    public bool AsBool() => Kind switch
    {
        JsonKind.Bool => _bool,
        JsonKind.Number => _number != 0,
        JsonKind.String => _string == "true",
        _ => false,
    };

    public override string ToString() => AsString();

    public string ToJson()
    {
        var w = new JsonWriter();
        w.Value(this);
        return w.ToString();
    }

    internal static string FormatNumber(double d)
    {
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString();
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    // ── parsing ───────────────────────────────────────────────────────────

    public static JsonValue Parse(string text)
    {
        if (string.IsNullOrEmpty(text)) return Null;
        var p = new Parser(text);
        var v = p.ParseValue();
        return v;
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s) => _s = s;

        private void SkipWs()
        {
            while (_i < _s.Length)
            {
                var c = _s[_i];
                if (c == ' ' || c == '\n' || c == '\r' || c == '\t') _i++;
                else break;
            }
        }

        public JsonValue ParseValue()
        {
            SkipWs();
            if (_i >= _s.Length) return Null;
            var c = _s[_i];
            switch (c)
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"': return From(ParseString());
                case 't': _i += 4; return True;
                case 'f': _i += 5; return False;
                case 'n': _i += 4; return Null;
                default: return ParseNumber();
            }
        }

        private JsonValue ParseObject()
        {
            var obj = NewObject();
            _i++;
            SkipWs();
            if (_i < _s.Length && _s[_i] == '}') { _i++; return obj; }
            while (_i < _s.Length)
            {
                SkipWs();
                var key = ParseString();
                SkipWs();
                _i++; // ':'
                obj[key] = ParseValue();
                SkipWs();
                if (_i < _s.Length && _s[_i] == ',') { _i++; continue; }
                _i++; // '}'
                break;
            }
            return obj;
        }

        private JsonValue ParseArray()
        {
            var arr = NewArray();
            _i++;
            SkipWs();
            if (_i < _s.Length && _s[_i] == ']') { _i++; return arr; }
            while (_i < _s.Length)
            {
                arr.Add(ParseValue());
                SkipWs();
                if (_i < _s.Length && _s[_i] == ',') { _i++; continue; }
                _i++; // ']'
                break;
            }
            return arr;
        }

        private string ParseString()
        {
            _i++; // opening quote
            var start = _i;
            // Fast path: no escapes.
            while (_i < _s.Length && _s[_i] != '"' && _s[_i] != '\\') _i++;
            if (_i < _s.Length && _s[_i] == '"')
            {
                var plain = _s.Substring(start, _i - start);
                _i++;
                return plain;
            }
            var sb = new StringBuilder();
            sb.Append(_s, start, _i - start);
            while (_i < _s.Length)
            {
                var c = _s[_i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                var e = _s[_i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)Convert.ToInt32(_s.Substring(_i, 4), 16));
                        _i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            return sb.ToString();
        }

        private JsonValue ParseNumber()
        {
            var start = _i;
            while (_i < _s.Length)
            {
                var c = _s[_i];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') _i++;
                else break;
            }
            var text = _s.Substring(start, _i - start);
            return From(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>Builds JSON text without allocating an intermediate tree.</summary>
public sealed class JsonWriter
{
    private readonly StringBuilder _sb = new();
    // One flag per open container: true once it has a first element.
    private readonly List<bool> _stack = new();
    private bool _afterName;

    private void Separator()
    {
        if (_afterName) { _afterName = false; return; }
        var n = _stack.Count;
        if (n == 0) return;
        if (_stack[n - 1]) _sb.Append(',');
        else _stack[n - 1] = true;
    }

    public JsonWriter BeginObject() { Separator(); _sb.Append('{'); _stack.Add(false); return this; }
    public JsonWriter EndObject() { _sb.Append('}'); _stack.RemoveAt(_stack.Count - 1); return this; }
    public JsonWriter BeginArray() { Separator(); _sb.Append('['); _stack.Add(false); return this; }
    public JsonWriter EndArray() { _sb.Append(']'); _stack.RemoveAt(_stack.Count - 1); return this; }

    public JsonWriter Name(string name)
    {
        Separator();
        WriteString(name);
        _sb.Append(':');
        _afterName = true;
        return this;
    }

    public JsonWriter String(string? s) { Separator(); if (s is null) _sb.Append("null"); else WriteString(s); return this; }
    public JsonWriter Number(double d) { Separator(); _sb.Append(JsonValue.FormatNumber(d)); return this; }
    public JsonWriter Number(int i) { Separator(); _sb.Append(i); return this; }
    public JsonWriter Number(long l) { Separator(); _sb.Append(l); return this; }
    public JsonWriter Bool(bool b) { Separator(); _sb.Append(b ? "true" : "false"); return this; }
    public JsonWriter Null() { Separator(); _sb.Append("null"); return this; }
    public JsonWriter Raw(string json) { Separator(); _sb.Append(json); return this; }

    public JsonWriter Prop(string name, string? value) => Name(name).String(value);
    public JsonWriter Prop(string name, int value) => Name(name).Number(value);
    public JsonWriter Prop(string name, long value) => Name(name).Number(value);
    public JsonWriter Prop(string name, double value) => Name(name).Number(value);
    public JsonWriter Prop(string name, bool value) => Name(name).Bool(value);

    public JsonWriter Value(JsonValue v)
    {
        switch (v.Kind)
        {
            case JsonKind.Null: return Null();
            case JsonKind.Bool: return Bool(v.AsBool());
            case JsonKind.Number: return Number(v.AsDouble());
            case JsonKind.String: return String(v.AsString());
            case JsonKind.Array:
                BeginArray();
                foreach (var item in v.Items) Value(item);
                return EndArray();
            default:
                BeginObject();
                foreach (var key in v.Keys) { Name(key); Value(v[key]); }
                return EndObject();
        }
    }

    /// <summary>Writes any value: primitives, strings, JsonValue, lists, dictionaries and plain objects.</summary>
    public JsonWriter Any(object? value)
    {
        Json.WriteObject(this, value);
        return this;
    }

    // Every character JSON must escape: the controls, the quote and the backslash.
    private static readonly char[] Special = BuildSpecial();

    private static char[] BuildSpecial()
    {
        var chars = new char[34];
        for (var i = 0; i < 32; i++) chars[i] = (char)i;
        chars[32] = '"';
        chars[33] = '\\';
        return chars;
    }

    private void WriteString(string s)
    {
        _sb.Append('"');
        // Almost every string (attribute values, ids, text) needs no escaping:
        // one search instead of a test per character.
        if (s.IndexOfAny(Special) < 0)
        {
            _sb.Append(s).Append('"');
            return;
        }
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            string? esc = c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => null,
            };
            if (esc is null && c < ' ') esc = "\\u" + ((int)c).ToString("x4");
            if (esc is null) continue;
            if (i > start) _sb.Append(s, start, i - start);
            _sb.Append(esc);
            start = i + 1;
        }
        if (start < s.Length) _sb.Append(s, start, s.Length - start);
        _sb.Append('"');
    }

    public override string ToString() => _sb.ToString();
}
