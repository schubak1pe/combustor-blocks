# Combustor Blocks — Informe técnico (Fase 1)

**Proyecto:** editor de programación visual estilo Scratch para Combustor  
**Build analizada:** Steam build `26701522` (Unity 6000.3.2f1, IL2CPP, Windows x64)  
**Fecha:** 2026-11-17

---

## 1. Resumen ejecutivo

La viabilidad es **alta**, y además mejor de lo esperado: Combustor ya incorpora un
**lenguaje de programación propio** (interno, tipo Python) con compilador y máquina
virtual, añadido en la actualización del 14 de noviembre de 2026 («Coding is here!»).
No hace falta inventar un lenguaje ni un intérprete: los bloques pueden traducirse a
ese lenguaje y ejecutarse con el propio runtime del juego.

El mod de referencia adjunto (`CombustorPngMod.zip`) **no es un mod de programación**.
Es un importador de PNG que demuestra el sistema de modding:

- MelonLoader **0.7.3** sobre un juego **IL2CPP**.
- Mods en C# (`MelonLoader.MelonMod`), armonía vía `0Harmony`, UI con **IMGUI**.
- El juego es Unity 6, no Mono: hay que compilar contra los *interop assemblies*
  que MelonLoader genera en `MelonLoader/Il2CppAssemblies`.

La integración recomendada es:

1. Editor de bloques nativo en C#/IMGUI (no Blockly: ver §7).
2. Compilador bloques → **GS** (el lenguaje real de Combustor).
3. Validación con `GasSim.Scripting.GsCompiler.Compile` (el compilador del juego).
4. Ejecución con `ScriptRig.Source = código; ScriptRig.Start(sim)` — el mismo
   runtime que usa el botón «Run» del juego.

Con eso, el programa del jugador es un programa GS normal: se guarda con el motor,
se comparte con el motor y aparece en el editor de código original.

---

## 2. El mod de referencia: qué demuestra y qué no

Contenido del ZIP:

```
Mods/CombustorPngImport.dll
UserData/CombustorPng/CombustorPngDecoder.dll
INSTALL.txt
```

`INSTALL.txt` confirma:

- Requiere **MelonLoader**.
- Ruta de instalación junto a `EngineSimulator.exe`, con carpetas `Mods` y `UserData`.
- El juego expone una lista **Mods** en el menú principal (añadido del juego, no del mod).
- El mod se engancha a la UI del juego (menú Esc del Sandbox) y a atajos (Ctrl+V).

Conclusiones aplicables:

| Aspecto | Hallazgo |
|---|---|
| Cargador | MelonLoader 0.7.3, mods DLL en `Mods/` |
| Runtime | IL2CPP (Unity 6000.3.2f1), no Mono |
| UI | IMGUI (`OnGUI`), como toda la UI del juego |
| Entrada | El juego usa el **nuevo Input System**; `UnityEngine.Input` (legacy) lanza `SEHException` |
| Logs | `MelonLoader/Logs/` y `%LOCALAPPDATA%Low/DefaultCompany/EngineSimulator/Player.log` |
| Persistencia | El mod guarda un sidecar `.pngcolors` junto al `.engine` |

**No** contiene código de programación visual, ni fuentes, ni documentación del
lenguaje. Es únicamente una prueba de que el pipeline de mods funciona en esta build.

---

## 3. El sistema de programación real de Combustor

### 3.1 Dónde está

Ensamblado `Assembly-CSharp.dll`, namespace **`GasSim.Scripting`**. Clases encontradas:

