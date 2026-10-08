using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SoundCalcs.Domain.Ifc
{
    /// <summary>Reference to another entity: #123.</summary>
    public sealed class StepRef
    {
        public int Id { get; }
        public StepRef(int id) { Id = id; }
        public override string ToString() => "#" + Id;
    }

    /// <summary>Enumeration value: .POSITIVE., .T., .MILLI.</summary>
    public sealed class StepEnum
    {
        public string Name { get; }
        public StepEnum(string name) { Name = name; }
        public override string ToString() => "." + Name + ".";
    }

    /// <summary>Typed value: IFCLABEL('x'), IFCLENGTHMEASURE(0.3).</summary>
    public sealed class StepTyped
    {
        public string Type { get; }
        public object Value { get; }
        public StepTyped(string type, object value) { Type = type; Value = value; }
        public override string ToString() => $"{Type}({Value})";
    }

    /// <summary>One entity instance of the DATA section. Arguments are parsed on first use.</summary>
    public sealed class StepEntity
    {
        private List<object> _args;

        public int Id { get; }
        /// <summary>Upper-case entity type, e.g. IFCWALL.</summary>
        public string Type { get; }
        public string RawArgs { get; }

        public StepEntity(int id, string type, string rawArgs)
        {
            Id = id;
            Type = type;
            RawArgs = rawArgs;
        }

        /// <summary>
        /// Arguments: <see cref="StepRef"/>, string, double, long, <see cref="StepEnum"/>, <see cref="StepTyped"/>,
        /// List&lt;object&gt; for aggregates, null for $ and *.
        /// </summary>
        public List<object> Args => _args ?? (_args = StepArgParser.Parse(RawArgs));

        public object Arg(int i) => i < Args.Count ? Args[i] : null;

        public override string ToString() => $"#{Id}={Type}({RawArgs})";
    }

    /// <summary>
    /// Minimal ISO 10303-21 (STEP physical file, ".ifc") reader: the DATA section as entities by id.
    /// Enough to read IFC walls without a full IFC toolkit.
    /// </summary>
    public sealed class StepFile
    {
        public Dictionary<int, StepEntity> Entities { get; } = new Dictionary<int, StepEntity>();

        /// <summary>FILE_SCHEMA, e.g. IFC2X3 or IFC4.</summary>
        public string Schema { get; private set; } = "";

        public StepEntity Get(int id) => Entities.TryGetValue(id, out StepEntity e) ? e : null;
        public StepEntity Get(object reference) => reference is StepRef r ? Get(r.Id) : null;

        public IEnumerable<StepEntity> OfType(params string[] types)
        {
            var set = new HashSet<string>(types, StringComparer.OrdinalIgnoreCase);
            foreach (StepEntity e in Entities.Values)
                if (set.Contains(e.Type)) yield return e;
        }

        public static StepFile Load(string path, Func<string, bool> keepType = null)
        {
            using (var reader = new StreamReader(path, Encoding.UTF8, true, 1 << 16))
                return Parse(reader, keepType);
        }

        /// <summary>
        /// Reads every statement. <paramref name="keepType"/> limits which entity types are kept (memory on
        /// large models); the rest are skipped without parsing their arguments.
        /// </summary>
        public static StepFile Parse(TextReader reader, Func<string, bool> keepType = null)
        {
            var file = new StepFile();
            file.ParseInto(reader, keepType == null ? (Func<int, string, bool>)null : (id, type) => keepType(type));
            return file;
        }

        /// <summary>
        /// Adds the entities of another read of the same file for which <paramref name="keep"/>(id, type) is true:
        /// lets a large file be read in passes, keeping only what the previous pass showed to be needed.
        /// </summary>
        public void ParseInto(TextReader reader, Func<int, string, bool> keep)
        {
            var file = this;
            var sb = new StringBuilder(256);
            bool inString = false, inComment = false;
            int c, prev = -1;
            while ((c = reader.Read()) >= 0)
            {
                char ch = (char)c;
                if (inComment)
                {
                    if (prev == '*' && ch == '/') { inComment = false; prev = -1; continue; }
                    prev = c;
                    continue;
                }
                if (inString)
                {
                    sb.Append(ch);
                    if (ch == '\'') inString = false;   // '' inside a string toggles twice: stays correct
                    prev = c;
                    continue;
                }
                if (ch == '/' && reader.Peek() == '*') { reader.Read(); inComment = true; prev = -1; continue; }
                if (ch == '\'') { inString = true; sb.Append(ch); prev = c; continue; }
                if (ch == ';')
                {
                    file.Statement(sb.ToString(), keep);
                    sb.Clear();
                    prev = c;
                    continue;
                }
                if (ch == '\r' || ch == '\n') { prev = c; continue; }
                sb.Append(ch);
                prev = c;
            }
        }

        private void Statement(string text, Func<int, string, bool> keep)
        {
            string s = text.Trim();
            if (s.Length == 0) return;
            if (s[0] != '#')
            {
                if (s.StartsWith("FILE_SCHEMA", StringComparison.OrdinalIgnoreCase))
                {
                    int q1 = s.IndexOf('\''), q2 = q1 >= 0 ? s.IndexOf('\'', q1 + 1) : -1;
                    if (q2 > q1) Schema = s.Substring(q1 + 1, q2 - q1 - 1).ToUpperInvariant();
                }
                return;
            }

            int eq = s.IndexOf('=');
            if (eq < 0 || !int.TryParse(s.Substring(1, eq - 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                return;
            int open = s.IndexOf('(', eq);
            int close = s.LastIndexOf(')');
            if (open < 0 || close < open) return;
            string type = s.Substring(eq + 1, open - eq - 1).Trim().ToUpperInvariant();
            if (keep != null && !keep(id, type)) return;
            Entities[id] = new StepEntity(id, type, s.Substring(open + 1, close - open - 1));
        }
    }

    internal static class StepArgParser
    {
        public static List<object> Parse(string raw)
        {
            int pos = 0;
            var list = new List<object>();
            ParseListBody(raw, ref pos, list, topLevel: true);
            return list;
        }

        private static void ParseListBody(string s, ref int pos, List<object> list, bool topLevel)
        {
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == ')') { pos++; return; }
            while (pos < s.Length)
            {
                list.Add(ParseValue(s, ref pos));
                SkipWs(s, ref pos);
                if (pos >= s.Length) return;
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ')') { pos++; return; }
                pos++; // tolerate junk
            }
        }

        private static object ParseValue(string s, ref int pos)
        {
            SkipWs(s, ref pos);
            if (pos >= s.Length) return null;
            char c = s[pos];
            if (c == '$' || c == '*') { pos++; return null; }
            if (c == '#')
            {
                int start = ++pos;
                while (pos < s.Length && char.IsDigit(s[pos])) pos++;
                return new StepRef(int.Parse(s.Substring(start, pos - start), CultureInfo.InvariantCulture));
            }
            if (c == '\'') return ParseString(s, ref pos);
            if (c == '.')
            {
                int end = s.IndexOf('.', pos + 1);
                if (end < 0) { pos = s.Length; return null; }
                string name = s.Substring(pos + 1, end - pos - 1);
                pos = end + 1;
                return new StepEnum(name.ToUpperInvariant());
            }
            if (c == '(')
            {
                pos++;
                var list = new List<object>();
                ParseListBody(s, ref pos, list, topLevel: false);
                return list;
            }
            if (c == '"')
            {
                int end = s.IndexOf('"', pos + 1);
                string bin = end < 0 ? s.Substring(pos + 1) : s.Substring(pos + 1, end - pos - 1);
                pos = end < 0 ? s.Length : end + 1;
                return bin;
            }
            if (char.IsLetter(c))
            {
                int start = pos;
                while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_')) pos++;
                string type = s.Substring(start, pos - start).ToUpperInvariant();
                SkipWs(s, ref pos);
                object inner = null;
                if (pos < s.Length && s[pos] == '(')
                {
                    pos++;
                    var args = new List<object>();
                    ParseListBody(s, ref pos, args, topLevel: false);
                    inner = args.Count == 1 ? args[0] : args;
                }
                return new StepTyped(type, inner);
            }

            // Number
            int ns = pos;
            while (pos < s.Length && "+-0123456789.Ee".IndexOf(s[pos]) >= 0) pos++;
            string num = s.Substring(ns, pos - ns);
            if (num.IndexOfAny(new[] { '.', 'E', 'e' }) < 0 &&
                long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                return l;
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            if (pos == ns) pos++; // unknown character: skip
            return null;
        }

        private static string ParseString(string s, ref int pos)
        {
            var sb = new StringBuilder();
            pos++; // opening quote
            while (pos < s.Length)
            {
                char c = s[pos++];
                if (c == '\'')
                {
                    if (pos < s.Length && s[pos] == '\'') { sb.Append('\''); pos++; continue; }
                    break;
                }
                sb.Append(c);
            }
            return DecodeString(sb.ToString());
        }

        /// <summary>ISO 10303-21 string escapes: \X2\…\X0\ (UTF-16 hex), \X\hh (ISO 8859-1), \S\c, \\.</summary>
        internal static string DecodeString(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\') { sb.Append(s[i]); continue; }
                if (string.CompareOrdinal(s, i, "\\X2\\", 0, 4) == 0)
                {
                    int end = s.IndexOf("\\X0\\", i + 4, StringComparison.Ordinal);
                    if (end < 0) break;
                    string hex = s.Substring(i + 4, end - i - 4);
                    for (int k = 0; k + 4 <= hex.Length; k += 4)
                        sb.Append((char)Convert.ToInt32(hex.Substring(k, 4), 16));
                    i = end + 3;
                }
                else if (string.CompareOrdinal(s, i, "\\X4\\", 0, 4) == 0)
                {
                    int end = s.IndexOf("\\X0\\", i + 4, StringComparison.Ordinal);
                    if (end < 0) break;
                    string hex = s.Substring(i + 4, end - i - 4);
                    for (int k = 0; k + 8 <= hex.Length; k += 8)
                        sb.Append(char.ConvertFromUtf32(Convert.ToInt32(hex.Substring(k, 8), 16)));
                    i = end + 3;
                }
                else if (string.CompareOrdinal(s, i, "\\X\\", 0, 3) == 0 && i + 5 <= s.Length)
                {
                    sb.Append((char)Convert.ToInt32(s.Substring(i + 3, 2), 16));
                    i += 4;
                }
                else if (string.CompareOrdinal(s, i, "\\S\\", 0, 3) == 0 && i + 3 < s.Length)
                {
                    sb.Append((char)(s[i + 3] + 128));
                    i += 3;
                }
                else if (i + 1 < s.Length && s[i + 1] == '\\')
                {
                    sb.Append('\\');
                    i++;
                }
                else sb.Append('\\');
            }
            return sb.ToString();
        }

        private static void SkipWs(string s, ref int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        }
    }
}
