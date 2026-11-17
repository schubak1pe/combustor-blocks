using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace CombustorBlocks
{
    // ---------------------------------------------------------------------
    // Project model
    // ---------------------------------------------------------------------

    public sealed class VariableDef
    {
        public string Name = "variable";
        public string Value = "0";
    }

    public sealed class ListDef
    {
        public string Name = "lista";
    }

    public sealed class FunctionDef
    {
        public string Name = "mi_funcion";
        public List<string> Params = new List<string>();
    }

    public sealed class ScriptStack
    {
        public float X;
        public float Y;
        public List<BlockNode> Blocks = new List<BlockNode>();
    }

    /// <summary>
    /// A block instance. Statement blocks live in a stack/body list; reporters live in
    /// <see cref="Inputs"/>. Both use the same type so copy/paste/undo are trivial.
    /// </summary>
    public sealed class BlockNode
    {
        public string Id = NewId();
        public string Type = "";
        public Dictionary<string, string> Fields = new Dictionary<string, string>();
        public Dictionary<string, BlockNode> Inputs = new Dictionary<string, BlockNode>();
        public List<BlockNode> Body = new List<BlockNode>();
        public List<BlockNode> ElseBody = new List<BlockNode>();

        public static string NewId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 10);
        }

        public string Field(string key, string fallback = "")
        {
            if (Fields != null && Fields.TryGetValue(key, out var v) && v != null) return v;
            return fallback;
        }

        public BlockNode Input(string key)
        {
            if (Inputs != null && Inputs.TryGetValue(key, out var v)) return v;
            return null;
        }

        public bool IsReporter
        {
            get
            {
                var spec = BlockSpecs.Get(Type);
                return spec != null && spec.IsReporter;
            }
        }

        public BlockNode Clone()
        {
            var json = JsonConvert.SerializeObject(this);
            return JsonConvert.DeserializeObject<BlockNode>(json);
        }

        /// <summary>Repairs null collections after a JSON round-trip.</summary>
        public void Fix()
        {
            if (Fields == null) Fields = new Dictionary<string, string>();
            if (Inputs == null) Inputs = new Dictionary<string, BlockNode>();
            if (Body == null) Body = new List<BlockNode>();
            if (ElseBody == null) ElseBody = new List<BlockNode>();
            foreach (var b in Inputs.Values) b?.Fix();
            foreach (var b in Body) b?.Fix();
            foreach (var b in ElseBody) b?.Fix();
        }
    }

    public sealed class BlockProject
    {
        public int Version = 1;
        public string Name = "Mi programa";
        public List<VariableDef> Variables = new List<VariableDef>();
        public List<ListDef> Lists = new List<ListDef>();
        public List<FunctionDef> Functions = new List<FunctionDef>();
        public List<ScriptStack> Stacks = new List<ScriptStack>();
        public float ViewX = 40;
        public float ViewY = 30;
        public float Zoom = 1f;

        public static BlockProject NewProject()
        {
            var p = new BlockProject();
            var stack = new ScriptStack { X = 120, Y = 100 };
            stack.Blocks.Add(new BlockNode { Type = "when_start" });
            p.Stacks.Add(stack);
            return p;
        }

        public void Fix()
        {
            if (Variables == null) Variables = new List<VariableDef>();
            if (Lists == null) Lists = new List<ListDef>();
            if (Functions == null) Functions = new List<FunctionDef>();
            if (Stacks == null) Stacks = new List<ScriptStack>();
            foreach (var s in Stacks)
            {
                if (s.Blocks == null) s.Blocks = new List<BlockNode>();
                foreach (var b in s.Blocks) b?.Fix();
            }
            foreach (var f in Functions)
            {
                if (f.Params == null) f.Params = new List<string>();
            }
        }

        public IEnumerable<BlockNode> AllBlocks()
        {
            foreach (var s in Stacks)
            {
                foreach (var b in Walk(s.Blocks)) yield return b;
            }
        }

        private static IEnumerable<BlockNode> Walk(List<BlockNode> list)
        {
            if (list == null) yield break;
            foreach (var b in list)
            {
                if (b == null) continue;
                yield return b;
                foreach (var child in Walk(b.Body)) yield return child;
                foreach (var child in Walk(b.ElseBody)) yield return child;
                foreach (var child in b.Inputs.Values)
                {
                    foreach (var c in Walk(new List<BlockNode> { child })) yield return c;
                }
            }
        }

        public bool IsVariableReferenced(string name)
        {
            return AllBlocks().Any(b => b.Fields != null && b.Fields.Any(kv =>
                (kv.Key == "var" || kv.Key == "list") && kv.Value == name));
        }

        public bool IsFunctionReferenced(string name)
        {
            return AllBlocks().Any(b => b.Fields != null && b.Fields.TryGetValue("func", out var v) && v == name);
        }
    }

    // ---------------------------------------------------------------------
    // Naming helpers
    // ---------------------------------------------------------------------

    public static class Ids
    {
        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "elif", "else", "while", "for", "in", "def", "return", "break",
            "continue", "pass", "and", "or", "not", "True", "False", "None",
            "print", "wait", "stop", "time", "range", "len", "abs", "floor",
            "ceil", "round", "clamp", "min", "max", "int", "float", "str",
            "sqrt", "sin", "cos", "pi", "ship", "shop"
        };

        public static string Safe(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "valor";
            var sb = new StringBuilder(raw.Length);
            foreach (var c in raw.Normalize(NormalizationForm.FormD))
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(c);
                if (cat == UnicodeCategory.NonSpacingMark) continue;
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')
                    sb.Append(c);
                else if (c == ' ' || c == '-' || c == '.')
                    sb.Append('_');
            }
            var s = sb.ToString();
            if (s.Length == 0) s = "valor";
            if (char.IsDigit(s[0])) s = "_" + s;
            if (Reserved.Contains(s)) s = s + "_";
            if (s.StartsWith("__cb_", StringComparison.Ordinal)) s = "u" + s;
            return s;
        }

        public static string Number(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "0";
            var s = d.ToString("0.############", CultureInfo.InvariantCulture);
            return s == "-0" ? "0" : s;
        }

        public static string Quote(string text)
        {
            if (text == null) text = "";
            text = text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
            return "\"" + text + "\"";
        }
    }
}