| Clase | Función |
|---|---|
| `GsLexer` | Tokenizador (Number, String, Name, Op, Newline, Indent, Dedent, End) |
| `GsParser` | Parser → AST (`IfStmt`, `WhileStmt`, `ForStmt`, `DefStmt`, `AssignStmt`, `ReturnStmt`, `BreakStmt`, `ContinueStmt`, `PassStmt`, `ExprStmt`) |
| `GsCompiler` | AST → bytecode (`GsProgram`, `GsFunc`, `Instr`, `Op`) |
| `GsVm` | Máquina virtual de pila, `Run(int budget)` |
| `ScriptRig` | Implementación de `IScriptWorld`: globals, partes, sensores, `ship`, `shop`; compila y ejecuta |
| `ScriptWindow` | Editor de código in-game (IMGUI), autocompletado, referencia, salida |
| `GsSuggest` | Autocompletado y documentación contextual |
| `Val` | Valor dinámico: `None`, `Bool`, `Num`, `Str`, `List`, `Func`, `Native`, `Part`, `Method`, `Range`, `Iter`, `Ship`, `Shop` |
| `Sensor`, `ScriptPart` | Sensores y partes expuestas al script |
| `ScriptError`, `ScriptFault` | Errores con línea |

El juego también incluye `Unity.VisualScripting` (Bolt) como dependencia de Unity,
pero **no se usa para el scripting del jugador**. El scripting real es `GsVm`.

### 3.2 El lenguaje (GS)

Sintaxis **tipo Python**, indentación obligatoria, sin llaves ni punto y coma.

```python
# comentario
x = 10
nombre = "Fuel"
lista = [1, 2, 3]

if x > 5:
    print("grande")
elif x == 5:
    print("igual")
else:
    print("pequeño")

while x > 0:
    x = x - 1

for i in range(5):
    print(i)

def doble(a):
    return a * 2
```

Palabras clave confirmadas por la referencia in-game (`script.kw.*`):

`if`, `elif`, `else`, `while`, `for`, `in`, `def`, `return`, `break`, `continue`,
`pass`, `and`, `or`, `not`, `True`, `False`, `None`

Literales: números, texto entre comillas dobles, listas `[...]`, `True/False/None`.

Acceso: `objeto.miembro`, `objeto.metodo(args)`, `lista[i]`, asignación a nombre,
a miembro (`valve_1.gas = "Fuel"`) y a índice (`lista[0] = 5`).

Operadores vistos en la documentación y en el compilador: `+ - * / % **`,
`== != < > <= >=`, `=`, `and`, `or`, `not`.

### 3.3 Modelo de ejecución

- El script **solo corre mientras corre la simulación**.
- Cada pasada de un `while` consume **un tick** de simulación (1/64 s).
- `wait(segundos)` pausa el script ese tiempo de simulación; `wait()` espera un tick.
- `time()` devuelve segundos de simulación desde el inicio.
- `stop()` termina el programa.
- Hay un **presupuesto de instrucciones** por tick (`GsVm.Run(int budget)`), para que
  un bucle no congele el juego.
- Un script puede **solo operar las partes que el jugador podría operar a mano** en
  ese momento (hay comprobaciones de permisos; error `script.err.not_allowed`).
- Una parte destruida deja de existir para el script (`script.err.part_gone`).
- Límites: profundidad de llamadas (`script.err.too_deep`), tamaño de listas y textos.

### 3.4 Builtins confirmados

| Builtin | Firma / uso |
|---|---|
| `print(...)` | escribe en el panel de salida del editor |
| `wait(segundos?)` | pausa; sin argumento = 1 tick |
| `time()` | segundos de simulación |
| `stop()` | termina el script |
| `range(a[, b[, paso]])` | iterable para `for` |
| `len(x)` | longitud de lista o texto |
| `abs`, `floor`, `ceil`, `round(x[, decimales])` | redondeo |
| `sqrt(x)` | raíz cuadrada |
| `sin(x)`, `cos(x)` | radianes |
| `clamp(x, min, max)` | limita |
| `min(...)`, `max(...)` | mínimo/máximo |
| `int(x)`, `float(x)`, `str(x)` | conversión |
| `pi` | constante |

**No hay** `random`, `exp`, `log`, `pow`, `tan`, ni acceso a ficheros/red. Cualquier
bloque que los necesite debe implementarse con código GS generado (ver §6.4).

### 3.5 API real de Combustor expuesta al script

Todo se accede por **nombre de instancia**: `valve_1`, `sensor_1`, `injector_2`, etc.
El nombre lo asigna el juego y se puede descubrir apuntando a la pieza con el editor
abierto. `ScriptRig.NameOf(part)` lo devuelve.

