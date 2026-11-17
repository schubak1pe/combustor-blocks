using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppGasSim.Scripting;

namespace CombustorBlocks
{
    public enum BlockShape { Hat, Stack, Cap, Reporter, Boolean }

    public enum SlotKind { Any, Num, Bool, Text }

    public enum FieldKind
    {
        Text, Number, VarName, ListName, PartName, Method, Property,
        FuncName, EventName, GasName, ShipProp, ShopProp, Params
    }

    public sealed class BlockSpec
    {
        public string Type;
        public string Category;
        public string Label;
        public BlockShape Shape;
        public bool HasBody;
        public bool HasElse;
        public string[] Fields = Array.Empty<string>();
        public Dictionary<string, FieldKind> FieldKinds = new Dictionary<string, FieldKind>();
        public Dictionary<string, string> FieldHints = new Dictionary<string, string>();
        public string[] Slots = Array.Empty<string>();
        public Dictionary<string, SlotKind> SlotKinds = new Dictionary<string, SlotKind>();
        public string Tip = "";

        public bool IsReporter => Shape == BlockShape.Reporter || Shape == BlockShape.Boolean;
        public bool IsHat => Shape == BlockShape.Hat;
    }

    public sealed class CategoryInfo
    {
        public string Id;
        public string Name;
        public string Color;
        public string Description;
    }

    public sealed class MemberInfo
    {
        public string Name;
        public bool Read;
        public bool Write;
        public bool Method;
        public string Range;
        public string Doc;

        public MemberInfo(string name, bool read, bool write, bool method, string range, string doc)
        {
            Name = name; Read = read; Write = write; Method = method; Range = range; Doc = doc;
        }
    }

    public static class BlockSpecs
    {
        public static readonly List<CategoryInfo> Categories = new List<CategoryInfo>
        {
            new CategoryInfo { Id = "events",    Name = "Eventos",       Color = "#E6B23A", Description = "Cuándo empieza el programa y cuándo se ejecuta cada bloque." },
            new CategoryInfo { Id = "control",   Name = "Control",       Color = "#F08A3C", Description = "Condiciones, bucles, esperas y paradas." },
            new CategoryInfo { Id = "logic",     Name = "Lógica",        Color = "#4FB96A", Description = "Combinar condiciones con Y, O y NO." },
            new CategoryInfo { Id = "compare",   Name = "Comparación",   Color = "#2FA8C7", Description = "Comparar números y texto." },
            new CategoryInfo { Id = "math",      Name = "Matemáticas",   Color = "#6BC24A", Description = "Cuentas y números aleatorios." },
            new CategoryInfo { Id = "variables", Name = "Variables",     Color = "#E2683C", Description = "Guardar valores con un nombre." },
            new CategoryInfo { Id = "lists",     Name = "Listas",        Color = "#D9534F", Description = "Colecciones ordenadas de valores." },
            new CategoryInfo { Id = "functions", Name = "Funciones",     Color = "#C05CD9", Description = "Bloques propios con entradas y resultado." },
            new CategoryInfo { Id = "data",      Name = "Combustor",     Color = "#7C6CF0", Description = "Partes, sensores, nave y tienda reales del juego." },
        };

        public static readonly Dictionary<string, BlockSpec> ByType = new Dictionary<string, BlockSpec>(StringComparer.Ordinal);
        public static readonly List<BlockSpec> All = new List<BlockSpec>();

        public static BlockSpec Get(string type)
        {
            return type != null && ByType.TryGetValue(type, out var s) ? s : null;
        }

        public static string CategoryColor(string category)
        {
            var c = Categories.FirstOrDefault(x => x.Id == category);
            return c?.Color ?? "#7C6CF0";
        }

        public static string CategoryName(string category)
        {
            var c = Categories.FirstOrDefault(x => x.Id == category);
            return c?.Name ?? category;
        }

        public static IEnumerable<BlockSpec> InCategory(string category)
        {
            return All.Where(s => s.Category == category);
        }

        private static void Add(BlockSpec s)
        {
            ByType[s.Type] = s;
            All.Add(s);
        }

