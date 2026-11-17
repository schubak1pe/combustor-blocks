using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CombustorBlocks
{
    public sealed class EmitLine
    {
        public string Text;
        public string BlockId;
        public EmitLine(string text, string blockId) { Text = text; BlockId = blockId; }
    }

    public sealed class EmitResult
    {
        public string Source = "";
        public List<EmitLine> Lines = new List<EmitLine>();
        public List<string> Warnings = new List<string>();
        public Dictionary<string, int> FirstLineOfBlock = new Dictionary<string, int>();

        public string SourceForBlock(string id)
        {
            if (id == null || !FirstLineOfBlock.TryGetValue(id, out var n)) return null;
            var line = Lines.FirstOrDefault(l => l.Text != null && l.BlockId == id);
            return line?.Text;
        }
    }

    /// <summary>
    /// Translates the block project into the game's own GS language.
    /// </summary>
    public sealed class GsEmitter
    {
        private readonly BlockProject _project;
        private readonly EmitResult _result = new EmitResult();
        private readonly StringBuilder _sb = new StringBuilder();
        private int _indent;
        private bool _usedRandom;
        private bool _usedPow;
        private readonly List<(string name, List<BlockNode> body)> _starts = new List<(string, List<BlockNode>)>();
        private readonly List<(string name, List<BlockNode> body)> _updates = new List<(string, List<BlockNode>)>();
        private readonly List<(string name, List<BlockNode> body)> _receives = new List<(string, List<BlockNode>)>();
        private readonly List<(string name, List<BlockNode> body)> _stops = new List<(string, List<BlockNode>)>();
        private readonly Dictionary<string, string> _eventVars = new Dictionary<string, string>();
        private readonly Dictionary<string, FunctionDef> _functions = new Dictionary<string, FunctionDef>(StringComparer.Ordinal);
        private readonly HashSet<string> _declaredVars = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _declaredLists = new HashSet<string>(StringComparer.Ordinal);
        private bool _simEndWarned;

        public GsEmitter(BlockProject project)
        {
            _project = project ?? new BlockProject();
        }

        public EmitResult Emit()
        {
            _project.Fix();

            // ---- collect ----
            foreach (var f in _project.Functions)
            {
                if (f == null) continue;
                var id = Ids.Safe(f.Name);
                if (_functions.ContainsKey(id)) continue;
                _functions[id] = f;
            }
            // Function definitions placed on the canvas but not registered in the project.
            foreach (var b in _project.AllBlocks())
            {
                if (b.Type != "func_def") continue;
                var id = Ids.Safe(b.Field("func", ""));
                if (_functions.ContainsKey(id)) continue;
                var def = new FunctionDef { Name = b.Field("func", "") };
                foreach (var p in (b.Field("params", "") ?? "").Split(','))
                    if (!string.IsNullOrWhiteSpace(p)) def.Params.Add(p.Trim());
                _functions[id] = def;
            }

            foreach (var stack in _project.Stacks)
            {
                if (stack?.Blocks == null || stack.Blocks.Count == 0) continue;
                var first = stack.Blocks[0];
                var spec = BlockSpecs.Get(first.Type);
                if (spec == null || !spec.IsHat)
                {
                    _result.Warnings.Add("Hay bloques sueltos sin un sombrero de evento: se ignoran.");
                    continue;
                }

                var body = stack.Blocks.Skip(1).ToList();
                switch (first.Type)
                {
                    case "when_start":
                    case "when_sim_start":
                        _starts.Add((first.Id, body));
                        break;
                    case "when_update":
                        _updates.Add((first.Id, body));
                        break;
                    case "when_stop":
                        _stops.Add((first.Id, body));
                        break;
                    case "when_receive":
                    {
                        var ev = EventVar(first.Field("event", "evento"));
                        if (!_eventVars.ContainsKey(ev)) _eventVars[ev] = ev;
                        _receives.Add((ev, body));
                        break;
                    }
                    case "when_sim_end":
                        if (!_simEndWarned)
                        {
                            _result.Warnings.Add("«Al finalizar la simulación» no tiene equivalente real en el lenguaje de Combustor: se omite.");
                            _simEndWarned = true;
                        }
                        break;
                }
            }

            // ---- header ----
            Line("# Combustor Blocks — código generado automáticamente");
            Line("# " + (_project.Name ?? "Mi programa"));
            Line("# No edites este texto a mano: se regenera desde los bloques.");
            Line("");

            // ---- prelude ----
            if (NeedsRandom())
            {
                Line("__cb_rng = [12345]");
                Line("def __cb_rand():");
                Line("    __cb_rng[0] = (__cb_rng[0] * 48271) % 2147483647");
                Line("    return __cb_rng[0] / 2147483647");
                Line("def __cb_random(a, b):");
                Line("    return a + __cb_rand() * (b - a)");
                Line("");
            }
            if (NeedsPow())
            {
                Line("def __cb_pow(a, b):");
                Line("    r = 1");
                Line("    n = int(b)");
                Line("    if n < 0:");
                Line("        return 0");
                Line("    while n > 0:");
                Line("        r = r * a");
                Line("        n = n - 1");
                Line("    return r");
                Line("");
            }

            // ---- variables and lists ----
            foreach (var v in _project.Variables)
            {
                if (v == null) continue;
                var id = Ids.Safe(v.Name);
                if (!_declaredVars.Add(id)) continue;
                var val = NumberOrZero(v.Value);
                Line(id + " = " + val, null);
            }
            foreach (var l in _project.Lists)
            {
                if (l == null) continue;
                var id = Ids.Safe(l.Name);
                if (!_declaredLists.Add(id)) continue;
                Line(id + " = []", null);
            }
            // Blocks such as "Crear variable" also declare names, so a program works
            // even if the project panel was never opened.
            foreach (var b in _project.AllBlocks())
            {
                if (b.Type == "var_create")
                {
                    var id = Ids.Safe(b.Field("var", ""));
                    if (_declaredVars.Add(id))
                        Line(id + " = " + ValueOf(b, "value"), null);
                }
                else if (b.Type == "list_create")
                {
                    var id = Ids.Safe(b.Field("list", ""));
                    if (_declaredLists.Add(id))
                        Line(id + " = []", null);
                }
            }
            if (_declaredVars.Count > 0 || _declaredLists.Count > 0) Line("");

            // event flags
            foreach (var ev in _eventVars.Keys)
            {
                Line(ev + " = False");
            }
            if (_eventVars.Count > 0) Line("");

            // ---- functions ----
            foreach (var kv in _functions)
            {
                EmitFunction(kv.Key, kv.Value);
            }

            // ---- stop handler ----
            if (_stops.Count > 0)
            {
                Line("def __cb_stop():");
                _indent++;
                foreach (var s in _stops)
                {
                    EmitBody(s.body, s.name);
                }
                _indent--;
                Line("");
            }

            // ---- start ----
            if (_starts.Count > 0)
            {
                Line("# --- Al iniciar ---");
                foreach (var s in _starts) EmitBody(s.body, s.name);
                Line("");
            }

            // ---- loop ----
            bool needsLoop = _updates.Count > 0 || _receives.Count > 0;
            if (needsLoop)
            {
                Line("# --- Bucle del programa (un tick por pasada) ---");
                Line("while True:");
                _indent++;
                foreach (var u in _updates) EmitBody(u.body, u.name);
                foreach (var group in _receives.GroupBy(r => r.name))
                {
                    Line("if " + group.Key + ":");
                    _indent++;
                    Line(group.Key + " = False");
                    foreach (var r in group)
                        EmitBody(r.body, r.name);
                    _indent--;
                }
                if (_indent == 1) Line("pass", null);
                _indent--;
                Line("");
            }

            _result.Source = _sb.ToString();
            BuildFirstLines();
            return _result;
        }

        private void BuildFirstLines()
        {
            for (int i = 0; i < _result.Lines.Count; i++)
            {
                var id = _result.Lines[i].BlockId;
                if (!string.IsNullOrEmpty(id) && !_result.FirstLineOfBlock.ContainsKey(id))
                    _result.FirstLineOfBlock[id] = i + 1;
            }
        }

        // -----------------------------------------------------------------
        // Statements
        // -----------------------------------------------------------------

        private void EmitBody(List<BlockNode> body, string ownerId)
        {
            bool any = false;
            if (body != null)
            {
                foreach (var b in body)
                {
                    if (b == null) continue;
                    if (BlockSpecs.Get(b.Type)?.IsHat == true) continue; // a hat inside a body is ignored
                    EmitStatement(b);
                    any = true;
                }
            }
            if (!any) Line("pass", ownerId);
        }

        private void EmitStatement(BlockNode b)
        {
            switch (b.Type)
            {
                case "if":
                    Line("if " + B(b, "cond") + ":", b.Id);
                    CBlock(b.Body, b.Id);
                    break;

                case "if_else":
                    Line("if " + B(b, "cond") + ":", b.Id);
                    CBlock(b.Body, b.Id);
                    EmitElseChain(b.ElseBody, b.Id);
                    break;

                case "else_if":
                    // only emitted by EmitElseChain; outside it behaves like if/else
                    Line("if " + B(b, "cond") + ":", b.Id);
                    CBlock(b.Body, b.Id);
                    EmitElseChain(b.ElseBody, b.Id);
                    break;

                case "repeat":
                    Line("for __cb_i in range(int(" + N(b, "times") + ")):", b.Id);
                    CBlock(b.Body, b.Id);
                    break;

                case "forever":
                    Line("while True:", b.Id);
                    CBlock(b.Body, b.Id);
                    break;

                case "while":
                    Line("while " + B(b, "cond") + ":", b.Id);
                    CBlock(b.Body, b.Id);
                    break;

                case "until":
                    Line("while not (" + B(b, "cond") + "):", b.Id);
                    CBlock(b.Body, b.Id);
                    break;

                case "wait":
                    Line("wait(" + N(b, "seconds") + ")", b.Id);
                    break;

                case "stop_all":
                    if (_stops.Count > 0) Line("__cb_stop()", b.Id);
                    Line("stop()", b.Id);
                    break;

                case "stop_this":
                    Line("break", b.Id);
                    break;

                case "when_broadcast":
                    Line(EventVar(b.Field("event", "evento")) + " = True", b.Id);
                    break;

                case "var_create":
                case "var_set":
                    Line(Ids.Safe(b.Field("var", "variable")) + " = " + A(b, "value"), b.Id);
                    break;

                case "var_delete":
                    Line(Ids.Safe(b.Field("var", "variable")) + " = None", b.Id);
                    break;

                case "var_change":
                    Line(Ids.Safe(b.Field("var", "variable")) + " = " + Ids.Safe(b.Field("var", "variable"))
                        + " + (" + N(b, "value") + ")", b.Id);
                    break;

                case "list_create":
                    Line(Ids.Safe(b.Field("list", "lista")) + " = []", b.Id);
                    break;

                case "list_add":
                    Line(Ids.Safe(b.Field("list", "lista")) + ".append(" + A(b, "value") + ")", b.Id);
                    break;

                case "list_remove":
                    Line(Ids.Safe(b.Field("list", "lista")) + ".pop(" + N(b, "index") + ")", b.Id);
                    break;

                case "list_clear":
                    Line(Ids.Safe(b.Field("list", "lista")) + ".clear()", b.Id);
                    break;

                case "func_def":
                    // emitted at the top by EmitFunction
                    break;

                case "func_call":
                    Line(FuncCall(b), b.Id);
                    break;

                case "func_return":
                    Line("return " + A(b, "value"), b.Id);
                    break;

                case "print":
                    Line("print(" + A(b, "a") + PrintSecond(b) + ")", b.Id);
                    break;

                case "part_call":
                    Line(Ids.Safe(b.Field("part", "valve_1")) + "." + Member(b) + "()", b.Id);
                    break;

                case "part_set":
                    Line(Ids.Safe(b.Field("part", "valve_1")) + "." + Member(b) + " = " + A(b, "value"), b.Id);
                    break;

                case "shop_buy":
                    Line("shop.buy(" + Ids.Quote(b.Field("gas", "Fuel")) + ", " + N(b, "moles") + ")", b.Id);
                    break;

                default:
                    _result.Warnings.Add("Bloque desconocido: " + b.Type);
                    break;
            }
        }

        private void CBlock(List<BlockNode> body, string id)
        {
            _indent++;
            EmitBody(body, id);
            _indent--;
        }

        private void EmitElseChain(List<BlockNode> elseBody, string owner)
        {
            if (elseBody == null || elseBody.Count == 0) return;
            var first = elseBody[0];
            if (first != null && first.Type == "else_if")
            {
                Line("elif " + B(first, "cond") + ":", first.Id);
                CBlock(first.Body, first.Id);
                EmitElseChain(first.ElseBody, first.Id);
                return;
            }

            Line("else:", owner);
            CBlock(elseBody, owner);
        }

        private void EmitFunction(string id, FunctionDef def)
        {
            var pars = ParseParams(def.Params);
            Line("def " + id + "(" + string.Join(", ", pars) + "):");
            _indent++;
            // find the definition block to get its body
            var block = _project.AllBlocks().FirstOrDefault(b => b.Type == "func_def" && Ids.Safe(b.Field("func", "")) == id);
            if (block != null) EmitBody(block.Body, block.Id);
            else Line("pass", null);
            _indent--;
            Line("");
        }

        private static List<string> ParseParams(List<string> raw)
        {
            var result = new List<string>();
            if (raw == null) return result;
            foreach (var p in raw)
            {
                var s = Ids.Safe(p);
                if (!result.Contains(s)) result.Add(s);
            }
            return result;
        }

        private string FuncCall(BlockNode b)
        {
            var id = Ids.Safe(b.Field("func", "funcion"));
            var args = new List<string>();
            int count = 4;
            FunctionDef def;
            if (_functions.TryGetValue(id, out def) && def.Params != null && def.Params.Count > 0)
                count = def.Params.Count;
            for (int i = 0; i < count; i++)
            {
                var slot = "arg" + i;
                if (b.Input(slot) == null && (def == null || def.Params == null || i >= def.Params.Count)) continue;
                args.Add(A(b, slot));
            }
            return id + "(" + string.Join(", ", args) + ")";
        }

        private string PrintSecond(BlockNode b)
        {
            return b.Input("b") == null ? "" : ", " + A(b, "b");
        }

        // -----------------------------------------------------------------
        // Expressions
        // -----------------------------------------------------------------

        private string A(BlockNode parent, string key)
        {
            var node = parent?.Input(key);
            if (node == null) return DefaultFor(parent, key);
            return Expr(node);
        }

        private string DefaultFor(BlockNode parent, string key)
        {
            var spec = BlockSpecs.Get(parent?.Type);
            if (spec != null && spec.SlotKinds.TryGetValue(key, out var kind))
            {
                switch (kind)
                {
                    case SlotKind.Bool: return "False";
                    case SlotKind.Text: return "\"\"";
                    case SlotKind.Num: return "0";
                }
            }
            return "0";
        }

        private string Expr(BlockNode e)
        {
            if (e == null) return "0";
            switch (e.Type)
            {
                case "num": return NumberOrZero(e.Field("value", "0"));
                case "text": return Ids.Quote(e.Field("value", ""));
                case "bool_true": return "True";
                case "bool_false": return "False";
                case "var_get": return Ids.Safe(e.Field("var", "variable"));

                case "logic_and": return "(" + A(e, "a") + " and " + A(e, "b") + ")";
                case "logic_or": return "(" + A(e, "a") + " or " + A(e, "b") + ")";
                case "logic_not": return "(not " + A(e, "a") + ")";

                case "cmp_eq": return "(" + A(e, "a") + " == " + A(e, "b") + ")";
                case "cmp_ne": return "(not (" + A(e, "a") + " == " + A(e, "b") + "))";
                case "cmp_gt": return "(" + A(e, "a") + " > " + A(e, "b") + ")";
                case "cmp_lt": return "(" + A(e, "a") + " < " + A(e, "b") + ")";
                case "cmp_gte": return "(not (" + A(e, "a") + " < " + A(e, "b") + "))";
                case "cmp_lte": return "(not (" + A(e, "a") + " > " + A(e, "b") + "))";

                case "math_add": return "(" + A(e, "a") + " + " + A(e, "b") + ")";
                case "math_sub": return "(" + A(e, "a") + " - " + A(e, "b") + ")";
                case "math_mul": return "(" + A(e, "a") + " * " + A(e, "b") + ")";
                case "math_div": return "(" + A(e, "a") + " / " + A(e, "b") + ")";
                case "math_mod": return "(" + A(e, "a") + " % " + A(e, "b") + ")";
                case "math_pow": _usedPow = true; return "__cb_pow(" + A(e, "a") + ", " + A(e, "b") + ")";
                case "math_sqrt": return "sqrt(" + A(e, "a") + ")";
                case "math_random": _usedRandom = true; return "__cb_random(" + A(e, "a") + ", " + A(e, "b") + ")";
                case "math_round": return "round(" + A(e, "a") + ")";
                case "math_abs": return "abs(" + A(e, "a") + ")";

                case "list_get": return Ids.Safe(e.Field("list", "lista")) + "[" + N(e, "index") + "]";
                case "list_length": return "len(" + Ids.Safe(e.Field("list", "lista")) + ")";
                case "list_contains": return "(" + Ids.Safe(e.Field("list", "lista")) + ".index(" + A(e, "value") + ") != -1)";
                case "list_index": return Ids.Safe(e.Field("list", "lista")) + ".index(" + A(e, "value") + ")";

                case "part_get": return Ids.Safe(e.Field("part", "sensor_1")) + "." + Member(e);
                case "sensor_gas": return Ids.Safe(e.Field("part", "sensor_1")) + ".gas(" + Ids.Quote(e.Field("gas", "Fuel")) + ")";
                case "sensor_moles": return Ids.Safe(e.Field("part", "sensor_1")) + ".moles(" + Ids.Quote(e.Field("gas", "Fuel")) + ")";

                case "ship_get": return "ship." + Member(e);
                case "shop_get": return "shop." + Member(e);
                case "shop_gas": return "shop." + Member(e) + "(" + Ids.Quote(e.Field("gas", "Fuel")) + ")";

                case "func_get":
                {
                    var id = Ids.Safe(e.Field("func", "funcion"));
                    var args = new List<string>();
                    int count = 4;
                    FunctionDef def;
                    if (_functions.TryGetValue(id, out def) && def.Params != null && def.Params.Count > 0) count = def.Params.Count;
                    for (int i = 0; i < count; i++)
                    {
                        if (e.Input("arg" + i) == null && (def == null || def.Params == null || i >= def.Params.Count)) continue;
                        args.Add(A(e, "arg" + i));
                    }
                    return id + "(" + string.Join(", ", args) + ")";
                }

                case "when_start":
                case "when_update":
                    return "0";
                default:
                    if (BlockSpecs.Get(e.Type)?.IsReporter == true)
                        _result.Warnings.Add("Reportero sin traducir: " + e.Type);
                    return "0";
            }
        }

        private string N(BlockNode parent, string key)
        {
            var node = parent?.Input(key);
            if (node == null)
            {
                var spec = BlockSpecs.Get(parent?.Type);
                if (spec != null && spec.SlotKinds.TryGetValue(key, out var k) && k == SlotKind.Num) return "0";
                return "0";
            }
            if (node.Type == "num") return NumberOrZero(node.Field("value", "0"));
            if (node.Type == "text") return "0";
            return Expr(node);
        }

        private string B(BlockNode parent, string key)
        {
            var node = parent?.Input(key);
            if (node == null) return "False";
            return Expr(node);
        }

        private static string Member(BlockNode b)
        {
            var m = b.Field("member", "");
            if (string.IsNullOrWhiteSpace(m)) return "is_on";
            // members are fixed identifiers from the game
            return Ids.Safe(m);
        }

        private string ValueOf(BlockNode parent, string key)
        {
            var node = parent?.Input(key);
            if (node == null) return "0";
            return Expr(node);
        }

        private static string NumberOrZero(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "0";
            if (double.TryParse(raw.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return Ids.Number(d);
            return "0";
        }

        private string EventVar(string raw)
        {
            var id = Ids.Safe(raw);
            return "__cb_evt_" + id;
        }

        private bool NeedsRandom()
        {
            return _project.AllBlocks().Any(b => b.Type == "math_random");
        }

        private bool NeedsPow()
        {
            return _project.AllBlocks().Any(b => b.Type == "math_pow");
        }

        // -----------------------------------------------------------------
        // Source writing
        // -----------------------------------------------------------------

        private void Line(string text, string blockId = null)
        {
            var pad = new string(' ', _indent * 4);
            _sb.Append(pad).Append(text).Append('\n');
            _result.Lines.Add(new EmitLine(pad + text, blockId));
        }
    }
}