#### Comunes a todas las partes

| Miembro | Tipo | Notas |
|---|---|---|
| `on()` / `off()` / `toggle()` | método | enciende/apaga |
| `is_on` | bool | lectura |
| `is_broken` | bool | lectura; una válvula rota queda abierta |

#### Válvulas — `valve_N`

| Miembro | Tipo |
|---|---|
| `open()`, `close()`, `toggle()` | método |
| `is_open` | bool lectura |
| `pressure` | número lectura (lado de mayor presión junto a la válvula) |
| `temperature` | número lectura (K, lado de mayor presión) |

#### Inyectores — `injector_N`

| Miembro | Tipo | Rango |
|---|---|---|
| `rate` | número lectura/escritura | 0.1 – 1000 mol/s |
| `temperature` | número lectura/escritura | 50 – 6000 K (en *Feed the Hole* es fija) |
| `max_pressure` | número lectura/escritura | 0 = sin límite |
| `gas` | texto lectura/escritura | `injector_1.gas = "Fuel"` |

#### Encendedores — `igniter_N`

| Miembro | Tipo | Rango |
|---|---|---|
| `temperature` | número lectura/escritura | 400 – 6000 K |

#### Agujeros negros — `hole_N`

| Miembro | Tipo | Rango |
|---|---|---|
| `pull` | número lectura/escritura | 0.5 – 1000 |

#### Ventiladores — `fan_N`

| Miembro | Tipo | Rango |
|---|---|---|
| `power` | número lectura/escritura | 0 – 5000 |
| `top_speed` | número lectura/escritura | 0 – 200 |
| `reverse` | bool lectura/escritura | invierte el flujo |

#### Filtros — `filter_N`

| Miembro | Tipo | Rango |
|---|---|---|
| `min_size`, `max_size` | número lectura/escritura | 0 – 10 |
| `flow_rate` | número lectura/escritura | 0 – 60 |

#### Compuertas unidireccionales — `gate_N`

| Miembro | Tipo |
|---|---|
| `reverse` | bool lectura/escritura |
| `limit` | número lectura/escritura (0 = sin límite) |
| `flow` | número **solo lectura** (mol/s del último tick) |

#### Pistones — `piston_N`

| Miembro | Tipo |
|---|---|
| `position` | número solo lectura (celdas) |
| `speed` | número solo lectura (celdas/s) |

#### Sensores — `sensor_N`

| Miembro | Tipo | Notas |
|---|---|---|
| `temperature` | número | K |
| `pressure` | número | 0 dentro de pared |
| `density` | número | mol totales en la celda |
| `speed`, `speed_x`, `speed_y` | número | celdas/s |
| `gas("Fuel")` | número 0–1 | fracción de ese gas |
| `moles("Fuel")` | número | moles de ese gas |

Los sensores se colocan con el botón **Place sensor** del editor de código del juego
(`ScriptRig.AddSensor(x, y)`); ocupan una celda y se guardan con el motor.

#### Nave (solo Labrek) — `ship`

`altitude` (km), `speed` (km/s), `propellant`, `thrust`, `mass`, `launched` (bool).
Todos de lectura.

#### Tienda (solo Feed the Hole) — `shop`

`cash` (lectura), `stock("Fuel")`, `price("Fuel")`, `buy("Fuel", moles)`.

### 3.6 Errores y validación

`GsCompiler.Compile(string)` lanza `ScriptFault` con un mensaje localizado que incluye
la línea (`script.err.*`). `ScriptRig` expone `ErrorText`, `ErrorLine`, `Log`, `Status()`.
El editor del juego muestra el error bajo el código y resalta la línea.

Esto permite que el editor de bloques **valide el código generado con el compilador
real** antes de ejecutarlo, y que muestre errores con número de línea.

### 3.7 Persistencia