        private static BlockSpec Spec(string type, string cat, string label, BlockShape shape,
            string[] fields = null, string[] slots = null, bool body = false, bool elseBody = false, string tip = "")
        {
            var s = new BlockSpec
            {
                Type = type,
                Category = cat,
                Label = label,
                Shape = shape,
                Fields = fields ?? Array.Empty<string>(),
                Slots = slots ?? Array.Empty<string>(),
                HasBody = body,
                HasElse = elseBody,
                Tip = tip
            };
            foreach (var f in s.Fields) s.FieldKinds[f] = FieldKind.Text;
            foreach (var sl in s.Slots) s.SlotKinds[sl] = SlotKind.Any;
            Add(s);
            return s;
        }

        private static void Field(BlockSpec s, string key, FieldKind kind, string hint = null)
        {
            s.FieldKinds[key] = kind;
            if (hint != null) s.FieldHints[key] = hint;
        }

        private static void Slot(BlockSpec s, string key, SlotKind kind)
        {
            s.SlotKinds[key] = kind;
        }

        static BlockSpecs()
        {
            BuildBlocks();
            BuildMembers();
        }

        private static void BuildBlocks()
        {
            // ---------------- EVENTS ----------------
            Spec("when_start", "events", "Al iniciar el programa", BlockShape.Hat,
                tip: "El código de debajo se ejecuta una vez al pulsar Ejecutar.");
            Spec("when_update", "events", "En cada actualización", BlockShape.Hat,
                tip: "El código de debajo se ejecuta una vez por tick de simulación (1/64 s).");
            Spec("when_receive", "events", "Al recibir evento {event}", BlockShape.Hat, new[] { "event" },
                tip: "Se comprueba una vez por tick, después de los bloques de actualización.");
            Field(Get("when_receive"), "event", FieldKind.EventName, "nombre_del_evento");
            Spec("when_stop", "events", "Al detener el programa", BlockShape.Hat,
                tip: "Se ejecuta cuando el propio programa usa el bloque Detener programa. El botón Stop del juego no puede ejecutarlo.");
            Spec("when_sim_start", "events", "Al comenzar la simulación", BlockShape.Hat,
                tip: "El script solo corre con la simulación, así que se ejecuta al arrancar el programa (igual que Al iniciar).");
            Spec("when_sim_end", "events", "Al finalizar la simulación", BlockShape.Hat,
                tip: "Limitado: el lenguaje del juego no expone un evento de fin de simulación. Se muestra el aviso en el editor.");

            // ---------------- CONTROL ----------------
            var s = Spec("if", "control", "Si {cond}", BlockShape.Stack, slots: new[] { "cond" }, body: true,
                tip: "Ejecuta el cuerpo solo cuando la condición es verdadera.");
            Slot(s, "cond", SlotKind.Bool);

            s = Spec("if_else", "control", "Si {cond}", BlockShape.Stack, slots: new[] { "cond" }, body: true, elseBody: true,
                tip: "Ejecuta un cuerpo u otro según la condición.");
            Slot(s, "cond", SlotKind.Bool);

            s = Spec("else_if", "control", "Si no, si {cond}", BlockShape.Stack, slots: new[] { "cond" }, body: true, elseBody: true,
                tip: "Va dentro del hueco «Si no» de un bloque Si. Se compila como elif.");
            Slot(s, "cond", SlotKind.Bool);

            s = Spec("repeat", "control", "Repetir {times} veces", BlockShape.Stack, slots: new[] { "times" }, body: true);
            Slot(s, "times", SlotKind.Num);

            Spec("forever", "control", "Repetir para siempre", BlockShape.Stack, body: true,
                tip: "Un bucle infinito; cada pasada consume un tick de simulación.");

            s = Spec("while", "control", "Mientras {cond}", BlockShape.Stack, slots: new[] { "cond" }, body: true);
            Slot(s, "cond", SlotKind.Bool);

            s = Spec("until", "control", "Hasta que {cond}", BlockShape.Stack, slots: new[] { "cond" }, body: true,
                tip: "Repite mientras la condición sea falsa.");
            Slot(s, "cond", SlotKind.Bool);

            s = Spec("wait", "control", "Esperar {seconds} s", BlockShape.Stack, slots: new[] { "seconds" });
            Slot(s, "seconds", SlotKind.Num);

            Spec("stop_all", "control", "Detener programa", BlockShape.Cap,
                tip: "Llama a stop() y ejecuta antes los bloques «Al detener el programa».");
            Spec("stop_this", "control", "Detener este bloque", BlockShape.Cap,
                tip: "Sale del bucle actual con break.");
            Spec("when_broadcast", "control", "Enviar evento {event}", BlockShape.Stack, new[] { "event" },
                tip: "Marca el evento para que lo recojan los bloques «Al recibir evento» en el siguiente tick.");
            Field(Get("when_broadcast"), "event", FieldKind.EventName, "nombre_del_evento");

            // ---------------- LOGIC ----------------
            s = Spec("logic_and", "logic", "{a} y {b}", BlockShape.Boolean, slots: new[] { "a", "b" });
            Slot(s, "a", SlotKind.Bool); Slot(s, "b", SlotKind.Bool);

            s = Spec("logic_or", "logic", "{a} o {b}", BlockShape.Boolean, slots: new[] { "a", "b" });
            Slot(s, "a", SlotKind.Bool); Slot(s, "b", SlotKind.Bool);

            s = Spec("logic_not", "logic", "no {a}", BlockShape.Boolean, slots: new[] { "a" });
            Slot(s, "a", SlotKind.Bool);

            Spec("bool_true", "logic", "verdadero", BlockShape.Boolean);
            Spec("bool_false", "logic", "falso", BlockShape.Boolean);

            // ---------------- COMPARE ----------------
            AddCompare("cmp_eq", "{a} = {b}", "Igual que. En el código se escribe ==.");
            AddCompare("cmp_ne", "{a} ≠ {b}", "Distinto de.");
            AddCompare("cmp_gt", "{a} > {b}", "Mayor que.");
            AddCompare("cmp_lt", "{a} < {b}", "Menor que.");
            AddCompare("cmp_gte", "{a} ≥ {b}", "Mayor o igual que.");
            AddCompare("cmp_lte", "{a} ≤ {b}", "Menor o igual que.");

            // ---------------- MATH ----------------
            AddMath("math_add", "{a} + {b}");
            AddMath("math_sub", "{a} − {b}");
            AddMath("math_mul", "{a} × {b}");
            AddMath("math_div", "{a} ÷ {b}");
            AddMath("math_mod", "{a} módulo {b}");
            AddMath("math_pow", "{a} elevado a {b}");

            s = Spec("math_sqrt", "math", "raíz de {a}", BlockShape.Reporter, slots: new[] { "a" });
            Slot(s, "a", SlotKind.Num);

            s = Spec("math_random", "math", "aleatorio entre {a} y {b}", BlockShape.Reporter, slots: new[] { "a", "b" },
                tip: "Se genera con aritmética porque el lenguaje del juego no tiene random().");
            Slot(s, "a", SlotKind.Num); Slot(s, "b", SlotKind.Num);

            s = Spec("math_round", "math", "redondear {a}", BlockShape.Reporter, slots: new[] { "a" });
            Slot(s, "a", SlotKind.Num);
            s = Spec("math_abs", "math", "valor absoluto de {a}", BlockShape.Reporter, slots: new[] { "a" });
            Slot(s, "a", SlotKind.Num);

            // ---------------- VARIABLES ----------------
            s = Spec("var_create", "variables", "Crear variable {var} = {value}", BlockShape.Stack,
                new[] { "var" }, new[] { "value" });
            Field(s, "var", FieldKind.VarName, "nombre"); Slot(s, "value", SlotKind.Any);

            s = Spec("var_delete", "variables", "Eliminar variable {var}", BlockShape.Stack, new[] { "var" });
            Field(s, "var", FieldKind.VarName, "nombre");

            s = Spec("var_set", "variables", "Establecer {var} a {value}", BlockShape.Stack,
                new[] { "var" }, new[] { "value" });
            Field(s, "var", FieldKind.VarName, "nombre"); Slot(s, "value", SlotKind.Any);

            s = Spec("var_change", "variables", "Cambiar {var} por {value}", BlockShape.Stack,
                new[] { "var" }, new[] { "value" });
            Field(s, "var", FieldKind.VarName, "nombre"); Slot(s, "value", SlotKind.Num);

            s = Spec("var_get", "variables", "{var}", BlockShape.Reporter, new[] { "var" },
                tip: "Lee el valor actual de una variable.");
            Field(s, "var", FieldKind.VarName, "nombre");

            // ---------------- LISTS ----------------
            s = Spec("list_create", "lists", "Crear lista {list}", BlockShape.Stack, new[] { "list" });
            Field(s, "list", FieldKind.ListName, "nombre");

            s = Spec("list_add", "lists", "Añadir {value} a {list}", BlockShape.Stack,
                new[] { "list" }, new[] { "value" });
            Field(s, "list", FieldKind.ListName, "nombre"); Slot(s, "value", SlotKind.Any);

            s = Spec("list_remove", "lists", "Eliminar elemento {index} de {list}", BlockShape.Stack,
                new[] { "list" }, new[] { "index" });
            Field(s, "list", FieldKind.ListName, "nombre"); Slot(s, "index", SlotKind.Num);

            s = Spec("list_clear", "lists", "Vaciar lista {list}", BlockShape.Stack, new[] { "list" });
            Field(s, "list", FieldKind.ListName, "nombre");

            s = Spec("list_get", "lists", "elemento {index} de {list}", BlockShape.Reporter,
                new[] { "list" }, new[] { "index" });
            Field(s, "list", FieldKind.ListName, "nombre"); Slot(s, "index", SlotKind.Num);

            s = Spec("list_length", "lists", "longitud de {list}", BlockShape.Reporter, new[] { "list" });
            Field(s, "list", FieldKind.ListName, "nombre");

            s = Spec("list_contains", "lists", "{list} contiene {value}", BlockShape.Boolean,
                new[] { "list" }, new[] { "value" });
            Field(s, "list", FieldKind.ListName, "nombre"); Slot(s, "value", SlotKind.Any);

            s = Spec("list_index", "lists", "posición de {value} en {list}", BlockShape.Reporter,
                new[] { "list" }, new[] { "value" });
            Field(s, "list", FieldKind.ListName, "nombre"); Slot(s, "value", SlotKind.Any);

            // ---------------- FUNCTIONS ----------------
            s = Spec("func_def", "functions", "Definir función {func}({params})", BlockShape.Stack,
                new[] { "func", "params" }, body: true,
                tip: "Se compila como def. Las funciones se colocan al principio del programa.");
            Field(s, "func", FieldKind.FuncName, "nombre");
            Field(s, "params", FieldKind.Params, "a, b");

            s = Spec("func_call", "functions", "Llamar {func}", BlockShape.Stack, new[] { "func" },
                slots: new[] { "arg0", "arg1", "arg2", "arg3" });
            Field(s, "func", FieldKind.FuncName, "nombre");
            Slot(s, "arg0", SlotKind.Any); Slot(s, "arg1", SlotKind.Any);
            Slot(s, "arg2", SlotKind.Any); Slot(s, "arg3", SlotKind.Any);

            s = Spec("func_return", "functions", "Devolver {value}", BlockShape.Cap, slots: new[] { "value" });
            Slot(s, "value", SlotKind.Any);

            s = Spec("func_get", "functions", "resultado de {func}", BlockShape.Reporter,
                new[] { "func" }, slots: new[] { "arg0", "arg1", "arg2", "arg3" },
                tip: "Usa una función como valor. Si no devuelve nada, vale None.");
            Field(s, "func", FieldKind.FuncName, "nombre");
            Slot(s, "arg0", SlotKind.Any); Slot(s, "arg1", SlotKind.Any);
            Slot(s, "arg2", SlotKind.Any); Slot(s, "arg3", SlotKind.Any);

            // ---------------- COMBUSTOR DATA ----------------
            s = Spec("print", "data", "Mostrar {a}", BlockShape.Stack, slots: new[] { "a", "b" },
                tip: "Escribe en el panel de salida del editor de código del juego.");
            Slot(s, "a", SlotKind.Any); Slot(s, "b", SlotKind.Any);

            s = Spec("part_call", "data", "{part}.{member}", BlockShape.Stack, new[] { "part", "member" },
                tip: "Llama a una acción real de una parte del motor (abrir, cerrar, encender...).");
            Field(s, "part", FieldKind.PartName, "valve_1");
            Field(s, "member", FieldKind.Method, "open");

            s = Spec("part_set", "data", "Establecer {part}.{member} a {value}", BlockShape.Stack,
                new[] { "part", "member" }, new[] { "value" });
            Field(s, "part", FieldKind.PartName, "injector_1");
            Field(s, "member", FieldKind.Property, "rate");
            Slot(s, "value", SlotKind.Any);

            s = Spec("part_get", "data", "{part}.{member}", BlockShape.Reporter,
                new[] { "part", "member" });
            Field(s, "part", FieldKind.PartName, "sensor_1");
            Field(s, "member", FieldKind.Property, "temperature");

            s = Spec("sensor_gas", "data", "proporción de {gas} en {part}", BlockShape.Reporter,
                new[] { "part", "gas" }, tip: "Equivale a sensor_1.gas(\"Fuel\").");
            Field(s, "part", FieldKind.PartName, "sensor_1");
            Field(s, "gas", FieldKind.GasName, "Fuel");

            s = Spec("sensor_moles", "data", "moles de {gas} en {part}", BlockShape.Reporter,
                new[] { "part", "gas" }, tip: "Equivale a sensor_1.moles(\"Fuel\").");
            Field(s, "part", FieldKind.PartName, "sensor_1");
            Field(s, "gas", FieldKind.GasName, "Fuel");

            s = Spec("ship_get", "data", "nave: {member}", BlockShape.Reporter, new[] { "member" },
                tip: "Solo existe en Labrek.");
            Field(s, "member", FieldKind.ShipProp, "altitude");

            s = Spec("shop_get", "data", "tienda: {member}", BlockShape.Reporter, new[] { "member" },
                tip: "Solo existe en Feed the Hole.");
            Field(s, "member", FieldKind.ShopProp, "cash");

            s = Spec("shop_gas", "data", "tienda: {member} de {gas}", BlockShape.Reporter,
                new[] { "member", "gas" });
            Field(s, "member", FieldKind.ShopProp, "stock");
            Field(s, "gas", FieldKind.GasName, "Fuel");

            s = Spec("shop_buy", "data", "comprar {moles} mol de {gas}", BlockShape.Stack,
                new[] { "gas" }, new[] { "moles" });
            Field(s, "gas", FieldKind.GasName, "Fuel");
            Slot(s, "moles", SlotKind.Num);
        }

