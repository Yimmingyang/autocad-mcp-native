using System.IO.Pipes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(AutoCadMcpPlugin.Entry))]
namespace AutoCadMcpPlugin;

/// <summary>
/// AutoCAD 2025 in-process MCP bridge (v0.5.5).
/// One named pipe, many native operations, self-describing registry.
/// </summary>
public sealed class Entry : IExtensionApplication
{
#if CAD2020
    private const string PipeName = "AutoCAD_MCP_2020_V2";
#elif CAD2021
    private const string PipeName = "AutoCAD_MCP_2021_V2";
#elif CAD2022
    private const string PipeName = "AutoCAD_MCP_2022_V2";
#elif CAD2023
    private const string PipeName = "AutoCAD_MCP_2023_V2";
#elif CAD2024
    private const string PipeName = "AutoCAD_MCP_2024_V2";
#elif CAD2026
    private const string PipeName = "AutoCAD_MCP_2026_V2";
#else
    private const string PipeName = "AutoCAD_MCP_2025_V2";
#endif
    private const string Version = "0.5.5";
    private const int PipeInstances = 4;
    private const double CoordinateLimit = 1e7;

    private static CancellationTokenSource? _shutdown;
    private static Task? _listener;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan CommandContextTimeout = TimeSpan.FromSeconds(110);
    private static readonly Dictionary<string, (string Drawing, string Operations)> Plans = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, DateTime> PlanCreated = new(StringComparer.Ordinal);

    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ---------------------------------------------------------------- lifecycle