`ScriptRig.Write(BinaryWriter, GasSimulation, bool)` y `Saved` (source + sensors +
counters) indican que el script y los sensores **se guardan dentro del `.engine`** y
viajan con los códigos de compartir. El editor además mantiene un sidecar en disco
(`ScriptWindow.FileFor(key, labrek)`, `KeepOnDisk`).

Consecuencia: si el mod asigna `sim.Script.Source`, el programa forma parte del motor
guardado/compartido, y el botón «Code» del juego lo muestra tal cual.

---

## 4. Integración con el juego: opciones evaluadas

### Opción A — Generar GS y ejecutarlo con `ScriptRig` (recomendada)

```
bloques → texto GS → GsCompiler.Compile (validación) → ScriptRig.Source = texto
        → ScriptRig.Start(sim)
```

Ventajas:

- Usa el **lenguaje y la VM reales**; no hay runtime paralelo.
- El código es visible en el editor original (botón Code / F4).
- Se guarda y comparte con el motor.
- Todos los límites del juego (presupuesto, permisos, partes) se respetan.
- Si el juego cambia el lenguaje, basta ajustar el generador.

Riesgos: la API de `ScriptRig` es interna (no documentada) y puede cambiar entre
builds. Mitigación: una capa `GameApi` aislada + degradación elegante.

### Opción B — Inyectar texto en `ScriptWindow` y pulsar Run

`ScriptWindow.SetSource(texto)` + `RunOrStop()`. Útil como complemento (sincronizar
el editor de código del juego), pero depende de que la ventana esté viva y del estado
interno del `TextEditor`. No se usa como mecanismo principal.

### Opción C — Runtime propio de bloques

Interpretar bloques directamente y llamar a `IScriptWorld`/partes. **Rechazada**:
duplica el lenguaje, pierde el guardado/compartición y aumenta el riesgo.

### Opción D — Google Blockly

**No es viable de forma nativa.** Blockly es JavaScript y necesita DOM/navegador.
Unity IL2CPP no ejecuta JS; habría que embeber un navegador (CEF/UniWebView), lo que
añade decenas de MB, dependencias nativas y problemas de foco/input. Además rompería
la estética IMGUI del juego. Se implementa un editor nativo inspirado en Scratch.

---

## 5. Arquitectura recomendada

```
┌─────────────────────────────────────────────────────────────┐
│ Mod CombustorBlocks (C#, net6.0, MelonLoader)               │
│                                                             │
│  UI/EditorWindow (IMGUI)                                    │
│   ├─ Palette      categorías y bloques arrastrables         │
│   ├─ Canvas       pan/zoom, drag&drop, anidado              │
│   ├─ Inspector    campos, desplegables, validación          │
│   └─ CodePanel    código GS generado + errores              │
│                                                             │
│  Model/  BlockProject · BlockNode · BlockSpecs              │
│  Compiler/  BlocksToGs (genera GS, indentación, eventos)    │
│  Runtime/   GameApi (ScriptWindow, GasSimulation, ScriptRig)│
│             PartCatalog (partes/sensores/gases reales)      │
│             ScriptRunner (compila, valida, ejecuta)         │
│  Persistence/ ProjectStore (JSON + autosave)                │
└─────────────────────────────────────────────────────────────┘
              │  código GS
              ▼
   GsCompiler.Compile  ──►  ScriptRig.Source / Start  ──►  GsVm
```

### Modelo de datos

- `BlockProject`: variables, listas, funciones, stacks, ajustes.
- `BlockNode`: `Type`, `Fields` (texto), `Inputs` (expresiones), `Body`/`ElseBody`.
- `ScriptStack`: posición en el lienzo + lista lineal de bloques.
- Las expresiones son el mismo `BlockNode` (reporteros), lo que simplifica copiar,
  pegar, duplicar y el compilador.

### Compilador

- Recorre sombreros de evento y genera un único programa GS.
- `Al iniciar` → código de nivel superior.
- `En cada actualización` → cuerpo de `while True:`.
- `Al recibir` → flag `__cb_evt_X` + comprobación dentro del bucle.
- `Al detener` → función `__cb_stop()` invocada por el bloque `Detener programa`.
- Funciones → `def` a nivel superior.
- Variables/listas → inicialización al principio.
- Expresiones → texto con paréntesis explícitos.
- Nombres → identificadores seguros (`[A-Za-z_][A-Za-z0-9_]*`), sin acentos.

