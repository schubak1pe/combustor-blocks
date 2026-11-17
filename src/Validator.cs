using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CombustorBlocks
{
    public enum Severity { Info, Warning, Error }

    public sealed class Issue
    {
        public Severity Severity;
        public string Text;
        public string BlockId;

        public Issue(Severity s, string t, string id = null)
        {
            Severity = s; Text = t; BlockId = id;
        }
    }

    public static class Validator
    {
        public static List<Issue> Validate(BlockProject project)
        {
            var issues = new List<Issue>();
            if (project == null) return issues;
            project.Fix();

            var vars = new HashSet<string>(project.Variables.Where(v => v != null).Select(v => Ids.Safe(v.Name)));
            var lists = new HashSet<string>(project.Lists.Where(l => l != null).Select(l => Ids.Safe(l.Name)));
            var funcs = new HashSet<string>(project.Functions.Where(f => f != null).Select(f => Ids.Safe(f.Name)));

            foreach (var b in project.AllBlocks())
            {
                if (b.Type == "var_create" || b.Type == "var_set" || b.Type == "var_change" || b.Type == "var_delete")
                    vars.Add(Ids.Safe(b.Field("var", "")));
                if (b.Type == "list_create" || b.Type == "list_add" || b.Type == "list_remove" ||
                    b.Type == "list_clear" || b.Type == "list_get" || b.Type == "list_length" ||
                    b.Type == "list_contains" || b.Type == "list_index")
                    lists.Add(Ids.Safe(b.Field("list", "")));
                if (b.Type == "func_def")
                    funcs.Add(Ids.Safe(b.Field("func", "")));
            }

            bool anyHat = false;
            bool anyStopAll = project.AllBlocks().Any(b => b.Type == "stop_all");

            foreach (var stack in project.Stacks)
            {
                if (stack?.Blocks == null || stack.Blocks.Count == 0) continue;
                var first = stack.Blocks[0];
                var spec = BlockSpecs.Get(first?.Type);
                if (spec == null || !spec.IsHat)
                {
                    issues.Add(new Issue(Severity.Error,
                        "Este grupo de bloques no empieza por un evento. Añade «Al iniciar» o «En cada actualización» arriba.", first?.Id));
                }
                else
                {
                    anyHat = true;
                }
                Walk(stack.Blocks, issues, vars, lists, funcs, false, false);
            }

            if (!anyHat)
                issues.Add(new Issue(Severity.Warning, "Todavía no hay ningún evento: el programa no hará nada."));

            if (anyStopAll == false && project.AllBlocks().Any(b => b.Type == "when_stop"))
                issues.Add(new Issue(Severity.Warning,
                    "«Al detener el programa» solo se ejecuta si algún bloque usa «Detener programa»."));

            if (project.AllBlocks().Any(b => b.Type == "when_sim_end"))
                issues.Add(new Issue(Severity.Warning,
                    "«Al finalizar la simulación» no existe en el lenguaje del juego: ese grupo se omite."));

            return issues;
        }

        private static void Walk(List<BlockNode> list, List<Issue> issues, HashSet<string> vars,
            HashSet<string> lists, HashSet<string> funcs, bool inLoop, bool inFunc)
        {
            if (list == null) return;
            foreach (var b in list)
            {
                if (b == null) continue;
                var spec = BlockSpecs.Get(b.Type);
                if (spec == null)
                {
                    issues.Add(new Issue(Severity.Error, "Bloque desconocido: " + b.Type, b.Id));
                    continue;
                }

                if (spec.IsHat)
                {
                    issues.Add(new Issue(Severity.Error, "Un evento no puede ir dentro de otro bloque.", b.Id));
                    continue;
                }

                if (b.Type == "stop_this" && !inLoop)
                    issues.Add(new Issue(Severity.Error, "«Detener este bloque» solo funciona dentro de un bucle.", b.Id));

                if (b.Type == "func_return" && !inFunc)
                    issues.Add(new Issue(Severity.Error, "«Devolver» solo funciona dentro de una función.", b.Id));

                if (b.Type == "else_if")
                    issues.Add(new Issue(Severity.Info, "«Si no, si» va dentro del hueco «Si no» de un bloque Si.", b.Id));

                // variable / list / function names
                if (spec.FieldKinds.TryGetValue("var", out var fk) && fk == FieldKind.VarName)
                {
                    var id = Ids.Safe(b.Field("var", ""));
                    if (!vars.Contains(id))
                        issues.Add(new Issue(Severity.Warning, "La variable «" + b.Field("var", "") + "» no está creada.", b.Id));
                }
                if (spec.FieldKinds.TryGetValue("list", out var lk) && lk == FieldKind.ListName)
                {
                    var id = Ids.Safe(b.Field("list", ""));
                    if (!lists.Contains(id))
                        issues.Add(new Issue(Severity.Warning, "La lista «" + b.Field("list", "") + "» no está creada.", b.Id));
                }
                if (b.Type == "func_call" || b.Type == "func_get")
                {
                    var id = Ids.Safe(b.Field("func", ""));
                    if (!funcs.Contains(id))
                        issues.Add(new Issue(Severity.Warning, "La función «" + b.Field("func", "") + "» no está definida.", b.Id));
                }

                ValidateDataBlock(b, issues);
                ValidateNumbers(b, issues);
                ValidateSlots(b, issues);

                bool loopHere = inLoop || b.Type == "repeat" || b.Type == "forever" ||
                                b.Type == "while" || b.Type == "until";
                bool funcHere = inFunc || b.Type == "func_def";
                Walk(b.Body, issues, vars, lists, funcs, loopHere, funcHere);
                Walk(b.ElseBody, issues, vars, lists, funcs, loopHere, funcHere);
                foreach (var kv in b.Inputs)
                {
                    var child = kv.Value;
                    if (child == null) continue;
                    var cs = BlockSpecs.Get(child.Type);
                    if (cs != null && !cs.IsReporter)
                        issues.Add(new Issue(Severity.Warning, "Aquí hace falta un valor, no un bloque de acción.", child.Id));
                }
            }
        }

        private static void ValidateDataBlock(BlockNode b, List<Issue> issues)
        {
            if (b.Type != "part_call" && b.Type != "part_set" && b.Type != "part_get" &&
                b.Type != "sensor_gas" && b.Type != "sensor_moles") return;

            var partName = b.Field("part", "");
            if (string.IsNullOrWhiteSpace(partName))
            {
                issues.Add(new Issue(Severity.Error, "Falta elegir una parte del motor.", b.Id));
                return;
            }

            PartInfo part = GameApi.FindPart(partName);
            if (part == null)
            {
                issues.Add(new Issue(Severity.Warning,
                    "«" + partName + "» no existe ahora en el motor. Coloca la pieza o elige otra.", b.Id));
                return;
            }

            if (b.Type == "sensor_gas" || b.Type == "sensor_moles")
            {
                if (part.Kind != Il2CppGasSim.Scripting.PartKind.Sensor)
                    issues.Add(new Issue(Severity.Error, partName + " no es un sensor.", b.Id));
                if (string.IsNullOrWhiteSpace(b.Field("gas", "")))
                    issues.Add(new Issue(Severity.Error, "Falta el nombre del gas.", b.Id));
                return;
            }

            var member = b.Field("member", "");
            var list = BlockSpecs.MembersFor(part.Kind);
            var info = list.FirstOrDefault(m => string.Equals(m.Name, member, StringComparison.Ordinal));
            if (info == null)
            {
                issues.Add(new Issue(Severity.Warning,
                    "«" + member + "» no es una operación de " + PartInfo.SpanishKind(part.Kind) + ".", b.Id));
                return;
            }

            if (b.Type == "part_call" && !info.Method)
                issues.Add(new Issue(Severity.Error, "«" + member + "» es un valor; se lee sin paréntesis.", b.Id));
            if (b.Type == "part_get" && info.Method)
                issues.Add(new Issue(Severity.Error, "«" + member + "» es una acción; se llama con ().", b.Id));
            if (b.Type == "part_set" && (info.Method || !info.Write))
                issues.Add(new Issue(Severity.Error, "«" + member + "» no se puede cambiar en " + partName + ".", b.Id));

            if (b.Type == "part_set")
                ValidateRange(b, part.Kind, member, issues);
        }

        private static void ValidateRange(BlockNode b, Il2CppGasSim.Scripting.PartKind kind, string member, List<Issue> issues)
        {
            var input = b.Input("value");
            if (input == null || input.Type != "num") return;
            if (!double.TryParse(input.Field("value", "").Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var v)) return;

            double min = double.NaN, max = double.NaN;
            switch (kind)
            {
                case Il2CppGasSim.Scripting.PartKind.Injector:
                    if (member == "rate") { min = 0.1; max = 1000; }
                    if (member == "temperature") { min = 50; max = 6000; }
                    if (member == "max_pressure") { min = 0; }
                    break;
                case Il2CppGasSim.Scripting.PartKind.Igniter:
                    if (member == "temperature") { min = 400; max = 6000; }
                    break;
                case Il2CppGasSim.Scripting.PartKind.Hole:
                    if (member == "pull") { min = 0.5; max = 1000; }
                    break;
                case Il2CppGasSim.Scripting.PartKind.Fan:
                    if (member == "power") { min = 0; max = 5000; }
                    if (member == "top_speed") { min = 0; max = 200; }
                    break;
                case Il2CppGasSim.Scripting.PartKind.Filter:
                    if (member == "min_size" || member == "max_size") { min = 0; max = 10; }
                    if (member == "flow_rate") { min = 0; max = 60; }
                    break;
                case Il2CppGasSim.Scripting.PartKind.Gate:
                    if (member == "limit") { min = 0; }
                    break;
            }

            if (!double.IsNaN(min) && v < min)
                issues.Add(new Issue(Severity.Warning, "El valor " + v + " está por debajo del mínimo (" + min + ").", b.Id));
            if (!double.IsNaN(max) && v > max)
                issues.Add(new Issue(Severity.Warning, "El valor " + v + " supera el máximo (" + max + ").", b.Id));
        }

        private static void ValidateNumbers(BlockNode b, List<Issue> issues)
        {
            if (b.Type == "math_div" || b.Type == "math_mod")
            {
                var d = b.Input("b");
                if (d != null && d.Type == "num" && Parse(d.Field("value", "0")) == 0)
                    issues.Add(new Issue(Severity.Warning, "Dividir por cero dará error en el juego.", b.Id));
            }
            if (b.Type == "math_sqrt")
            {
                var a = b.Input("a");
                if (a != null && a.Type == "num" && Parse(a.Field("value", "0")) < 0)
                    issues.Add(new Issue(Severity.Warning, "La raíz de un número negativo dará error.", b.Id));
            }
            if (b.Type == "wait")
            {
                var a = b.Input("seconds");
                if (a != null && a.Type == "num" && Parse(a.Field("value", "0")) < 0)
                    issues.Add(new Issue(Severity.Warning, "Una espera negativa no tiene sentido.", b.Id));
            }
        }

        private static void ValidateSlots(BlockNode b, List<Issue> issues)
        {
            var spec = BlockSpecs.Get(b.Type);
            if (spec == null || !spec.IsReporter) return;
            foreach (var kv in spec.SlotKinds)
            {
                var child = b.Input(kv.Key);
                if (child == null) continue;
                var cs = BlockSpecs.Get(child.Type);
                if (cs == null) continue;
                bool boolSlot = kv.Value == SlotKind.Bool;
                bool boolChild = cs.Shape == BlockShape.Boolean ||
                                 child.Type == "bool_true" || child.Type == "bool_false";
                if (boolSlot && !boolChild)
                    issues.Add(new Issue(Severity.Info, "Este hueco espera una condición (verdadero/falso).", child.Id));
            }
        }

        private static double Parse(string s)
        {
            return double.TryParse((s ?? "0").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }
    }
}