    public void Initialize()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("unhandled", e.ExceptionObject as System.Exception);
        Application.Idle += StartOnIdle;
    }

    public void Terminate()
    {
        Application.Idle -= StartOnIdle;
        Stop();
    }

    private static void StartOnIdle(object? sender, EventArgs e)
    {
        Application.Idle -= StartOnIdle;
        try { BeginListening(); } catch (System.Exception ex) { Log("start", ex); }
    }

    private static void Log(string where, System.Exception? ex)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "AutoCadMcp");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "bridge.log"), $"[{DateTime.Now:O}] {where}: {ex}\r\n");
        }
        catch { }
    }

    private static System.Threading.Timer? _watchdog;

    private static void BeginListening()
    {
        if (_listener is { IsCompleted: false }) return;
        _shutdown = new CancellationTokenSource();
        _listener = Task.WhenAll(Enumerable.Range(0, PipeInstances).Select(_ => ListenLoopAsync(_shutdown.Token)));
        // Supervise: if every listener loop ever ends unexpectedly, bring the bridge back.
        _watchdog ??= new System.Threading.Timer(_ =>
        {
            try { if (_listener is null || _listener.IsCompleted) BeginListening(); }
            catch (System.Exception ex) { Log("watchdog", ex); }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\nAutoCAD MCP bridge v{Version} listening on \\\\.\\pipe\\{PipeName}");
    }

    [CommandMethod("ACMCPSTART")]
    public void Start()
    {
        if (_listener is { IsCompleted: false })
        {
            Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nAutoCAD MCP bridge is already running.");
            return;
        }
        BeginListening();
    }

    [CommandMethod("ACMCPSTOP")]
    public void Stop()
    {
        var shutdown = _shutdown;
        var listener = _listener;
        _shutdown = null;
        if (shutdown is null) return;
        shutdown.Cancel();
        _listener = listener is null ? null : CleanupListenerAsync(listener, shutdown);
    }

    private static async Task CleanupListenerAsync(Task listener, CancellationTokenSource shutdown)
    {
        try { await listener; }
        catch (OperationCanceledException) { }
        catch (System.Exception ex) { Log("stop", ex); }
        finally { shutdown.Dispose(); }
    }

    // ------------------------------------------------------------------- pipes

    private static async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, PipeInstances,
                    PipeTransmissionMode.Byte, ServerPipeOptions);
                await pipe.WaitForConnectionAsync(token);
                using var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, false, 4096, true);
                using var writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(false), 4096, true) { AutoFlush = true };
                var line = await reader.ReadLineAsync().WaitAsync(token);
                if (line is null) continue;
                var response = await HandleRequestAsync(line);
                await writer.WriteLineAsync(response);
                await writer.FlushAsync();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (System.Exception ex)
            {
                Log("pipe", ex);
                try { await Task.Delay(200, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
        }
    }

#if NET6_0_OR_GREATER
    private const PipeOptions ServerPipeOptions = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
#else
    private const PipeOptions ServerPipeOptions = PipeOptions.Asynchronous;
#endif

    // ---------------------------------------------------------------- dispatch

    private static async Task<string> HandleRequestAsync(string line)
    {
        string? id = null;
        string tool = "";
        try
        {
            using var request = JsonDocument.Parse(line);
            var root = request.RootElement;
            id = root.GetProperty("id").GetString();
            tool = root.GetProperty("tool").GetString() ?? "";
            var args = root.TryGetProperty("args", out var a) ? a : default;

            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc is null && !DocumentFreeOps.Contains(tool))
                return Fail(id, "No active AutoCAD drawing. Open a DWG and retry.");

            var mainHwnd = Application.MainWindow?.Handle ?? IntPtr.Zero;
            if (mainHwnd != IntPtr.Zero && !IsWindowEnabled(mainHwnd))
                return Fail(id, "AutoCAD has a modal dialog open (file dialog / drawing recovery / alert). Close it and retry.");

            var op = Ops.TryGetValue(tool, out var found) ? found : null;
            object? result = null;
            string? commandError = null;
            await Gate.WaitAsync();
            try
            {
                if (op is { OutsideContext: true } || DocumentFreeOps.Contains(tool))
                {
                    try { result = op.Run(args); }
                    catch (System.Exception ex) { commandError = ex.Message; }
                }
                else
                {
                    var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _ = Application.DocumentManager.ExecuteInCommandContextAsync(_ =>
                    {
                        try { result = Execute(tool, args); }
                        catch (System.Exception ex) { commandError = ex.Message; }
                        completion.TrySetResult(true);
                        return Task.CompletedTask;
                    }, null);
                    await completion.Task.WaitAsync(CommandContextTimeout);
                }
            }
            finally { Gate.Release(); }
            if (commandError is not null) return Fail(id, commandError);
            return JsonSerializer.Serialize(new { id, ok = true, data = result });
        }
        catch (TimeoutException)
        {
            return Fail(id, "AutoCAD did not enter a command context within 110 s; the operation may have completed. Inspect the drawing before retrying.");
        }
        catch (System.Exception ex)
        {
            Log($"tool {tool}", ex);
            return Fail(id, ex.Message);
        }
    }

    private static string Fail(string? id, string message) =>
        JsonSerializer.Serialize(new { id, ok = false, error = message });

    private static readonly HashSet<string> DocumentFreeOps = new(StringComparer.OrdinalIgnoreCase)
    { "get_bridge_status", "list_documents", "list_tools", "get_sysvar", "new_drawing", "open_drawing" };

    // ------------------------------------------------------------ op registry

    private sealed class Op
    {
        public string Name { get; }
        public string Description { get; }
        public string Params { get; }
        public bool Mutates { get; }
        public Func<JsonElement, object> Run { get; }
        public bool OutsideContext { get; }
        public Op(string name, string description, string parameters, bool mutates, Func<JsonElement, object> run, bool outsideContext = false)
        { Name = name; Description = description; Params = parameters; Mutates = mutates; Run = run; OutsideContext = outsideContext; }
    }

    private static readonly Dictionary<string, Op> Ops = BuildRegistry();

    private static object Execute(string tool, JsonElement args)
    {
        if (Ops.TryGetValue(tool, out var op)) return op.Run(args);
        throw new ArgumentException($"Unknown tool '{tool}'. Call list_tools for the native operation catalogue.");
    }

    private static Dictionary<string, Op> BuildRegistry()
    {
        var d = new Dictionary<string, Op>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string description, string @params, bool mutates, Func<JsonElement, object> run, bool outside = false)
            => d[name] = new Op(name, description, @params, mutates, run, outside);

        // ---- meta / document -------------------------------------------------
        Add("get_bridge_status", "Plugin version, pipe, active drawing, units, read-only flag, AutoCAD version, document count.", "{}", false, _ => BridgeStatus());
        Add("list_tools", "Native operation catalogue with JSON parameter hints.", "{}", false, _ => Ops.Values.OrderBy(o => o.Name).Select(o => new { name = o.Name, description = o.Description, @params = o.Params, mutates = o.Mutates }).ToArray());
        Add("list_documents", "Open documents and which one is active.", "{}", false, _ => ListDocuments());
        Add("get_active_drawing", "Active drawing name, path, tile mode, read-only state.", "{}", false, _ => ActiveDrawing());
        Add("new_drawing", "Create a new drawing (optionally from a template path).", "{template?:string}", true, a => NewDrawing(a));
        Add("open_drawing", "Open a DWG; readOnly defaults to false.", "{path:string, readOnly?:bool}", true, a => OpenDrawing(a));
        Add("save_active_drawing", "Save the active drawing to its existing path.", "{}", true, _ => SaveActive());
        Add("save_as", "Save the active drawing under a new DWG path.", "{path:string}", true, a => SaveAs(a));
        Add("export_dxf", "Export the active drawing to DXF.", "{path:string, precision?:int}", true, a => ExportDxf(a));
        Add("create_drawing_copy", "Create a DWG copy of the active drawing without modifying it.", "{path:string}", true, a => CopyDrawing(a));
        Add("close_active_drawing", "Close the active document (save=false discards).", "{save?:bool}", true, a => CloseActive(a));
        Add("zoom_extents", "Zoom the current viewport to drawing extents.", "{}", true, _ => { Doc().Editor.Command("_.ZOOM", "_E"); return new { zoomed = true }; });
        Add("undo", "Undo the last operation(s).", "{count?:int}", true, a => UndoRedo(a, true));
        Add("redo", "Redo undone operation(s).", "{count?:int}", true, a => UndoRedo(a, false));

        // ---- drawing info ----------------------------------------------------
        Add("drawing_summary", "Units, limits, extents, layer count, entity counts, current layer/layout, key system variables.", "{}", false, _ => Summary());
        Add("list_layers", "All layers with colour, linetype, lineweight, off/frozen/locked/plottable state.", "{}", false, _ => Layers());
        Add("layer_create", "Create a layer (colour index 1-255, optional linetype/lineweight).", "{name:string, colorIndex?:int, linetype?:string, lineweight?:int}", true, a => LayerCreate(a));
        Add("layer_set", "Change layer state: current/off/on/freeze/thaw/lock/unlock/plottable/colorIndex/linetype/lineweight/description.", "{name:string, current?:bool, off?:bool, frozen?:bool, locked?:bool, plottable?:bool, colorIndex?:int, linetype?:string, lineweight?:int, description?:string}", true, a => LayerSet(a));
        Add("layer_delete", "Delete a layer (must be empty and not current).", "{name:string}", true, a => LayerDelete(a));
        Add("count_modelspace_entities", "Count model-space entities by DXF type.", "{limit?:int}", false, a => CountEntities(a));
        Add("list_modelspace_entities", "List model-space handles/types/layers, optionally filtered by type or layer.", "{limit?:int, type?:string, layer?:string}", false, a => ListEntities(a));
        Add("inspect_drawing_geometry", "Full geometry dump (lines, arcs, circles, polylines with bulges, ellipse, spline control points, text, dimensions, hatch).", "{limit?:int, includeSplines?:bool}", false, a => Inspect(a));
        Add("entity_info", "Every readable property of one entity by handle.", "{handle:string}", false, a => EntityInfo(a));
        Add("select_entities", "Select handles by type/layer/colour/text filter (whole model space).", "{type?:string, layer?:string, colorIndex?:int, text?:string, limit?:int}", false, a => SelectEntities(a));
        Add("measure", "Length of a curve, area of a closed polyline/circle/hatch, or drawing extents.", "{handle?:string}", false, a => Measure(a));

        // ---- draw ------------------------------------------------------------
        Add("draw_line", "Draw a line.", "{start:point, end:point, layer?:string}", true, a => DrawLine(a));
        Add("draw_circle", "Draw a circle.", "{center:point, radius:number, layer?:string}", true, a => DrawCircle(a));
        Add("draw_arc", "Draw an arc (angles in degrees, CCW).", "{center:point, radius:number, startAngle:number, endAngle:number, layer?:string}", true, a => DrawArc(a));
        Add("draw_ellipse", "Draw an ellipse (majorRadius, radiusRatio 0..1).", "{center:point, majorRadius:number, radiusRatio:number, rotation?:number, layer?:string}", true, a => DrawEllipse(a));
        Add("draw_polyline", "Draw a 2D polyline with optional bulges.", "{points:point[], closed?:bool, bulges?:number[], layer?:string}", true, a => DrawPolyline(a));
        Add("draw_rectangle", "Draw a closed rectangular polyline from opposite corners.", "{x1:number, y1:number, x2:number, y2:number, layer?:string}", true, a => DrawRectangle(a));
        Add("draw_point", "Draw a point entity.", "{position:point, layer?:string}", true, a => DrawPoint(a));
        Add("draw_text", "Draw single-line text; style may name a text style (use a CJK TrueType style for Chinese).", "{position:point, text:string, height:number, rotation?:number, layer?:string, style?:string}", true, a => DrawText(a));
        Add("draw_mtext", "Draw multi-line text with wrap width; style may name a text style.", "{position:point, text:string, height:number, width:number, rotation?:number, layer?:string, style?:string}", true, a => DrawMText(a));
        Add("draw_hatch", "Hatch a closed boundary by entity handle, or a polygon of points.", "{pattern:string, handle?:string, points?:point[], scale?:number, angle?:number, colorIndex?:int, layer?:string}", true, a => DrawHatch(a));
        Add("draw_dimension", "Dimension: kind=linear|aligned|angular|radial|diametric.", "{kind:string, start:point, end:point, dimLine:point, text?:string, radius?:number, layer?:string}", true, a => DrawDimension(a));

        // ---- edit ------------------------------------------------------------
        Add("move_entity", "Move an entity by a delta.", "{handle:string, dx:number, dy:number, dz?:number}", true, a => Transform(a, "move"));
        Add("copy_entity", "Copy an entity by a delta; returns the new handle.", "{handle:string, dx:number, dy:number, dz?:number}", true, a => Transform(a, "copy"));
        Add("rotate_entity", "Rotate an entity about a base point (degrees).", "{handle:string, basePoint:point, angle:number}", true, a => Transform(a, "rotate"));
        Add("scale_entity", "Scale an entity about a base point.", "{handle:string, basePoint:point, factor:number}", true, a => Transform(a, "scale"));
        Add("mirror_entity", "Mirror an entity about a line (two points); keeps the source by default.", "{handle:string, p1:point, p2:point, eraseSource?:bool}", true, a => Transform(a, "mirror"));
        Add("offset_entity", "Offset a curve; returns offset handles.", "{handle:string, distance:number}", true, a => OffsetEntity(a));
        Add("array_entity", "Rectangular array (rows x cols, spacing).", "{handle:string, rows:int, cols:int, rowSpacing:number, colSpacing:number}", true, a => ArrayEntity(a));
        Add("set_entity_layer", "Move an entity to an existing layer.", "{handle:string, layer:string}", true, a => (object)SetEntity(a, "layer"));
        Add("set_entity_property", "Set layer/colorIndex/trueColor/linetype/lineweight/visible on an entity.", "{handle:string, layer?:string, colorIndex?:int, trueColor?:string, linetype?:string, lineweight?:int, visible?:bool}", true, a => SetEntity(a, null));
        Add("delete_entity", "Erase one entity by handle.", "{handle:string}", true, a => DeleteEntity(a));

        // ---- blocks / attributes / xref -------------------------------------
        Add("list_blocks", "Local block definitions usable for insertion.", "{}", false, _ => ListBlocks());
        Add("block_create", "Create a block definition from line/circle/rectangle/text entities.", "{name:string, entities:object[]}", true, a => BlockCreate(a));
        Add("block_insert", "Insert a block definition with scale and rotation.", "{name:string, position:point, scale?:number, rotation?:number, layer?:string}", true, a => BlockInsert(a));
        Add("create_block", "Alias of block_create.", "{name:string, entities:object[]}", true, a => BlockCreate(a));
        Add("insert_block", "Alias of block_insert.", "{name:string, position:point, scale?:number, rotation?:number, layer?:string}", true, a => BlockInsert(a));
        Add("block_attributes", "List a block reference's attributes (tag/value).", "{handle:string}", false, a => BlockAttributes(a));
        Add("set_block_attribute", "Set an attribute value on a block reference.", "{handle:string, tag:string, value:string}", true, a => SetBlockAttribute(a));
        Add("block_explode", "Explode a block reference; returns created handles.", "{handle:string}", true, a => BlockExplode(a));
        Add("xref_attach", "Attach an external DWG reference.", "{path:string, name:string, insertPoint?:point}", true, a => XrefAttach(a));

        // ---- system variables / escape hatches -------------------------------
        Add("text_style_create", "Create a text style; typeface is a TrueType family (default SimSun) so Chinese renders instead of '?'.", "{name:string, typeface?:string, bigFont?:string, current?:bool}", true, a => TextStyleCreate(a));
        Add("list_text_styles", "List text styles with typeface / big font so callers can pick a CJK-capable one.", "{}", false, _ => ListTextStyles());
        Add("extract_parameters", "Read EVERY parameter in one call: dimensions (value + text override), dimension callout texts, all notes, title-block texts, holes, arcs, layers, blocks, text styles and the drawing scale derived from dimension definition points. Use this before any geometry work so nothing is missed.", "{}", false, _ => ExtractParameters());
        Add("compare_geometry", "Entity-level diff of the active drawing against another DWG/DXF-backed drawing, optionally limited to a region. Pair entities by type and normalised geometry; returns counts and samples of differences.", "{path:string, x0?:number, y0?:number, x1?:number, y1?:number}", false, a => CompareGeometry(a));
        Add("get_sysvar", "Read one AutoCAD system variable.", "{name:string}", false, a => new { name = Str(a, "name"), value = SysVar(Str(a, "name")) });
        Add("set_sysvar", "Write one AutoCAD system variable.", "{name:string, value:object}", true, a => SetSysVar(a));
        Add("send_command", "Run an AutoCAD command synchronously; pass each prompt answer in args, e.g. {command:\"_.CIRCLE\",args:[\"10,10\",\"5\"]}.", "{command:string, args?:string[]}", true, a => SendCommand(a));
        Add("cancel_command", "Send Escape to clear a pending command prompt so the bridge keeps responding.", "{}", true, _ => CancelCommand());

        // ---- transactional plans --------------------------------------------
        Add("preview_drawing_plan", "Validate up to 100 operations without touching the drawing; returns planId.", "{operations:object[]}", false, a => PreviewPlan(a));
        Add("apply_drawing_plan", "Apply a previewed plan in one transaction (rolls back on any error).", "{planId:string}", true, a => ApplyPlan(a));

        return d;
    }

    // ------------------------------------------------------------- doc helpers

    private static Document Doc() => Application.DocumentManager.MdiActiveDocument
        ?? throw new InvalidOperationException("No active AutoCAD drawing.");

    private static Database Db() => Doc().Database;

    private static double Num(JsonElement a, string name, double? fallback = null)
    {
        if (!a.TryGetProperty(name, out var e))
        {
            if (fallback.HasValue) return fallback.Value;
            throw new ArgumentException($"Missing required number: {name}");
        }
        var v = e.GetDouble();
        if (double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v) > CoordinateLimit)
            throw new ArgumentOutOfRangeException(name, $"{name} must be finite and within ±{CoordinateLimit} drawing units.");
        return v;
    }

    private static int ClampInt(int value, int min, int max) => value < min ? min : value > max ? max : value;

    private static int Int(JsonElement a, string name, int fallback) =>
        a.TryGetProperty(name, out var e) ? e.GetInt32() : fallback;

    private static string Str(JsonElement a, string name)
    {
        var v = a.TryGetProperty(name, out var e) ? e.GetString()?.Trim() : null;
        return string.IsNullOrEmpty(v) ? throw new ArgumentException($"{name} must not be empty.") : v;
    }

    private static string? OptStr(JsonElement a, string name) =>
        a.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static bool Bool(JsonElement a, string name, bool fallback = false) =>
        a.TryGetProperty(name, out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : fallback;

    private static Point3d Point(JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out var p) || p.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"Missing required point: {key}");
        return new Point3d(Num(p, "x"), Num(p, "y"), Num(p, "z", 0));
    }

    private static Point3d? OptPoint(JsonElement args, string key) =>
        args.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Object ? Point(args, key) : null;

    private static string TextStyleName(Transaction tr, ObjectId id)
    {
        try { return ((TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name; }
        catch { return id.Handle.ToString(); }
    }

    private static object P(Point3d p) => new { x = p.X, y = p.Y, z = p.Z };

    private static object P2(Point2d p) => new { x = p.X, y = p.Y, z = 0.0 };

    private static double Len(Curve c) => c.GetDistanceAtParameter(c.EndParam);

    private static bool TryExtents(Entity e, out Point3d min, out Point3d max)
    {
        try
        {
            var ext = e.GeometricExtents;
            min = ext.MinPoint;
            max = ext.MaxPoint;
            return true;
        }
        catch
        {
            min = Point3d.Origin;
            max = Point3d.Origin;
            return false;
        }
    }

    private static ObjectId ByHandle(Transaction tr, Database db, string handleText)
    {
        if (!long.TryParse(handleText, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var raw))
            throw new ArgumentException("handle must be a hexadecimal AutoCAD entity handle.");
        var id = db.GetObjectId(false, new Handle(raw), 0);
        if (id.IsNull || id.IsErased) throw new ArgumentException($"No live entity found for handle {handleText}.");
        return id;
    }

    private static ObjectId ModelSpace(Transaction tr, Database db)
    {
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        return bt[BlockTableRecord.ModelSpace];
    }

    private static void Append(Transaction tr, Database db, Entity e)
    {
        var ms = (BlockTableRecord)tr.GetObject(ModelSpace(tr, db), OpenMode.ForWrite);
        ms.AppendEntity(e);
        tr.AddNewlyCreatedDBObject(e, true);
    }

    private static void ApplyLayer(Entity e, JsonElement a)
    {
        var layer = OptStr(a, "layer");
        if (!string.IsNullOrEmpty(layer)) e.Layer = layer;
    }

    private static object HandleOf(Entity e) => new { handle = e.Handle.ToString(), type = e.GetRXClass().DxfName, layer = e.Layer };

    // --------------------------------------------------------------- meta ops

    private static object BridgeStatus()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        var db = doc?.Database;
        return new
        {
            plugin = "autocad-mcp-native",
            version = Version,
            pipe = @"\\.\pipe\" + PipeName,
            acadVersion = Application.Version.ToString(),
            processId = Process.GetCurrentProcess().Id,
            documentCount = Application.DocumentManager.Count,
            drawing = doc is null ? null : Path.GetFileName(doc.Name),
            path = doc?.Name,
            readOnly = doc?.IsReadOnly,
            units = db?.Insunits.ToString(),
            toolCount = Ops.Count,
            instances = PipeInstances
        };
    }

    private static object ListDocuments() => new
    {
        active = Path.GetFileName(Application.DocumentManager.MdiActiveDocument?.Name ?? ""),
        documents = Application.DocumentManager.Cast<Document>().Select(x => new
        {
            name = Path.GetFileName(x.Name),
            path = x.Name,
            readOnly = x.IsReadOnly,
            active = ReferenceEquals(x, Application.DocumentManager.MdiActiveDocument)
        }).ToArray()
    };

    private static object ActiveDrawing()
    {
        var doc = Doc();
        return new { name = Path.GetFileName(doc.Name), path = doc.Name, tileMode = doc.Database.TileMode, readOnly = doc.IsReadOnly };
    }

    private static object NewDrawing(JsonElement a)
    {
        var template = OptStr(a, "template");
        var doc = string.IsNullOrEmpty(template) || !File.Exists(template)
            ? Application.DocumentManager.Add("")
            : Application.DocumentManager.Add(template);
        return new { created = Path.GetFileName(doc.Name), template = template ?? "" };
    }

    private static object OpenDrawing(JsonElement a)
    {
        var path = Path.GetFullPath(Str(a, "path"));
        if (!File.Exists(path)) throw new FileNotFoundException("Drawing file not found.", path);
        var readOnly = Bool(a, "readOnly");
        var opened = Application.DocumentManager.Open(path, readOnly);
        Application.DocumentManager.MdiActiveDocument = opened;
        return new { name = Path.GetFileName(opened.Name), path = opened.Name, readOnly };
    }

    private static object SaveActive()
    {
        var doc = Doc();
        if (doc.IsReadOnly) throw new InvalidOperationException("The active drawing is read-only; save is not allowed.");
        if (!Path.IsPathRooted(doc.Name))
            throw new InvalidOperationException("The active drawing has never been saved. Use save_as with an absolute .dwg path first.");
        var db = doc.Database;
        db.SaveAs(doc.Name, true, DwgVersion.Current, db.SecurityParameters);
        return new { saved = doc.Name, readOnly = false };
    }

    private static object SaveAs(JsonElement a)
    {
        var path = Path.GetFullPath(Str(a, "path"));
        if (!string.Equals(Path.GetExtension(path), ".dwg", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("path must end in .dwg");
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) throw new DirectoryNotFoundException("Output directory must exist.");
        if (File.Exists(path)) throw new IOException("Refusing to overwrite an existing DWG; choose a new path.");
        var doc = Doc();
        var db = doc.Database;
        db.SaveAs(path, true, DwgVersion.Current, db.SecurityParameters);
        return new { saved = path, source = doc.Name };
    }

    private static object ExportDxf(JsonElement a)
    {
        var path = Path.GetFullPath(Str(a, "path"));
        if (!string.Equals(Path.GetExtension(path), ".dxf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("path must end in .dxf");
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) throw new DirectoryNotFoundException("Output directory must exist.");
        var precision = Int(a, "precision", 16);
        var db = Db();
        db.DxfOut(path, precision, DwgVersion.Current);
        var info = new FileInfo(path);
        return new { exported = path, bytes = info.Exists ? info.Length : 0, precision };
    }

    private static object CopyDrawing(JsonElement a)
    {
        var path = Path.GetFullPath(Str(a, "path"));
        if (!string.Equals(Path.GetExtension(path), ".dwg", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("path must end in .dwg");
        if (File.Exists(path)) throw new IOException("Refusing to overwrite an existing DWG copy.");
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) throw new DirectoryNotFoundException("Output directory must exist.");
        var doc = Doc();
        var db = doc.Database;
        using (var cloned = db.Wblock()) cloned.SaveAs(path, DwgVersion.Current);
        return new { source = doc.Name, copy = path, sourceUnits = db.Insunits.ToString(), sourceUnchanged = true };
    }

    private static Action? _pendingClose;

    private static object CloseActive(JsonElement a)
    {
        var doc = Doc();
        var save = Bool(a, "save");
        var name = doc.Name;
        var target = Doc();
        _pendingClose = () =>
        {
            try { if (save) target.CloseAndSave(name); else target.CloseAndDiscard(); }
            catch (System.Exception ex) { Log("close", ex); }
        };
        Application.Idle += RunPendingClose;
        return new { queued = true, closed = name, saved = save, note = "closing on the next AutoCAD idle" };
    }

    private static void RunPendingClose(object? sender, EventArgs e)
    {
        Application.Idle -= RunPendingClose;
        var pending = _pendingClose;
        _pendingClose = null;
        pending?.Invoke();
    }

    private static object UndoRedo(JsonElement a, bool undo)
    {
        var count = ClampInt(Int(a, "count", 1), 1, 20);
        var editor = Doc().Editor;
        for (var i = 0; i < count; i++) editor.Command(undo ? "_U" : "_REDO");
        return new { operation = undo ? "undo" : "redo", count };
    }

    // ------------------------------------------------------------ info ops

    private static object Summary()
    {
        var doc = Doc();
        var db = doc.Database;
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (ObjectId id in ms)
            if (tr.GetObject(id, OpenMode.ForRead, false) is Entity e)
                counts[e.GetRXClass().DxfName] = counts.TryGetValue(e.GetRXClass().DxfName, out var c) ? c + 1 : 1;
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        var layerName = tr.GetObject(db.Clayer, OpenMode.ForRead) is LayerTableRecord ltr ? ltr.Name : "";
        return new
        {
            drawing = Path.GetFileName(doc.Name),
            path = doc.Name,
            units = db.Insunits.ToString(),
            limits = new { min = P2(db.Limmin), max = P2(db.Limmax) },
            extents = ComputedExtents(tr, ms),
            entityCount = counts.Values.Sum(),
            entityTypes = counts,
            layerCount = layers.Cast<ObjectId>().Count(),
            currentLayer = layerName,
            currentSpace = db.CurrentSpaceId.ObjectClass.DxfName,
            sysvars = new { OSMODE = SysVar("OSMODE"), LTSCALE = SysVar("LTSCALE"), DIMSCALE = SysVar("DIMSCALE"), INSUNITS = SysVar("INSUNITS"), MEASUREMENT = SysVar("MEASUREMENT") }
        };
    }

    private static object? ComputedExtents(Transaction tr, BlockTableRecord ms)
    {
        var min = new Point3d(double.MaxValue, double.MaxValue, 0);
        var max = new Point3d(double.MinValue, double.MinValue, 0);
        var any = false;
        foreach (ObjectId id in ms)
            if (tr.GetObject(id, OpenMode.ForRead, false) is Entity e && TryExtents(e, out var bmin, out var bmax))
            {
                any = true;
                min = new Point3d(Math.Min(min.X, bmin.X), Math.Min(min.Y, bmin.Y), 0);
                max = new Point3d(Math.Max(max.X, bmax.X), Math.Max(max.Y, bmax.Y), 0);
            }
        return any ? new { min = P(min), max = P(max) } : null;
    }

    private static ObjectId? ResolveTextStyle(Transaction tr, Database db, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        if (!table.Has(name)) throw new ArgumentException($"Text style not found: {name}");
        return table[name];
    }

    private static object TextStyleCreate(JsonElement a)
    {
        var name = Str(a, "name");
        var typeface = OptStr(a, "typeface") ?? "SimSun";
        var bigFont = OptStr(a, "bigFont");
        var db = Db();
        using var tr = db.TransactionManager.StartTransaction();
        var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForWrite);
        var rec = table.Has(name)
            ? (TextStyleTableRecord)tr.GetObject(table[name], OpenMode.ForWrite)
            : new TextStyleTableRecord { Name = name };
        if (!table.Has(name))
        {
            table.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }
        if (!string.IsNullOrEmpty(bigFont))
        {
            rec.Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor("txt", false, false, 0, 0);
            rec.BigFontFileName = bigFont;
        }
        else
        {
            rec.Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor(typeface, false, false, 0, 34);
        }
        if (Bool(a, "current")) db.Textstyle = rec.ObjectId;
        tr.Commit();
        return new { name, typeface, bigFont = bigFont ?? "", current = Bool(a, "current") };
    }

    private static object ListTextStyles()
    {
        var db = Db();
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        var list = new List<object>();
        foreach (ObjectId id in table)
        {
            var rec = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
            list.Add(new
            {
                name = rec.Name,
                fileName = rec.FileName,
                bigFont = rec.BigFontFileName,
                current = id == db.Textstyle
            });
        }
        return new { count = list.Count, styles = list };
    }

    private static object Layers()
    {
        var db = Db();
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        var list = new List<object>();
        foreach (ObjectId id in table)
        {
            var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
            list.Add(new
            {
                name = l.Name,
                colorIndex = l.Color?.ColorIndex ?? 0,
                trueColor = l.Color is null ? null : $"#{l.Color.ColorValue.R:X2}{l.Color.ColorValue.G:X2}{l.Color.ColorValue.B:X2}",
                linetype = ((LinetypeTableRecord)tr.GetObject(l.LinetypeObjectId, OpenMode.ForRead)).Name,
                lineweight = (int)l.LineWeight,
                isOff = l.IsOff,
                isFrozen = l.IsFrozen,
                isLocked = l.IsLocked,
                isPlottable = l.IsPlottable,
                current = l.ObjectId == db.Clayer,
                description = l.Description
            });
        }
        return new { count = list.Count, layers = list };
    }

    private static object LayerCreate(JsonElement a)
    {
        var name = Str(a, "name");
        var db = Db();
        using var tr = db.TransactionManager.StartTransaction();
        var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
        if (table.Has(name)) throw new ArgumentException($"Layer already exists: {name}");
        var rec = new LayerTableRecord { Name = name };
        if (a.TryGetProperty("colorIndex", out var ci)) rec.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)ClampInt(ci.GetInt32(), 1, 255));
        var lt = OptStr(a, "linetype");
        if (!string.IsNullOrEmpty(lt))
        {
            var lts = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            if (!lts.Has(lt)) throw new ArgumentException($"Linetype not found: {lt}");
            rec.LinetypeObjectId = lts[lt];
        }
        if (a.TryGetProperty("lineweight", out var lw)) rec.LineWeight = (LineWeight)lw.GetInt32();
        table.Add(rec);
        tr.AddNewlyCreatedDBObject(rec, true);
        tr.Commit();
        return new { created = name };
    }

    private static object LayerSet(JsonElement a)
    {
        var name = Str(a, "name");
        var db = Db();
        using var tr = db.TransactionManager.StartTransaction();
        var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (!table.Has(name)) throw new ArgumentException($"Layer not found: {name}");
        var rec = (LayerTableRecord)tr.GetObject(table[name], OpenMode.ForWrite);
        if (a.TryGetProperty("off", out var off)) rec.IsOff = off.GetBoolean();
        if (a.TryGetProperty("frozen", out var frz)) rec.IsFrozen = frz.GetBoolean();
        if (a.TryGetProperty("locked", out var lck)) rec.IsLocked = lck.GetBoolean();
        if (a.TryGetProperty("plottable", out var plt)) rec.IsPlottable = plt.GetBoolean();
        if (a.TryGetProperty("colorIndex", out var ci)) rec.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)ClampInt(ci.GetInt32(), 1, 255));
        var lt = OptStr(a, "linetype");
        if (!string.IsNullOrEmpty(lt))
        {
            var lts = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            if (!lts.Has(lt)) throw new ArgumentException($"Linetype not found: {lt}");
            rec.LinetypeObjectId = lts[lt];
        }
        if (a.TryGetProperty("lineweight", out var lw)) rec.LineWeight = (LineWeight)lw.GetInt32();
        var desc = OptStr(a, "description");
        if (desc is not null) rec.Description = desc;
        var makeCurrent = Bool(a, "current");
        if (makeCurrent) db.Clayer = rec.ObjectId;
        tr.Commit();
        return new { updated = name, current = makeCurrent };
    }

    private static object LayerDelete(JsonElement a)
    {
        var name = Str(a, "name");
        var db = Db();
        using var tr = db.TransactionManager.StartTransaction();
        var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (!table.Has(name)) throw new ArgumentException($"Layer not found: {name}");
        var id = table[name];
        if (id == db.Clayer) throw new InvalidOperationException("Cannot delete the current layer.");
        var rec = (LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite);
        rec.Erase();
        tr.Commit();
        return new { deleted = name };
    }

    // -------------------------------------------------------- entity listing

    private static object CountEntities(JsonElement a)
    {
        var limit = ClampInt(Int(a, "limit", 5000), 1, 20000);
        var db = Db();
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var ms = (BlockTableRecord)tr.GetObject(ModelSpace(tr, db), OpenMode.ForRead);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var inspected = 0;
        foreach (ObjectId id in ms)
        {
            if (inspected++ >= limit) break;
            if (tr.GetObject(id, OpenMode.ForRead, false) is Entity e)
                counts[e.GetRXClass().DxfName] = counts.TryGetValue(e.GetRXClass().DxfName, out var c) ? c + 1 : 1;
        }
        return new { inspected, limit, truncated = inspected >= limit, counts };
    }

    private static object ListEntities(JsonElement a)
    {
        var limit = ClampInt(Int(a, "limit", 1000), 1, 5000);
        var typeFilter = OptStr(a, "type")?.ToUpperInvariant();
        var layerFilter = OptStr(a, "layer");
        var db = Db();
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var ms = (BlockTableRecord)tr.GetObject(ModelSpace(tr, db), OpenMode.ForRead);
        var list = new List<object>();
        foreach (ObjectId id in ms)
        {
            if (list.Count >= limit) break;
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity e) continue;
            var dxf = e.GetRXClass().DxfName;
            if (typeFilter is not null && !string.Equals(dxf, typeFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (layerFilter is not null && !string.Equals(e.Layer, layerFilter, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new { handle = e.Handle.ToString(), type = dxf, layer = e.Layer });
        }
        return new { count = list.Count, limit, entities = list };
    }

    private static object SelectEntities(JsonElement a)
    {
        var limit = ClampInt(Int(a, "limit", 2000), 1, 5000);
        var typeFilter = OptStr(a, "type")?.ToUpperInvariant();
        var layerFilter = OptStr(a, "layer");
        var textFilter = OptStr(a, "text");
        int? colorIndex = a.TryGetProperty("colorIndex", out var ci) ? ci.GetInt32() : null;
        var db = Db();
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var ms = (BlockTableRecord)tr.GetObject(ModelSpace(tr, db), OpenMode.ForRead);
        var list = new List<object>();
        foreach (ObjectId id in ms)
        {
            if (list.Count >= limit) break;
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity e) continue;
            var dxf = e.GetRXClass().DxfName;
            if (typeFilter is not null && !string.Equals(dxf, typeFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (layerFilter is not null && !string.Equals(e.Layer, layerFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (colorIndex.HasValue && (e.Color?.ColorIndex ?? 0) != colorIndex.Value) continue;
            if (textFilter is not null)
            {
                var t = e switch { DBText dt => dt.TextString, MText mt => mt.Contents, _ => null };
                if (t is null || t.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            }
            list.Add(new { handle = e.Handle.ToString(), type = dxf, layer = e.Layer });
        }
        return new { count = list.Count, limit, entities = list };
    }

    private static object EntityInfo(JsonElement a)
    {
        var db = Db();
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var e = (Entity)tr.GetObject(ByHandle(tr, db, Str(a, "handle")), OpenMode.ForRead);
        object? geo = e switch
        {
            Line ln => new { start = P(ln.StartPoint), end = P(ln.EndPoint), length = ln.Length },
            Circle c => new { center = P(c.Center), radius = c.Radius, diameter = c.Diameter, normal = P3(c.Normal) },
            Arc ar => new { center = P(ar.Center), radius = ar.Radius, startAngle = Rad(ar.StartAngle), endAngle = Rad(ar.EndAngle), length = ar.Length },
            Ellipse el => new { center = P(el.Center), majorRadius = el.MajorRadius, minorRadius = el.MinorRadius, radiusRatio = el.RadiusRatio, startAngle = Rad(el.StartAngle), endAngle = Rad(el.EndAngle) },
            Polyline pl => new { closed = pl.Closed, length = pl.Length, area = pl.Closed ? (double?)pl.Area : null, points = Vertices(pl) },
            DBText dt => new { text = dt.TextString, position = P(dt.Position), height = dt.Height, rotation = Rad(dt.Rotation), style = TextStyleName(tr, dt.TextStyleId) },
            MText mt => new { text = mt.Contents, position = P(mt.Location), height = mt.TextHeight, width = mt.Width, rotation = Rad(mt.Rotation) },
            BlockReference br => new { block = BlockName(tr, br), position = P(br.Position), scale = new { x = br.ScaleFactors.X, y = br.ScaleFactors.Y, z = br.ScaleFactors.Z }, rotation = Rad(br.Rotation), attributes = Attrs(br) },
            Hatch h => new { pattern = h.PatternName, area = h.Area, loops = h.NumberOfLoops, scale = h.PatternScale, angle = Rad(h.PatternAngle) },
            Dimension dm => new { measurement = dm.Measurement, text = dm.DimensionText, textPosition = P(dm.TextPosition), styleId = dm.DimensionStyle.ToString() },
            Curve cv => new { length = Len(cv), closed = cv.Closed },
            _ => null
        };
        return new
        {
            handle = e.Handle.ToString(),
            type = e.GetRXClass().DxfName,
            layer = e.Layer,
            colorIndex = e.Color?.ColorIndex ?? 0,
            linetype = e.Linetype,
            lineweight = (int)e.LineWeight,
            visible = e.Visible,
            bounds = TryExtents(e, out var emin, out var emax) ? new { min = P(emin), max = P(emax) } : null,
            geometry = geo
        };
    }

    private static object P3(Vector3d v) => new { x = v.X, y = v.Y, z = v.Z };
    private static double Rad(double radians) => Math.Round(radians * 180.0 / Math.PI, 6);

    private static object[] Vertices(Polyline pl)
    {
        var list = new List<object>();
        for (var i = 0; i < pl.NumberOfVertices; i++)
        {
            var p = pl.GetPoint2dAt(i);
            list.Add(new { x = Math.Round(p.X, 9), y = Math.Round(p.Y, 9), bulge = Math.Round(pl.GetBulgeAt(i), 9) });
        }
        return list.ToArray();
    }

    private static string BlockName(Transaction tr, BlockReference br) =>
        ((BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead)).Name;

    private static object[] Attrs(BlockReference br)
    {
        var list = new List<object>();
        foreach (ObjectId id in br.AttributeCollection)
            if (id.GetObject(OpenMode.ForRead) is AttributeReference ar) list.Add(new { tag = ar.Tag, value = ar.TextString });
        return list.ToArray();
    }

    private static object Inspect(JsonElement a)
    {
        var limit = ClampInt(Int(a, "limit", 2000), 1, 20000);
        var includeSplines = Bool(a, "includeSplines", false);
        var db = Db();
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var ms = (BlockTableRecord)tr.GetObject(ModelSpace(tr, db), OpenMode.ForRead);
        var items = new List<object>();
        var min = new Point3d(double.MaxValue, double.MaxValue, 0);
        var max = new Point3d(double.MinValue, double.MinValue, 0);
        var has = false;
        foreach (ObjectId id in ms)
        {
            if (items.Count >= limit) break;
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity e) continue;
            object? geometry = null;
            try
            {
                geometry = e switch
                {
                    Line ln => new { start = P(ln.StartPoint), end = P(ln.EndPoint), length = Math.Round(ln.Length, 9) },
                    Circle c => new { center = P(c.Center), radius = Math.Round(c.Radius, 9), diameter = Math.Round(c.Diameter, 9) },
                    Arc ar => new { center = P(ar.Center), radius = Math.Round(ar.Radius, 9), startAngleDegrees = Rad(ar.StartAngle), endAngleDegrees = Rad(ar.EndAngle), length = Math.Round(ar.Length, 9) },
                    Polyline pl => new { closed = pl.Closed, length = Math.Round(pl.Length, 9), vertices = Vertices(pl) },
                    Ellipse el => new { center = P(el.Center), majorRadius = Math.Round(el.MajorRadius, 9), radiusRatio = Math.Round(el.RadiusRatio, 9) },
                    Spline sp when includeSplines => new { degree = sp.Degree, closed = sp.Closed, length = Len(sp) },
                    DBText dt => new { text = dt.TextString, position = P(dt.Position), height = dt.Height, rotationDegrees = Rad(dt.Rotation) },
                    MText mt => new { text = mt.Contents, position = P(mt.Location), height = mt.TextHeight },
                    BlockReference br => new { block = BlockName(tr, br), position = P(br.Position), rotationDegrees = Rad(br.Rotation) },
                    Hatch h => new { pattern = h.PatternName, area = Math.Round(h.Area, 9) },
                    Dimension dm => new { measurement = Math.Round(dm.Measurement, 9), text = dm.DimensionText, textPosition = P(dm.TextPosition) }
                };
            }
            catch (System.Exception ex) { geometry = new { readError = ex.Message }; }

            if (TryExtents(e, out var bmin, out var bmax))
            {
                has = true;
                min = new Point3d(Math.Min(min.X, bmin.X), Math.Min(min.Y, bmin.Y), 0);
                max = new Point3d(Math.Max(max.X, bmax.X), Math.Max(max.Y, bmax.Y), 0);
            }
            items.Add(new { handle = e.Handle.ToString(), type = e.GetRXClass().DxfName, layer = e.Layer, geometry });
        }
        return new
        {
            drawing = Path.GetFileName(Doc().Name),
            path = Doc().Name,
            units = db.Insunits.ToString(),
            unitsCode = (int)db.Insunits,
            modelSpaceEntities = items.Count,
            truncated = items.Count >= limit,
            overallBounds = has ? new { min = P(min), max = P(max) } : null,
            entities = items
        };
    }

    private static object Measure(JsonElement a)
    {
        var db = Db();
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var handle = OptStr(a, "handle");
        if (handle is null)
        {
            var ms = (BlockTableRecord)tr.GetObject(ModelSpace(tr, db), OpenMode.ForRead);
            var min = new Point3d(double.MaxValue, double.MaxValue, 0);
            var max = new Point3d(double.MinValue, double.MinValue, 0);
            var any = false;
            foreach (ObjectId id in ms)
                if (tr.GetObject(id, OpenMode.ForRead, false) is Entity e && TryExtents(e, out var bmin, out var bmax))
                {
                    any = true;
                    min = new Point3d(Math.Min(min.X, bmin.X), Math.Min(min.Y, bmin.Y), 0);
                    max = new Point3d(Math.Max(max.X, bmax.X), Math.Max(max.Y, bmax.Y), 0);
                }
            return new { kind = "extents", bounds = any ? new { min = P(min), max = P(max), width = Math.Round(max.X - min.X, 9), height = Math.Round(max.Y - min.Y, 9) } : null };
        }
        var e2 = (Entity)tr.GetObject(ByHandle(tr, db, handle), OpenMode.ForRead);
        return e2 switch
        {
            Hatch h => new { kind = "area", area = Math.Round(h.Area, 9) },
            Polyline p when p.Closed => new { kind = "area", area = Math.Round(p.Area, 9), length = Math.Round(p.Length, 9) },
            Circle c => new { kind = "area", area = Math.Round(Math.PI * c.Radius * c.Radius, 9), length = Math.Round(Len(c), 9) },
            Curve cv => new { kind = "length", length = Math.Round(Len(cv), 9) },
            _ => throw new ArgumentException($"Entity {handle} has no measurable geometry.")
        };
    }

    // ------------------------------------------------------------- draw ops

    private static object DrawLine(JsonElement a)
    {
        var doc = Doc(); var db = doc.Database;
        using var tr = db.TransactionManager.StartTransaction();
        var line = new Line(Point(a, "start"), Point(a, "end")) { Layer = OptStr(a, "layer") ?? "0" };
        Append(tr, db, line);
        tr.Commit();
        return HandleOf(line);
    }

    private static object DrawCircle(JsonElement a)
    {
        var doc = Doc(); var db = doc.Database;
        using var tr = db.TransactionManager.StartTransaction();
        var c = new Circle(Point(a, "center"), Vector3d.ZAxis, Num(a, "radius")) { Layer = OptStr(a, "layer") ?? "0" };
        if (c.Radius <= 0) throw new ArgumentOutOfRangeException("radius", "radius must be positive.");
        Append(tr, db, c);
        tr.Commit();
        return HandleOf(c);
    }

    private static object DrawArc(JsonElement a)
    {
        var doc = Doc(); var db = doc.Database;
        var sa = Num(a, "startAngle") * Math.PI / 180.0;
        var ea = Num(a, "endAngle") * Math.PI / 180.0;
        using var tr = db.TransactionManager.StartTransaction();
        var arc = new Arc(Point(a, "center"), Num(a, "radius"), sa, ea) { Layer = OptStr(a, "layer") ?? "0" };
        Append(tr, db, arc);
        tr.Commit();
        return HandleOf(arc);
    }

    private static object DrawEllipse(JsonElement a)
    {
        var doc = Doc(); var db = doc.Database;
        var center = Point(a, "center");
        var major = Num(a, "majorRadius");
        var ratio = Num(a, "radiusRatio");
        if (major <= 0) throw new ArgumentOutOfRangeException("majorRadius", "majorRadius must be positive.");
        if (ratio is <= 0 or > 1) throw new ArgumentOutOfRangeException("radiusRatio", "radiusRatio must be within (0,1].");
        var rot = Num(a, "rotation", 0) * Math.PI / 180.0;
        var axis = new Vector3d(Math.Cos(rot) * major, Math.Sin(rot) * major, 0);
        using var tr = db.TransactionManager.StartTransaction();
        var el = new Ellipse(center, Vector3d.ZAxis, axis, ratio, 0, 2 * Math.PI) { Layer = OptStr(a, "layer") ?? "0" };
        Append(tr, db, el);
        tr.Commit();
        return HandleOf(el);
    }

    private static object DrawPolyline(JsonElement a)
    {
        var doc = Doc(); var db = doc.Database;
        var pts = ParsePoints(a, "points");
        var bulges = a.TryGetProperty("bulges", out var b) && b.ValueKind == JsonValueKind.Array
            ? b.EnumerateArray().Select(x => x.GetDouble()).ToArray() : null;
        if (bulges is not null && bulges.Length != pts.Count) throw new ArgumentException("bulges must have one value per point.");
        var closed = Bool(a, "closed");
        using var tr = db.TransactionManager.StartTransaction();
        var pl = new Polyline(pts.Count) { Closed = closed, Layer = OptStr(a, "layer") ?? "0" };
        for (var i = 0; i < pts.Count; i++) pl.AddVertexAt(i, pts[i], bulges is null ? 0 : bulges[i], 0, 0);
        Append(tr, db, pl);
        tr.Commit();
        return HandleOf(pl);
    }

    private static List<Point2d> ParsePoints(JsonElement a, string name)
    {
        if (!a.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"Missing required point array: {name}");
        var list = new List<Point2d>();
        foreach (var p in arr.EnumerateArray())
            list.Add(new Point2d(Num(p, "x"), Num(p, "y")));
        if (list.Count < 2) throw new ArgumentException($"{name} needs at least two points.");
        return list;
    }

    private static object DrawRectangle(JsonElement a)
    {
        var doc = Doc(); var db = doc.Database;
        var x1 = Num(a, "x1"); var y1 = Num(a, "y1"); var x2 = Num(a, "x2"); var y2 = Num(a, "y2");
        using var tr = db.TransactionManager.StartTransaction();
        var pl = new Polyline(4) { Closed = true, Layer = OptStr(a, "layer") ?? "0" };
        pl.AddVertexAt(0, new Point2d(x1, y1), 0, 0, 0);
        pl.AddVertexAt(1, new Point2d(x2, y1), 0, 0, 0);
        pl.AddVertexAt(2, new Point2d(x2, y2), 0, 0, 0);
        pl.AddVertexAt(3, new Point2d(x1, y2), 0, 0, 0);
        Append(tr, db, pl);
        tr.Commit();
        return HandleOf(pl);
    }

    private static object DrawPoint(JsonElement a)
    {
        var doc = Doc(); var db = doc.Database;
        using var tr = db.TransactionManager.StartTransaction();
        var pt = new DBPoint(Point(a, "position")) { Layer = OptStr(a, "layer") ?? "0" };
        Append(tr, db, pt);
        tr.Commit();
        return HandleOf(pt);
    }

    private static object DrawText(JsonElement a)
    {
        var doc = Doc(); var db = doc.Database;
        using var tr = db.TransactionManager.StartTransaction();
        var t = new DBText
        {
            Position = Point(a, "position"),
            TextString = Str(a, "text"),
            Height = Num(a, "height"),
            Rotation = Num(a, "rotation", 0) * Math.PI / 180.0,
            Layer = OptStr(a, "layer") ?? "0"
        };
        if (ResolveTextStyle(tr, db, OptStr(a, "style")) is { } tsId) t.TextStyleId = tsId;
        Append(tr, db, t);
        tr.Commit();
        return HandleOf(t);
    }

    private static object DrawMText(JsonElement a)
    {
        var doc = Doc(); var db = doc.Database;
        using var tr = db.TransactionManager.StartTransaction();
        var m = new MText
        {
            Location = Point(a, "position"),
            Contents = Str(a, "text"),
            TextHeight = Num(a, "height"),
            Width = Num(a, "width", 0),
            Rotation = Num(a, "rotation", 0) * Math.PI / 180.0,
            Layer = OptStr(a, "layer") ?? "0"
        };
        if (ResolveTextStyle(tr, db, OptStr(a, "style")) is { } msId) m.TextStyleId = msId;
        Append(tr, db, m);
        tr.Commit();
        return HandleOf(m);
    }

    private static object DrawHatch(JsonElement a)
    {
        var doc = Doc(); var db = doc.Database;
        var pattern = OptStr(a, "pattern") ?? "ANSI31";
        using var tr = db.TransactionManager.StartTransaction();
        var hatch = new Hatch { Layer = OptStr(a, "layer") ?? "0" };
        hatch.SetHatchPattern(HatchPatternType.PreDefined, pattern);
        hatch.PatternScale = Num(a, "scale", 1);
        hatch.PatternAngle = Num(a, "angle", 0) * Math.PI / 180.0;
        if (a.TryGetProperty("colorIndex", out var ci))
            hatch.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)ClampInt(ci.GetInt32(), 1, 255));

        var handle = OptStr(a, "handle");
        if (string.IsNullOrEmpty(handle))
            throw new ArgumentException("handle of a closed boundary curve is required (draw a polyline/circle first).");

        var srcId = ByHandle(tr, db, handle);
        if (tr.GetObject(srcId, OpenMode.ForRead) is not Curve)
            throw new ArgumentException("handle must refer to a closed curve.");

        Append(tr, db, hatch);
        var boundaries = new ObjectIdCollection { srcId };
        hatch.Associative = true;
        hatch.AppendLoop(HatchLoopTypes.External, boundaries);
        hatch.EvaluateHatch(true);
        tr.Commit();
        return new { handle = hatch.Handle.ToString(), pattern, source = handle, loops = hatch.NumberOfLoops, area = Math.Round(hatch.Area, 9) };
    }

    private static object DrawDimension(JsonElement a)
    {
        var doc = Doc(); var db = doc.Database;
        var kind = Str(a, "kind").ToLowerInvariant();
        var style = db.Dimstyle;
        using var tr = db.TransactionManager.StartTransaction();
        Dimension dim = kind switch
        {
            "linear" => new RotatedDimension(0, Point(a, "start"), Point(a, "end"), Point(a, "dimLine"), OptStr(a, "text") ?? "", style),
            "aligned" => new AlignedDimension(Point(a, "start"), Point(a, "end"), Point(a, "dimLine"), OptStr(a, "text") ?? "", style),
            "radial" => new RadialDimension(Point(a, "start"), Point(a, "end"), Num(a, "radius", 0) == 0 ? 1 : Num(a, "radius"), OptStr(a, "text") ?? "", style),
            "diametric" => new DiametricDimension(Point(a, "start"), Point(a, "end"), Num(a, "radius", 0) == 0 ? 1 : Num(a, "radius"), OptStr(a, "text") ?? "", style),
            "angular" => new LineAngularDimension2(Point(a, "start"), Point(a, "end"), Point(a, "dimLine"), Point(a, "dimLine"), Point(a, "dimLine"), OptStr(a, "text") ?? "", style),
            _ => throw new ArgumentException("kind must be linear|aligned|angular|radial|diametric")
        };
        if (OptStr(a, "layer") is { Length: > 0 } layer) dim.Layer = layer;
        Append(tr, db, dim);
        tr.Commit();
        return HandleOf(dim);
    }

    // ------------------------------------------------------------- edit ops

    private static object Transform(JsonElement a, string mode)
    {
        var doc = Doc(); var db = doc.Database;
        var handle = Str(a, "handle");
        using var tr = db.TransactionManager.StartTransaction();
        var id = ByHandle(tr, db, handle);
        var e = (Entity)tr.GetObject(id, OpenMode.ForWrite);
        switch (mode)
        {
            case "move":
                e.TransformBy(Matrix3d.Displacement(new Vector3d(Num(a, "dx"), Num(a, "dy"), Num(a, "dz", 0))));
                tr.Commit();
                return new { handle, moved = true };
            case "copy":
            {
                var copy = (Entity)e.Clone();
                copy.TransformBy(Matrix3d.Displacement(new Vector3d(Num(a, "dx"), Num(a, "dy"), Num(a, "dz", 0))));
                Append(tr, db, copy);
                tr.Commit();
                return new { source = handle, handle = copy.Handle.ToString() };
            }
            case "rotate":
                e.TransformBy(Matrix3d.Rotation(Num(a, "angle") * Math.PI / 180.0, Vector3d.ZAxis, Point(a, "basePoint")));
                tr.Commit();
                return new { handle, rotated = true };
            case "scale":
            {
                var factor = Num(a, "factor");
                if (factor <= 0) throw new ArgumentOutOfRangeException("factor", "factor must be positive.");
                e.TransformBy(Matrix3d.Scaling(factor, Point(a, "basePoint")));
                tr.Commit();
                return new { handle, scaled = true, factor };
            }
            case "mirror":
            {
                var p1 = Point(a, "p1"); var p2 = Point(a, "p2");
                if (p1.DistanceTo(p2) < 1e-9) throw new ArgumentException("p1 and p2 must differ.");
                var plane = new Plane(p1, new Vector3d(-(p2.Y - p1.Y), p2.X - p1.X, 0));
                var target = e;
                if (!Bool(a, "eraseSource"))
                {
                    target = (Entity)e.Clone();
                    Append(tr, db, target);
                }
                target.TransformBy(Matrix3d.Mirroring(plane));
                tr.Commit();
                return new { handle = target.Handle.ToString(), source = handle, eraseSource = Bool(a, "eraseSource") };
            }
            default: throw new ArgumentException("unsupported transform");
        }
    }

    private static object OffsetEntity(JsonElement a)
    {
        var doc = Doc(); var db = doc.Database;
        var handle = Str(a, "handle");
        var distance = Num(a, "distance");
        using var tr = db.TransactionManager.StartTransaction();
        if (tr.GetObject(ByHandle(tr, db, handle), OpenMode.ForRead) is not Curve curve)
            throw new ArgumentException("handle is not a curve.");
        var offsets = curve.GetOffsetCurves(distance);
        var handles = new List<string>();
        foreach (DBObject o in offsets)
            if (o is Entity ent)
            {
                Append(tr, db, ent);
                handles.Add(ent.Handle.ToString());
            }
        tr.Commit();
        return new { source = handle, distance, created = handles };
    }

    private static object ArrayEntity(JsonElement a)
    {
        var doc = Doc(); var db = doc.Database;
        var handle = Str(a, "handle");
        var rows = ClampInt(Int(a, "rows", 1), 1, 100);
        var cols = ClampInt(Int(a, "cols", 1), 1, 100);
        var rs = Num(a, "rowSpacing", 0);
        var cs = Num(a, "colSpacing", 0);
        using var tr = db.TransactionManager.StartTransaction();
        var src = (Entity)tr.GetObject(ByHandle(tr, db, handle), OpenMode.ForRead);
        var created = new List<string>();
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                if (r == 0 && c == 0) continue;
                var copy = (Entity)src.Clone();
                copy.TransformBy(Matrix3d.Displacement(new Vector3d(c * cs, r * rs, 0)));
                Append(tr, db, copy);
                created.Add(copy.Handle.ToString());
            }
        tr.Commit();
        return new { source = handle, rows, cols, created };
    }

    private static object SetEntity(JsonElement a, string? forceLayerOnly)
    {
        var db = Db();
        var handle = Str(a, "handle");
        using var tr = db.TransactionManager.StartTransaction();
        var e = (Entity)tr.GetObject(ByHandle(tr, db, handle), OpenMode.ForWrite);
        var layer = OptStr(a, "layer");
        if (!string.IsNullOrEmpty(layer))
        {
            var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!layers.Has(layer)) throw new ArgumentException($"Layer not found: {layer}");
            e.Layer = layer;
        }
        if (forceLayerOnly is null)
        {
            if (a.TryGetProperty("colorIndex", out var ci))
                e.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)ClampInt(ci.GetInt32(), 0, 256));
            var trueColor = OptStr(a, "trueColor");
            if (trueColor is not null)
            {
                var hex = trueColor.TrimStart('#');
                if (hex.Length != 6) throw new ArgumentException("trueColor must be RRGGBB.");
                e.Color = Color.FromRgb(Convert.ToByte(hex.Substring(0, 2), 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
            }
            var linetype = OptStr(a, "linetype");
            if (linetype is not null) e.Linetype = linetype;
            if (a.TryGetProperty("lineweight", out var lw)) e.LineWeight = (LineWeight)lw.GetInt32();
            if (a.TryGetProperty("visible", out var vis)) e.Visible = vis.GetBoolean();
        }
        tr.Commit();
        return new { handle, layer = e.Layer, updated = true };
    }

    private static object DeleteEntity(JsonElement a)
    {
        var db = Db();
        var handle = Str(a, "handle");
        using var tr = db.TransactionManager.StartTransaction();
        var e = (Entity)tr.GetObject(ByHandle(tr, db, handle), OpenMode.ForWrite);
        e.Erase();
        tr.Commit();
        return new { handle, deleted = true };
    }

    // ----------------------------------------------------------- block ops

    private static object ListBlocks()
    {
        var db = Db();
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var list = new List<object>();
        foreach (ObjectId id in bt)
        {
            var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (btr.IsLayout || btr.IsAnonymous || btr.IsFromExternalReference) continue;
            list.Add(new { name = btr.Name, entityCount = btr.Cast<ObjectId>().Count() });
        }
        return new { count = list.Count, blocks = list };
    }

    private static object BlockCreate(JsonElement a)
    {
        var name = Str(a, "name");
        var doc = Doc(); var db = doc.Database;
        if (!a.TryGetProperty("entities", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("entities array is required.");
        var defs = arr.EnumerateArray().ToList();
        if (defs.Count is < 1 or > 50) throw new ArgumentException("entities must contain 1..50 items.");
        using var tr = db.TransactionManager.StartTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
        if (bt.Has(name)) throw new ArgumentException($"Block already exists: {name}");
        var btr = new BlockTableRecord { Name = name };
        bt.Add(btr);
        tr.AddNewlyCreatedDBObject(btr, true);
        foreach (var def in defs)
        {
            var kind = Str(def, "type").ToLowerInvariant();
            Entity e = kind switch
            {
                "line" => new Line(Point(def, "start"), Point(def, "end")),
                "circle" => new Circle(Point(def, "center"), Vector3d.ZAxis, Num(def, "radius")),
                "rectangle" => RectPolyline(Num(def, "x1"), Num(def, "y1"), Num(def, "x2"), Num(def, "y2")),
                "text" => new DBText { Position = Point(def, "position"), TextString = Str(def, "text"), Height = Num(def, "height") },
                _ => throw new ArgumentException($"Unsupported block entity type: {kind}")
            };
            btr.AppendEntity(e);
            tr.AddNewlyCreatedDBObject(e, true);
        }
        tr.Commit();
        return new { block = name, entities = defs.Count };
    }

    private static Polyline RectPolyline(double x1, double y1, double x2, double y2)
    {
        var pl = new Polyline(4) { Closed = true };
        pl.AddVertexAt(0, new Point2d(x1, y1), 0, 0, 0);
        pl.AddVertexAt(1, new Point2d(x2, y1), 0, 0, 0);
        pl.AddVertexAt(2, new Point2d(x2, y2), 0, 0, 0);
        pl.AddVertexAt(3, new Point2d(x1, y2), 0, 0, 0);
        return pl;
    }

    private static object BlockInsert(JsonElement a)
    {
        var name = Str(a, "name");
        var doc = Doc(); var db = doc.Database;
        using var tr = db.TransactionManager.StartTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        if (!bt.Has(name)) throw new ArgumentException($"Block not found: {name}");
        var scale = Num(a, "scale", 1);
        var br = new BlockReference(Point(a, "position"), bt[name])
        {
            ScaleFactors = new Scale3d(scale, scale, scale),
            Rotation = Num(a, "rotation", 0) * Math.PI / 180.0,
            Layer = OptStr(a, "layer") ?? "0"
        };
        Append(tr, db, br);
        tr.Commit();
        return new { handle = br.Handle.ToString(), block = name };
    }

    private static object BlockAttributes(JsonElement a)
    {
        var db = Db();
        using var tr = db.TransactionManager.StartTransaction();
        var br = (BlockReference)tr.GetObject(ByHandle(tr, db, Str(a, "handle")), OpenMode.ForRead);
        return new { handle = br.Handle.ToString(), block = BlockName(tr, br), attributes = Attrs(br) };
    }

    private static object SetBlockAttribute(JsonElement a)
    {
        var db = Db();
        var tag = Str(a, "tag");
        var value = a.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
        using var tr = db.TransactionManager.StartTransaction();
        var br = (BlockReference)tr.GetObject(ByHandle(tr, db, Str(a, "handle")), OpenMode.ForRead);
        foreach (ObjectId id in br.AttributeCollection)
            if (id.GetObject(OpenMode.ForWrite) is AttributeReference ar && string.Equals(ar.Tag, tag, StringComparison.OrdinalIgnoreCase))
            {
                ar.TextString = value;
                tr.Commit();
                return new { handle = br.Handle.ToString(), tag, value, updated = true };
            }
        throw new ArgumentException($"Attribute tag not found: {tag}");
    }

    private static object BlockExplode(JsonElement a)
    {
        var db = Db();
        using var tr = db.TransactionManager.StartTransaction();
        var br = (BlockReference)tr.GetObject(ByHandle(tr, db, Str(a, "handle")), OpenMode.ForWrite);
        var collected = new DBObjectCollection();
        br.Explode(collected);
        var handles = new List<string>();
        foreach (DBObject o in collected)
            if (o is Entity ent)
            {
                Append(tr, db, ent);
                handles.Add(ent.Handle.ToString());
            }
        br.Erase();
        tr.Commit();
        return new { exploded = true, created = handles };
    }

    private static object XrefAttach(JsonElement a)
    {
        var path = Path.GetFullPath(Str(a, "path"));
        if (!File.Exists(path)) throw new FileNotFoundException("Xref file not found.", path);
        var name = Str(a, "name");
        var db = Db();
        using var tr = db.TransactionManager.StartTransaction();
        var xrefId = db.AttachXref(path, name);
        if (xrefId.IsNull) throw new InvalidOperationException("AttachXref returned no id.");
        var insert = OptPoint(a, "insertPoint") ?? Point3d.Origin;
        var br = new BlockReference(insert, xrefId);
        Append(tr, db, br);
        tr.Commit();
        return new { name, path, handle = br.Handle.ToString() };
    }

    // --------------------------------------------------- sysvars / commands

    private static object SysVar(string name)
    {
        var value = Application.GetSystemVariable(name);
        return value switch
        {
            null => null,
            Point3d p => P(p),
            Point2d p2 => new { x = p2.X, y = p2.Y },
            short s => s,
            int i => i,
            double d => Math.Round(d, 9),
            bool b => b,
            string str => str,
            _ => value.ToString()
        };
    }

    private static object SetSysVar(JsonElement a)
    {
        var name = Str(a, "name");
        if (!a.TryGetProperty("value", out var v)) throw new ArgumentException("value is required.");
        object? value = v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var i) ? i : v.GetDouble(),
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Object => new Point3d(Num(v, "x"), Num(v, "y"), Num(v, "z", 0)),
            _ => throw new ArgumentException("unsupported value kind")
        };
        Application.SetSystemVariable(name, value);
        return new { name, value = SysVar(name) };
    }

    private static object SendCommand(JsonElement a)
    {
        var cmd = Str(a, "command");
        var steps = new List<string> { cmd };
        if (a.TryGetProperty("args", out var arr) && arr.ValueKind == JsonValueKind.Array)
            steps.AddRange(arr.EnumerateArray().Select(x => x.GetString() ?? ""));
        Doc().Editor.Command(steps.ToArray());
        return new { executed = cmd, prompts = steps.Count - 1 };
    }

    private static object CancelCommand()
    {
        // Posting VK_ESCAPE through the message queue clears a dangling prompt without
        // entering another command context.
        var hwnd = Application.MainWindow?.Handle ?? IntPtr.Zero;
        var sent = 0;
        if (hwnd != IntPtr.Zero)
            for (var i = 0; i < 3; i++)
            {
                PostMessage(hwnd, 0x0100, (IntPtr)0x1B, IntPtr.Zero); // WM_KEYDOWN VK_ESCAPE
                PostMessage(hwnd, 0x0101, (IntPtr)0x1B, IntPtr.Zero); // WM_KEYUP
                sent++;
            }
        return new { escapeSent = sent, hwnd = hwnd.ToInt64() };
    }

    // ------------------------------------------------- full parameter extraction

    private static object ExtractParameters()
    {
        var doc = Doc();
        var db = doc.Database;
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var ms = (BlockTableRecord)tr.GetObject(ModelSpace(tr, db), OpenMode.ForRead);

        var dims = new List<object>();
        var texts = new List<object>();
        var holes = new List<object>();
        var arcs = new List<object>();
        var polylines = new List<object>();
        var other = new SortedDictionary<string, int>(StringComparer.Ordinal);
        double dimlfacSum = 0; int dimlfacN = 0;

        foreach (ObjectId id in ms)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity e) continue;
            switch (e)
            {
                case Dimension dm:
                {
                    var dist = dm.Measurement;
                    var dstyle = (DimStyleTableRecord)tr.GetObject(dm.DimensionStyle, OpenMode.ForRead);
                    dims.Add(new
                    {
                        handle = dm.Handle.ToString(),
                        layer = dm.Layer,
                        measurement = Math.Round(dm.Measurement, 4),
                        textOverride = dm.DimensionText,
                        style = dstyle.Name,
                        dimlfac = dstyle.Dimlfac,
                        textPosition = P(dm.TextPosition)
                    });
                    if (Math.Abs(dstyle.Dimlfac) > 0.0001) { dimlfacSum += dstyle.Dimlfac; dimlfacN++; }
                    break;
                }
                case DBText t:
                    texts.Add(new { handle = t.Handle.ToString(), type = "TEXT", layer = t.Layer, style = TextStyleName(tr, t.TextStyleId), position = P(t.Position), height = Math.Round(t.Height, 3), text = t.TextString });
                    break;
                case MText mt:
                    texts.Add(new { handle = mt.Handle.ToString(), type = "MTEXT", layer = mt.Layer, style = TextStyleName(tr, mt.TextStyleId), position = P(mt.Location), height = Math.Round(mt.TextHeight, 3), text = mt.Contents });
                    break;
                case Circle c:
                    holes.Add(new { handle = c.Handle.ToString(), layer = c.Layer, center = P(c.Center), diameter = Math.Round(c.Diameter, 4) });
                    break;
                case Arc ar:
                    arcs.Add(new { handle = ar.Handle.ToString(), layer = ar.Layer, center = P(ar.Center), radius = Math.Round(ar.Radius, 4), startDeg = Rad(ar.StartAngle), endDeg = Rad(ar.EndAngle) });
                    break;
                case Polyline pl:
                {
                    var v = new List<object>();
                    for (var i = 0; i < pl.NumberOfVertices; i++)
                    {
                        var pt = pl.GetPoint2dAt(i);
                        v.Add(new { x = Math.Round(pt.X, 4), y = Math.Round(pt.Y, 4), bulge = Math.Round(pl.GetBulgeAt(i), 5) });
                    }
                    polylines.Add(new { handle = pl.Handle.ToString(), layer = pl.Layer, closed = pl.Closed, vertices = v });
                    break;
                }
                default:
                    var n = e.GetRXClass().DxfName;
                    other[n] = other.TryGetValue(n, out var c0) ? c0 + 1 : 1;
                    break;
            }
        }

        var layers = new List<object>();
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        foreach (ObjectId id in lt)
        {
            var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
            layers.Add(new { name = l.Name, colorIndex = l.Color?.ColorIndex ?? 0, linetype = ((LinetypeTableRecord)tr.GetObject(l.LinetypeObjectId, OpenMode.ForRead)).Name, isOff = l.IsOff, isFrozen = l.IsFrozen, isLocked = l.IsLocked, isPlottable = l.IsPlottable, current = id == db.Clayer });
        }

        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var blocks = new List<object>();
        foreach (ObjectId id in bt)
        {
            var b = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (b.IsLayout || b.IsFromExternalReference) continue;
            blocks.Add(new { name = b.Name, entityCount = b.Cast<ObjectId>().Count(), anonymous = b.IsAnonymous });
        }

        var styles = new List<object>();
        var st = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        foreach (ObjectId id in st)
        {
            var r = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
            styles.Add(new { name = r.Name, font = r.FileName, bigFont = r.BigFontFileName, current = id == db.Textstyle });
        }

        return new
        {
            drawing = Path.GetFileName(doc.Name),
            path = doc.Name,
            units = db.Insunits.ToString(),
            unitsCode = (int)db.Insunits,
            measurement = SysVar("MEASUREMENT"),
            dimlfacAverage = dimlfacN > 0 ? Math.Round(dimlfacSum / dimlfacN, 4) : 0,
            dimensionCount = dims.Count,
            textCount = texts.Count,
            holeCount = holes.Count,
            dimensions = dims,
            texts,
            holes,
            arcs,
            polylines,
            otherEntities = other,
            layers,
            blocks,
            textStyles = styles,
            note = "dimensions[].textOverride 是标注文字（可能是 <> 或 2-M4 等），孔的实径看 holes[].diameter，比例先看 dimlfacAverage 再动手。"
        };
    }

    // ------------------------------------------------------ geometry comparison

    private static object CompareGeometry(JsonElement a)
    {
        var path = Path.GetFullPath(Str(a, "path"));
        if (!File.Exists(path)) throw new FileNotFoundException("Comparison drawing not found.", path);
        var hasRegion = a.TryGetProperty("x0", out _) && a.TryGetProperty("y0", out _) && a.TryGetProperty("x1", out _) && a.TryGetProperty("y1", out _);
        var rx0 = hasRegion ? Num(a, "x0") : double.MinValue;
        var ry0 = hasRegion ? Num(a, "y0") : double.MinValue;
        var rx1 = hasRegion ? Num(a, "x1") : double.MaxValue;
        var ry1 = hasRegion ? Num(a, "y1") : double.MaxValue;

        var mine = SignatureSet(Db(), rx0, ry0, rx1, ry1);
        var otherDoc = Application.DocumentManager.Open(path, true);
        HashSet<string> theirs;
        try
        {
            theirs = SignatureSet(otherDoc.Database, rx0, ry0, rx1, ry1);
        }
        finally
        {
            otherDoc.CloseAndDiscard();
        }

        var onlyMine = mine.Except(theirs).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var onlyTheirs = theirs.Except(mine).OrderBy(x => x, StringComparer.Ordinal).ToList();
        return new
        {
            active = Path.GetFileName(Doc().Name),
            comparedWith = Path.GetFileName(path),
            region = hasRegion ? new { x0 = rx0, y0 = ry0, x1 = rx1, y1 = ry1 } : null,
            activeCount = mine.Count,
            comparedCount = theirs.Count,
            identical = onlyMine.Count == 0 && onlyTheirs.Count == 0,
            onlyInActive = onlyMine.Count,
            onlyInCompared = onlyTheirs.Count,
            sampleOnlyInActive = onlyMine.Take(20).ToArray(),
            sampleOnlyInCompared = onlyTheirs.Take(20).ToArray()
        };
    }

    private static HashSet<string> SignatureSet(Database db, double x0, double y0, double x1, double y1)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        string R(double v) => Math.Round(v, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (ObjectId id in ms)
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity e) continue;
            double cx, cy;
            var sig = "";
            switch (e)
            {
                case Line ln:
                {
                    var p1 = ln.StartPoint; var p2 = ln.EndPoint;
                    cx = (p1.X + p2.X) / 2; cy = (p1.Y + p2.Y) / 2;
                    var a = $"{R(p1.X)},{R(p1.Y)}"; var b = $"{R(p2.X)},{R(p2.Y)}";
                    sig = string.CompareOrdinal(a, b) <= 0 ? $"L|{a}|{b}" : $"L|{b}|{a}";
                    break;
                }
                case Arc ar:
                    cx = ar.Center.X; cy = ar.Center.Y;
                    sig = $"A|{R(ar.Center.X)},{R(ar.Center.Y)}|{R(ar.Radius)}|{R(Rad(ar.StartAngle) / 1)}|{R(Rad(ar.EndAngle))}";
                    break;
                case Circle c:
                    cx = c.Center.X; cy = c.Center.Y;
                    sig = $"C|{R(c.Center.X)},{R(c.Center.Y)}|{R(c.Diameter)}";
                    break;
                case Polyline pl:
                {
                    var xs = new List<double>(); var ys = new List<double>();
                    for (var i = 0; i < pl.NumberOfVertices; i++) { var pt = pl.GetPoint2dAt(i); xs.Add(pt.X); ys.Add(pt.Y); }
                    cx = xs.Count > 0 ? xs.Average() : 0; cy = ys.Count > 0 ? ys.Average() : 0;
                    var parts = new List<string>();
                    for (var i = 0; i < pl.NumberOfVertices; i++) { var pt = pl.GetPoint2dAt(i); parts.Add($"{R(pt.X)},{R(pt.Y)},{R(pl.GetBulgeAt(i))}"); }
                    sig = $"P|{pl.Closed}|{string.Join(";", parts)}";
                    break;
                }
                case DBText dt:
                    cx = dt.Position.X; cy = dt.Position.Y;
                    sig = $"T|{R(dt.Position.X)},{R(dt.Position.Y)}|{dt.TextString}";
                    break;
                case MText mt:
                    cx = mt.Location.X; cy = mt.Location.Y;
                    sig = $"M|{R(mt.Location.X)},{R(mt.Location.Y)}|{mt.Contents}";
                    break;
                default:
                {
                    if (!TryExtents(e, out var mn, out var mx)) continue;
                    cx = (mn.X + mx.X) / 2; cy = (mn.Y + mx.Y) / 2;
                    sig = $"X|{e.GetRXClass().DxfName}|{R(mn.X)},{R(mn.Y)}|{R(mx.X)},{R(mx.Y)}";
                    break;
                }
            }
            if (cx < x0 || cx > x1 || cy < y0 || cy > y1) continue;
            set.Add(sig);
        }
        return set;
    }

    // ---------------------------------------------------------------- plans

    private static object PreviewPlan(JsonElement a)
    {
        var doc = Doc();
        if (!a.TryGetProperty("operations", out var ops) || ops.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("operations array is required.");
        var kinds = new List<string>();
        foreach (var op in ops.EnumerateArray())
        {
            var kind = op.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (kind is not ("line" or "polyline" or "circle" or "rectangle" or "text" or "dimension" or "create_layer"))
                throw new ArgumentException($"Unsupported plan operation: {kind}");
            kinds.Add(kind);
        }
        PurgeExpiredPlans();
        var planId = Guid.NewGuid().ToString("N");
        Plans[planId] = (doc.Name, ops.GetRawText());
        PlanCreated[planId] = DateTime.UtcNow;
        return new { planId, drawing = doc.Name, operationCount = kinds.Count, operationTypes = kinds, previewOnly = true,
            message = "No drawing changes made. Apply this planId explicitly to commit all operations as one transaction." };
    }

    private static void PurgeExpiredPlans()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-30);
        foreach (var id in PlanCreated.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
        {
            PlanCreated.Remove(id);
            Plans.Remove(id);
        }
        while (PlanCreated.Count > 20)
        {
            var oldest = PlanCreated.OrderBy(kv => kv.Value).First().Key;
            PlanCreated.Remove(oldest);
            Plans.Remove(oldest);
        }
    }

    private static object ApplyPlan(JsonElement a)
    {
        var planId = Str(a, "planId");
        var doc = Doc();
        var db = doc.Database;
        if (!Plans.TryGetValue(planId, out var plan)) throw new ArgumentException("Plan not found or already applied.");
        if (!string.Equals(plan.Drawing, doc.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Active drawing changed after preview; preview the plan again.");

        using var json = JsonDocument.Parse(plan.Operations);
        using var tr = db.TransactionManager.StartTransaction();
        var created = new List<object>();
        foreach (var op in json.RootElement.EnumerateArray())
        {
            var kind = Str(op, "type").ToLowerInvariant();
            var layerName = OptStr(op, "layer");
            Entity? entity = null;
            switch (kind)
            {
                case "create_layer":
                {
                    var name = Str(op, "name");
                    var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
                    if (layers.Has(name)) { created.Add(new { type = "layer", name, existing = true }); break; }
                    var rec = new LayerTableRecord { Name = name };
                    if (op.TryGetProperty("colorIndex", out var ci)) rec.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)ClampInt(ci.GetInt32(), 1, 255));
                    layers.Add(rec);
                    tr.AddNewlyCreatedDBObject(rec, true);
                    created.Add(new { type = "layer", name });
                    break;
                }
                case "line": entity = new Line(Point(op, "start"), Point(op, "end")); break;
                case "polyline":
                {
                    var pts = ParsePoints(op, "points");
                    var bulges = op.TryGetProperty("bulges", out var b) && b.ValueKind == JsonValueKind.Array
                        ? b.EnumerateArray().Select(x => x.GetDouble()).ToArray() : null;
                    if (bulges is not null && bulges.Length != pts.Count) throw new ArgumentException("bulges must have one value per point.");
                    var pl = new Polyline(pts.Count) { Closed = Bool(op, "closed") };
                    for (var i = 0; i < pts.Count; i++) pl.AddVertexAt(i, pts[i], bulges is null ? 0 : bulges[i], 0, 0);
                    entity = pl;
                    break;
                }
                case "circle": entity = new Circle(Point(op, "center"), Vector3d.ZAxis, Num(op, "radius")); break;
                case "rectangle": entity = RectPolyline(Num(op, "x1"), Num(op, "y1"), Num(op, "x2"), Num(op, "y2")); break;
                case "text": entity = new DBText { Position = Point(op, "position"), TextString = Str(op, "text"), Height = Num(op, "height"), Rotation = Num(op, "rotation", 0) * Math.PI / 180.0 }; break;
                case "dimension":
                    entity = new RotatedDimension(Math.Abs(Num(op, "end", 0) - Num(op, "start", 0)) >= 1e-9 ? 0 : Math.PI / 2,
                        Point(op, "start"), Point(op, "end"), Point(op, "dimensionLinePoint"), OptStr(op, "text") ?? "", db.Dimstyle);
                    break;
                default: throw new ArgumentException($"Unsupported plan operation: {kind}");
            }
            if (entity is null) continue;
            if (!string.IsNullOrEmpty(layerName)) entity.Layer = layerName;
            Append(tr, db, entity);
            created.Add(new { type = kind, handle = entity.Handle.ToString(), layer = entity.Layer });
        }
        tr.Commit();
        Plans.Remove(planId);
        PlanCreated.Remove(planId);
        return new { applied = created.Count, created };
    }
}

internal static class TaskCompatibilityExtensions
{
    public static async Task<T> WaitAsync<T>(this Task<T> task, CancellationToken token)
    {
        var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, token));
        if (completed != task) token.ThrowIfCancellationRequested();
        return await task;
    }

    public static async Task<T> WaitAsync<T>(this Task<T> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task) throw new TimeoutException("The operation exceeded its allowed time.");
        return await task;
    }
}