### Validación en dos capas

1. Validación propia: partes inexistentes, variables sin definir, campos vacíos,
   rangos fuera de lo permitido, bloques huérfanos.
2. Validación real: `GsCompiler.Compile(código)`; si lanza, se muestra el mensaje y
   la línea, y se marca el bloque origen (mapa línea → bloque).

### Persistencia

- Proyecto en JSON en `Application.persistentDataPath/CombustorBlocks/`.
- Autosave con debounce.
- Exportar/importar `.json`.
- El código generado se puede volcar al `.engine` vía `ScriptRig` (lo guarda el juego).

---

## 6. Mapeo de bloques a GS

### 6.1 Eventos

| Bloque | GS | Notas |
|---|---|---|
| Al iniciar el programa | código de nivel superior | se ejecuta al pulsar Run |
| En cada actualización | cuerpo de `while True:` | 1 pasada = 1 tick |
| Al recibir evento X | `if __cb_evt_X:` + reset | cooperativo, 1 tick de latencia |
| Al detener el programa | `def __cb_stop():` | se llama desde `Detener programa` |
| Al comenzar la simulación | código de nivel superior | el script solo corre con la simulación |
| Al finalizar la simulación | — | **sin equivalente real**; se documenta |

### 6.2 Control

`if/elif/else`, `while`, `for i in range(int(n))`, `while True`, `break`, `wait()`,
`stop()`.

### 6.3 Operadores y matemáticas

`and`, `or`, `not`, `==`, `!=`, `<`, `>`, `<=`, `>=`, `+ - * / % **`, `sqrt()`.
`aleatorio(a,b)` se implementa con un generador congruencial en GS (no existe
`random` en el lenguaje).

### 6.4 Listas y variables

`nombre = valor`, `nombre = nombre + delta`, `[]`, `.append`, `.pop(i)`, `[i]`,
`len()`, `.index(v) != -1`.

### 6.5 Datos de Combustor

Los bloques de datos **no hardcodean** partes: ofrecen un desplegable con las partes
reales del motor (`PartCatalog` consulta `ScriptRig.Count/PartAt/NameOf`), filtran los
miembros válidos por tipo y muestran los rangos reales. Así no se inventan sensores
ni funciones: solo se puede programar lo que el juego expone.

---

## 7. Limitaciones reales (comprobadas)

1. **No hay evento de fin de simulación** en GS. El script se pausa con la simulación;
   no recibe un callback. El bloque se omite o se marca como no disponible.
2. **No hay eventos asíncronos**. `Al recibir` se implementa con flags y se evalúa
   una vez por tick.
3. **`Al detener`** solo puede ejecutarse cuando el propio programa se detiene
   (`Detener programa`), no cuando el jugador pulsa Stop en la UI del juego.
4. **No hay `random`**. Se genera con aritmética y `time()`/semilla.
5. **No hay `pow`**. Se implementa con multiplicación repetida (exponente entero).
6. **Los sensores deben existir**. El editor los lista; si faltan, avisa. No se
   inventan lecturas globales de «temperatura del motor»: hay que colocar un sensor
   en la cámara/conducto.
7. **Una script por motor**. No hay multitarea; los sombreros se ejecutan en secuencia
   dentro del mismo bucle cooperativo.
8. **El script corre con la simulación**. En pausa no avanza.
9. **Presupuesto de ticks**. Bucles muy pesados pueden ralentizar; el juego limita
   instrucciones por tick.
10. **IL2CPP**: acceso a campos privados vía interop; un update del juego puede
    romper nombres. La capa `GameApi` concentra ese riesgo.
11. **Input**: el juego usa el nuevo Input System; hay que bloquear `GasSimInput.Update`
    mientras el editor está abierto (Harmony) para que WASD no mueva la cámara.