        private static void AddCompare(string type, string label, string tip)
        {
            var s = Spec(type, "compare", label, BlockShape.Boolean, slots: new[] { "a", "b" }, tip: tip);
            Slot(s, "a", SlotKind.Any);
            Slot(s, "b", SlotKind.Any);
        }

        private static void AddMath(string type, string label)
        {
            var s = Spec(type, "math", label, BlockShape.Reporter, slots: new[] { "a", "b" });
            Slot(s, "a", SlotKind.Num);
            Slot(s, "b", SlotKind.Num);
        }

        // -----------------------------------------------------------------
        // Combustor's real part members (verified against the game's docs)
        // -----------------------------------------------------------------

        public static readonly Dictionary<PartKind, List<MemberInfo>> Members =
            new Dictionary<PartKind, List<MemberInfo>>();

        public static readonly List<MemberInfo> CommonMembers = new List<MemberInfo>
        {
            new MemberInfo("on", false, false, true, "", "Enciende la parte."),
            new MemberInfo("off", false, false, true, "", "Apaga la parte."),
            new MemberInfo("toggle", false, false, true, "", "Cambia el estado."),
            new MemberInfo("is_on", true, false, false, "", "Verdadero mientras está encendida."),
            new MemberInfo("is_broken", true, false, false, "", "Verdadero si ha sido destruida."),
        };

