using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Glitchr.Store
{
    /// <summary>
    /// A small JSON reader and writer: objects as <see cref="Dictionary{TKey,TValue}"/>, arrays as
    /// <see cref="List{T}"/>, numbers as <see cref="double"/> - what Unity's JsonUtility cannot do
    /// (a manifest's dependencies, a registry's documents, keyed by name).
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            var reader = new Reader(text ?? string.Empty);
            object value = reader.Value();
            reader.Space();
            if (!reader.End) throw new FormatException("Unexpected text after the JSON value at " + reader.Position + ".");

            return value;
        }

        public static string Write(object value, bool pretty = false)
        {
            var builder = new StringBuilder();
            WriteValue(builder, value, pretty, 0);

            return builder.ToString();
        }

        public static Dictionary<string, object> Object(object value) => value as Dictionary<string, object>;
        public static List<object> Array(object value) => value as List<object>;
        public static string String(object value) => value as string;

        /// <summary>The value at a path of keys, or null.</summary>
        public static object Get(object value, params string[] keys)
        {
            foreach (string key in keys)
            {
                if (!(value is Dictionary<string, object> map) || !map.TryGetValue(key, out value)) return null;
            }

            return value;
        }

        private sealed class Reader
        {
            private readonly string text;
            public int Position;

            public Reader(string text) { this.text = text; }

            public bool End => Position >= text.Length;

            public void Space()
            {
                while (!End && char.IsWhiteSpace(text[Position])) Position++;
            }

            public object Value()
            {
                Space();
                if (End) throw new FormatException("Unexpected end of JSON.");
                char c = text[Position];
                switch (c)
                {
                    case '{': return ObjectValue();
                    case '[': return ArrayValue();
                    case '"': return StringValue();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default: return NumberValue();
                }
            }

            private void Expect(string word)
            {
                if (string.CompareOrdinal(text, Position, word, 0, word.Length) != 0) throw new FormatException("Expected " + word + " at " + Position + ".");
                Position += word.Length;
            }

            private Dictionary<string, object> ObjectValue()
            {
                var map = new Dictionary<string, object>();
                Position++;
                Space();
                if (!End && text[Position] == '}') { Position++; return map; }
                while (true)
                {
                    Space();
                    string key = StringValue();
                    Space();
                    if (End || text[Position] != ':') throw new FormatException("Expected : at " + Position + ".");
                    Position++;
                    map[key] = Value();
                    Space();
                    if (End) throw new FormatException("Unclosed object.");
                    if (text[Position] == ',') { Position++; continue; }
                    if (text[Position] == '}') { Position++; return map; }
                    throw new FormatException("Expected , or } at " + Position + ".");
                }
            }

            private List<object> ArrayValue()
            {
                var list = new List<object>();
                Position++;
                Space();
                if (!End && text[Position] == ']') { Position++; return list; }
                while (true)
                {
                    list.Add(Value());
                    Space();
                    if (End) throw new FormatException("Unclosed array.");
                    if (text[Position] == ',') { Position++; continue; }
                    if (text[Position] == ']') { Position++; return list; }
                    throw new FormatException("Expected , or ] at " + Position + ".");
                }
            }

            private string StringValue()
            {
                if (End || text[Position] != '"') throw new FormatException("Expected a string at " + Position + ".");
                Position++;
                var builder = new StringBuilder();
                while (!End)
                {
                    char c = text[Position++];
                    if (c == '"') return builder.ToString();
                    if (c != '\\') { builder.Append(c); continue; }
                    if (End) break;
                    char e = text[Position++];
                    switch (e)
                    {
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'u':
                            builder.Append((char)Convert.ToInt32(text.Substring(Position, 4), 16));
                            Position += 4;
                            break;
                        default: builder.Append(e); break;
                    }
                }

                throw new FormatException("Unclosed string.");
            }

            private object NumberValue()
            {
                int start = Position;
                while (!End && "+-0123456789.eE".IndexOf(text[Position]) >= 0) Position++;
                if (start == Position) throw new FormatException("Unexpected character '" + text[Position] + "' at " + Position + ".");

                return double.Parse(text.Substring(start, Position - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }

        private static void WriteValue(StringBuilder b, object value, bool pretty, int depth)
        {
            switch (value)
            {
                case null: b.Append("null"); break;
                case string s: WriteString(b, s); break;
                case bool flag: b.Append(flag ? "true" : "false"); break;
                case double d: b.Append(d.ToString("R", CultureInfo.InvariantCulture)); break;
                case float f: b.Append(f.ToString("R", CultureInfo.InvariantCulture)); break;
                case int or long or short or byte: b.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
                case IDictionary<string, object> map:
                    b.Append('{');
                    int i = 0;
                    foreach (var pair in map)
                    {
                        if (i++ > 0) b.Append(',');
                        Indent(b, pretty, depth + 1);
                        WriteString(b, pair.Key);
                        b.Append(pretty ? ": " : ":");
                        WriteValue(b, pair.Value, pretty, depth + 1);
                    }
                    if (i > 0) Indent(b, pretty, depth);
                    b.Append('}');
                    break;
                case IEnumerable list:
                    b.Append('[');
                    int j = 0;
                    foreach (object item in list)
                    {
                        if (j++ > 0) b.Append(',');
                        Indent(b, pretty, depth + 1);
                        WriteValue(b, item, pretty, depth + 1);
                    }
                    if (j > 0) Indent(b, pretty, depth);
                    b.Append(']');
                    break;
                default: WriteString(b, value.ToString()); break;
            }
        }

        private static void Indent(StringBuilder b, bool pretty, int depth)
        {
            if (!pretty) return;
            b.Append('\n').Append(' ', depth * 2);
        }

        private static void WriteString(StringBuilder b, string s)
        {
            b.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4"));
                        else b.Append(c);
                        break;
                }
            }
            b.Append('"');
        }
    }
}