12. **Blockly no es viable** de forma nativa.

---

## 8. Criterios de aceptación — cómo se cubren

| Criterio | Cobertura |
|---|---|
| Crear programas completos con bloques | Sí: eventos + control + datos |
| IF/ELSE | `if`, `else_if`, `else` anidados |
| AND/OR/NOT | reporteros booleanos |
| Bucles y repeticiones | `repeat`, `forever`, `while`, `until` |
| Variables | crear, borrar, fijar, cambiar, leer |
| Comparaciones matemáticas | 6 operadores |
| Anidar bloques | cuerpos de C-blocks |
| Generar código válido | validado con `GsCompiler.Compile` |
| Ejecutar en Combustor | `ScriptRig.Source` + `Start` |
| Guardar/recuperar | JSON + autosave + export |

---

## 9. Plan de implementación

| Fase | Contenido | Estado |
|---|---|---|
| 1 | Investigación y viabilidad | **Completada** (este informe) |
| 2 | Arquitectura, modelo, catálogo de bloques | En curso |
| 3 | Editor: paleta, lienzo, drag&drop, zoom | |
| 4 | Control, operadores, bucles, variables, listas, funciones | |
| 5 | Datos reales de Combustor (partes/sensores/gases) | |
| 6 | Compilador GS + validación + panel de código | |
| 7 | Guardado/carga, autosave, duplicar/pegar, undo/redo | |
| 8 | Pruebas in-game, pulido, rendimiento | |

---

## 10. Conclusión

El enfoque es **viable y sólido**. Combustor ya tiene el lenguaje, el compilador, la
VM y el modelo de guardado; el mod solo aporta la capa visual y el traductor. Eso
reduce el riesgo, garantiza compatibilidad con el juego y cumple el requisito de no
crear un lenguaje independiente.

La única decisión de diseño relevante es **no usar Blockly** y construir el editor
en IMGUI, integrándolo con la estética y el input del juego.

---

### Anexo: referencia del lenguaje (extraída de `Localization/en.full.csv`)

El juego documenta cada keyword, builtin y miembro de parte en las claves `script.*`.
Ese texto es la fuente de verdad para las etiquetas y ayudas del editor, y se puede
mostrar en el panel de referencia del mod. Ejemplos:

- `script.kw.while`: «Repeats the indented lines below it for as long as the condition
  is true. Each pass takes one tick of the simulation.»
- `script.fn.wait`: «Pauses the script for this many seconds of simulation time.»
- `script.m.sensor.temperature`: «Temperature in the cell, in kelvin.»
- `script.err.not_allowed`: «{0} cannot be worked here: you could not work it by hand
  at this moment either.»

---

# Addendum de implementación (2026-11-17)

## Resultado

El enfoque «bloques → GS → compilador/VM del juego» se confirmó como válido y se
implementó por completo. La única desviación respecto al plan inicial es la
**superficie de interfaz**: el editor no se dibuja dentro de la ventana de Unity,
sino en una página web local servida por el mod.

## Por qué cambió la interfaz

La Fase 1 asumió que se podía usar IMGUI dentro del juego. La implementación en
la build 26701522 encontró tres obstáculos reales:

1. `MelonMod.OnGUI` no se invoca en esta combinación MelonLoader 0.7.3 + Unity
   6.3 IL2CPP.
2. Un postfix de Harmony en `GasSimHud.OnGUI` / `DrawOverlay` provoca un crash
   nativo de coreclr (`0xc0000005`) al entrar en Sandbox.
3. Un MonoBehaviour inyectado con `ClassInjector` y `OnGUI` también termina en
   crash nativo, incluso con un cuerpo vacío.

El mod de referencia evita esos caminos: parchea métodos hoja concretos
(`GasSimInput.HandleKeys`, `GasSimHud.DrawPauseMenu`, `GasSimRenderer.DrawGenerators`)
y dibuja en el menú o en el buffer de píxeles, no una ventana IMGUI completa.