        private static void BuildMembers()
        {
            Members[PartKind.Valve] = new List<MemberInfo>
            {
                new MemberInfo("open", false, false, true, "", "Abre la válvula."),
                new MemberInfo("close", false, false, true, "", "Cierra la válvula."),
                new MemberInfo("toggle", false, false, true, "", "Abre si está cerrada, cierra si está abierta."),
                new MemberInfo("is_open", true, false, false, "", "Verdadero mientras está abierta."),
                new MemberInfo("is_broken", true, false, false, "", "Verdadero si ha sido destruida."),
                new MemberInfo("pressure", true, false, false, "", "Presión junto a la válvula, en el lado de mayor presión."),
                new MemberInfo("temperature", true, false, false, "K", "Temperatura junto a la válvula, en el lado de mayor presión."),
            };

            Members[PartKind.Injector] = Common(("rate", true, true, "", "Gas que añade, en mol/s (0.1 a 1000)."),
                ("temperature", true, true, "K", "Temperatura del gas que añade (50 a 6000 K)."),
                ("max_pressure", true, true, "", "Deja de añadir por encima de esta presión. 0 = sin límite."),
                ("gas", true, true, "", "Gas que añade, por nombre: injector_1.gas = \"Fuel\"."));

            Members[PartKind.Igniter] = Common(("temperature", true, true, "K", "Temperatura que mantiene en su zona (400 a 6000 K)."));

            Members[PartKind.Hole] = Common(("pull", true, true, "", "Proporción del gas que absorbe cada segundo (0.5 a 1000)."));

            Members[PartKind.Fan] = Common(("power", true, true, "", "Fuerza con la que empuja el gas (0 a 5000)."),
                ("top_speed", true, true, "", "Velocidad máxima del gas, en celdas/s (0 a 200)."),
                ("reverse", true, true, "", "Invierte el sentido del ventilador."));

            Members[PartKind.Filter] = Common(("min_size", true, true, "", "Molécula más pequeña que deja pasar (0 a 10)."),
                ("max_size", true, true, "", "Molécula más grande que deja pasar (0 a 10)."),
                ("flow_rate", true, true, "", "Velocidad del gas que se filtra (0 a 60)."));

            Members[PartKind.Gate] = Common(("reverse", true, true, "", "Hace pasar el gas en el otro sentido."),
                ("limit", true, true, "", "Gas máximo por segundo (mol/s). 0 = sin límite."),
                ("flow", true, false, "", "Gas que pasó en el último tick (mol/s). Solo lectura."));

            Members[PartKind.Piston] = Common(("position", true, false, "", "Distancia desde donde se colocó, en celdas."),
                ("speed", true, false, "", "Velocidad, en celdas/s."));

            Members[PartKind.Sensor] = new List<MemberInfo>
            {
                new MemberInfo("temperature", true, false, false, "K", "Temperatura de la celda."),
                new MemberInfo("pressure", true, false, false, "", "Presión de la celda (0 dentro de una pared)."),
                new MemberInfo("density", true, false, false, "", "Gas total de la celda, en mol."),
                new MemberInfo("speed", true, false, false, "", "Velocidad del gas, en celdas/s."),
                new MemberInfo("speed_x", true, false, false, "", "Velocidad del gas en el eje x."),
                new MemberInfo("speed_y", true, false, false, "", "Velocidad del gas en el eje y."),
            };
        }