La solución estable es un **editor web local** (Blockly 13.4.0) servido por un
`TcpListener` en `127.0.0.1:8765`, con una API JSON que marshalea las acciones al
hilo principal de Unity. El juego sigue ejecutándose en segundo plano.

Esto no crea un lenguaje nuevo: el generador JavaScript produce **GS**, y el mod
lo valida con `GsCompiler.Compile` y lo ejecuta con `ScriptRig.Start(sim)`, la
misma ruta que usa el botón Run del juego.

## Validación realizada

| Prueba | Resultado |
|---|---|
| `/api/health`, `/api/connect` | OK; 29 gases en Sandbox |
| Programa con variables, listas, `for`, `while`, `if`, funciones y `print` | `GsCompiler.Compile` OK; salida `hola desde JS` ×3 |
| Eventos + `__cb_pow(2,3)` + `__cb_random` + `__cb_stop` + `stop()` | Log `8`, `2`, `recibido`, `adios`; sin errores |
| Parte inexistente | `Error línea 13: Nothing is called 'sensor_1'.` |
| Guardar / abrir | `UserData/CombustorBlocks/project.json` |
| UI Blockly | Categorías, bloques, zoom, código y salida sin errores de consola |
| Origen de API | `Origin` externo → 403; local → 200 |
| Pausa por pérdida de foco | `paused=true`; `/api/resume` lo desbloquea; el script continúa en segundo plano |
| Idioma ES/EN | Auto → español con navegador `es`; selector EN → «Run», «When program starts», Blockly `if` |


### Asistente IA

Se añadió una pestaña **IA** con dos capas:

1. **Asistente integrado** (sin conexión): plantillas de temperatura, válvula,
   contador y aleatorio; explicación de errores del compilador/runtime;
   explicación del bloque seleccionado.
2. **Modelo externo opcional**: el navegador envía los mensajes a `/api/ai`; el
   mod hace la petición HTTP (Ollama o OpenAI-compatible) desde su servidor
   local, evitando CORS. La API key es opcional y se guarda en `localStorage`.

Los programas generados se insertan como un bloque **Código GS (avanzado)**
(`cb_gs_code`), se validan con `GsCompiler.Compile` y se ejecutan con
`ScriptRig`. La inserción reemplaza el programa actual y se puede deshacer.

Validación: el asistente integrado generó un control de temperatura, se insertó
como bloque GS, el código pasó `GsCompiler.Compile` (`ok=true`) y el error
`Nothing is called 'sensor_1'` se explicó correctamente. Un endpoint externo
inalcanzable devolvió un error claro y el editor cayó al asistente integrado.

### Idioma (ES/EN)

Se añadió un selector **Auto / ES / EN** y detección automática. El modo Auto
usa el idioma del navegador; si no es español o inglés, consulta el idioma del
juego con `/api/lang` (`L.Language`). Blockly carga sus mensajes de `es.js` para
español o los mensajes ingleses incluidos. Se tradujeron etiquetas y tooltips de
los bloques personalizados, categorías, botones, ayuda, panel de IA, errores y
mensajes de estado. La elección se guarda en `localStorage`; al cambiarla se
recarga la página y se conserva el proyecto.

Validación: con navegador `es` y Auto, la UI mostró «Conectar / Ejecutar» y
Blockly «si»; al forzar EN mostró «Connect / Run», «When program starts» y
Blockly «if». El panel de ayuda y el asistente IA cambiaron con el idioma.

## Archivos entregados

- `CombustorBlocks-v0.1.0.zip`
  - `Mods/CombustorBlocks.dll`
  - `UserData/CombustorBlocks/web/*` (Blockly + editor)
  - `Abrir editor.url` / `.bat`
  - `README.md`, `INSTALL.txt`, licencias
- Código fuente en `CombustorBlocks/`.
- Este informe y `README.md`.

## Limitaciones que permanecen

Las mismas de la Fase 1: no hay evento real de fin de simulación; «Al detener»
solo se ejecuta desde el bloque Detener programa; los eventos son cooperativos
(una comprobación por tick); los sensores deben colocarse con Place sensor; una
script por motor; el editor requiere un navegador en la misma máquina.