        private static List<MemberInfo> Common(params (string name, bool read, bool write, string range, string doc)[] extra)
        {
            var list = new List<MemberInfo>();
            list.AddRange(CommonMembers);
            foreach (var e in extra)
                list.Add(new MemberInfo(e.name, e.read, e.write, false, e.range, e.doc));
            return list;
        }

        public static readonly List<MemberInfo> ShipMembers = new List<MemberInfo>
        {
            new MemberInfo("altitude", true, false, false, "km", "Altura sobre el suelo."),
            new MemberInfo("speed", true, false, false, "km/s", "Velocidad vertical; negativa al caer."),
            new MemberInfo("propellant", true, false, false, "", "Propelente que queda."),
            new MemberInfo("thrust", true, false, false, "", "Empuje del motor en este momento."),
            new MemberInfo("mass", true, false, false, "", "Masa del cohete con propelente."),
            new MemberInfo("launched", true, false, false, "", "Verdadero cuando ha despegado."),
        };

        public static readonly List<MemberInfo> ShopMembers = new List<MemberInfo>
        {
            new MemberInfo("cash", true, false, false, "$", "Dinero de la cartera."),
            new MemberInfo("stock", true, false, true, "mol", "Moles en stock; necesita un gas."),
            new MemberInfo("price", true, false, true, "$", "Precio por mol; necesita un gas."),
            new MemberInfo("buy", false, false, true, "mol", "Compra moles; necesita gas y cantidad."),
        };

        public static List<MemberInfo> MembersFor(PartKind kind)
        {
            return Members.TryGetValue(kind, out var list) ? list : CommonMembers;
        }

        public static string DefaultMember(PartKind kind, bool wantMethod, bool wantWrite)
        {
            var list = MembersFor(kind);
            foreach (var m in list)
            {
                if (wantMethod && m.Method) return m.Name;
                if (!wantMethod && wantWrite && m.Write && !m.Method) return m.Name;
                if (!wantMethod && !wantWrite && m.Read && !m.Method) return m.Name;
            }
            return "is_on";
        }
    }
}
