# AutoCAD MCP v0.6.0 原生操作参考

共 **66** 个原生操作，全部由 AutoCAD 进程内的 .NET API 直接执行（非命令行字符串拼接）。本地 v0.6.0 MCP 服务向客户端注册 24 个入口；底层原生操作通过 `cad_call { tool: "<名称>", args: { ... } }` 调用，先用 `list_tools` 查目录。


## 连接 / 文档

| 操作 | 变更 | 参数 | 说明 |
| --- | --- | --- | --- |
| `get_bridge_status` | 只读 | `{}` | Plugin version, pipe, active drawing, units, read-only flag, AutoCAD version, document count. |
| `list_tools` | 只读 | `{}` | Native operation catalogue with JSON parameter hints. |
| `list_documents` | 只读 | `{}` | Open documents and which one is active. |
| `get_active_drawing` | 只读 | `{}` | Active drawing name, path, tile mode, read-only state. |
| `new_drawing` | 写入 | `{template?:string}` | Create a new drawing (optionally from a template path). |
| `open_drawing` | 写入 | `{path:string, readOnly?:bool}` | Open a DWG; readOnly defaults to false. |
| `save_active_drawing` | 写入 | `{}` | Save the active drawing to its existing path. |
| `save_as` | 写入 | `{path:string}` | Save the active drawing under a new DWG path. |
| `export_dxf` | 写入 | `{path:string, precision?:int}` | Export the active drawing to DXF. |
| `create_drawing_copy` | 写入 | `{path:string}` | Create a DWG copy of the active drawing without modifying it. |
| `close_active_drawing` | 写入 | `{save?:bool}` | Close the active document (save=false discards). |
| `zoom_extents` | 写入 | `{}` | Zoom the current viewport to drawing extents. |
| `undo` | 写入 | `{count?:int}` | Undo the last operation(s). |
| `redo` | 写入 | `{count?:int}` | Redo undone operation(s). |
| `cancel_command` | 写入 | `{}` | Send Escape to clear a pending command prompt so the bridge keeps responding. |

## 读取 / 查询

| 操作 | 变更 | 参数 | 说明 |
| --- | --- | --- | --- |
| `drawing_summary` | 只读 | `{}` | Units, limits, extents, layer count, entity counts, current layer/layout, key system variables. |
| `list_layers` | 只读 | `{}` | All layers with colour, linetype, lineweight, off/frozen/locked/plottable state. |
| `count_modelspace_entities` | 只读 | `{limit?:int}` | Count model-space entities by DXF type. |
| `list_modelspace_entities` | 只读 | `{limit?:int, type?:string, layer?:string}` | List model-space handles/types/layers, optionally filtered by type or layer. |
| `inspect_drawing_geometry` | 只读 | `{limit?:int, includeSplines?:bool}` | Full geometry dump (lines, arcs, circles, polylines with bulges, ellipse, spline control points, text, dimensions, hatch). |
| `audit_drawing_read` | 只读 | `{}` | Deterministic ModelSpace inventory: enumerated ObjectIds, entity counts by DXF type, unreadable samples and unclassified types. It reports inventory coverage only; it does not independently prove semantic correctness, paper-space/Xref coverage, or design intent. |
| `entity_info` | 只读 | `{handle:string}` | Every readable property of one entity by handle. |
| `select_entities` | 只读 | `{type?:string, layer?:string, colorIndex?:int, text?:string, limit?:int}` | Select handles by type/layer/colour/text filter (whole model space). |
| `measure` | 只读 | `{handle?:string}` | Length of a curve, area of a closed polyline/circle/hatch, or drawing extents. |
| `get_sysvar` | 只读 | `{name:string}` | Read one AutoCAD system variable. |

## 图层

| 操作 | 变更 | 参数 | 说明 |
| --- | --- | --- | --- |
| `layer_create` | 写入 | `{name:string, colorIndex?:int, linetype?:string, lineweight?:int}` | Create a layer (colour index 1-255, optional linetype/lineweight). |
| `layer_set` | 写入 | `{name:string, current?:bool, off?:bool, frozen?:bool, locked?:bool, plottable?:bool, colorIndex?:int, linetype?:string, lineweight?:int, description?:string}` | Change layer state: current/off/on/freeze/thaw/lock/unlock/plottable/colorIndex/linetype/lineweight/description. |
| `layer_delete` | 写入 | `{name:string}` | Delete a layer (must be empty and not current). |

## 绘制

| 操作 | 变更 | 参数 | 说明 |
| --- | --- | --- | --- |
| `draw_line` | 写入 | `{start:point, end:point, layer?:string}` | Draw a line. |
| `draw_circle` | 写入 | `{center:point, radius:number, layer?:string}` | Draw a circle. |
| `draw_arc` | 写入 | `{center:point, radius:number, startAngle:number, endAngle:number, layer?:string}` | Draw an arc (angles in degrees, CCW). |
| `draw_ellipse` | 写入 | `{center:point, majorRadius:number, radiusRatio:number, rotation?:number, layer?:string}` | Draw an ellipse (majorRadius, radiusRatio 0..1). |
| `draw_polyline` | 写入 | `{points:point[], closed?:bool, bulges?:number[], layer?:string}` | Draw a 2D polyline with optional bulges. |
| `draw_rectangle` | 写入 | `{x1:number, y1:number, x2:number, y2:number, layer?:string}` | Draw a closed rectangular polyline from opposite corners. |
| `draw_point` | 写入 | `{position:point, layer?:string}` | Draw a point entity. |
| `draw_text` | 写入 | `{position:point, text:string, height:number, rotation?:number, layer?:string, style?:string}` | Draw single-line text; style may name a text style (use a CJK TrueType style for Chinese). |
| `draw_mtext` | 写入 | `{position:point, text:string, height:number, width:number, rotation?:number, layer?:string, style?:string}` | Draw multi-line text with wrap width; style may name a text style. |
| `draw_hatch` | 写入 | `{pattern:string, handle?:string, points?:point[], scale?:number, angle?:number, colorIndex?:int, layer?:string}` | Hatch a closed boundary by entity handle, or a polygon of points. |
| `draw_dimension` | 写入 | `{kind:string, start:point, end:point, dimLine:point, text?:string, radius?:number, layer?:string}` | Dimension: kind=linear\|aligned\|angular\|radial\|diametric. |

## 编辑

| 操作 | 变更 | 参数 | 说明 |
| --- | --- | --- | --- |
| `move_entity` | 写入 | `{handle:string, dx:number, dy:number, dz?:number}` | Move an entity by a delta. |
| `copy_entity` | 写入 | `{handle:string, dx:number, dy:number, dz?:number}` | Copy an entity by a delta; returns the new handle. |
| `rotate_entity` | 写入 | `{handle:string, basePoint:point, angle:number}` | Rotate an entity about a base point (degrees). |
| `scale_entity` | 写入 | `{handle:string, basePoint:point, factor:number}` | Scale an entity about a base point. |
| `mirror_entity` | 写入 | `{handle:string, p1:point, p2:point, eraseSource?:bool}` | Mirror an entity about a line (two points); keeps the source by default. |
| `offset_entity` | 写入 | `{handle:string, distance:number}` | Offset a curve; returns offset handles. |
| `array_entity` | 写入 | `{handle:string, rows:int, cols:int, rowSpacing:number, colSpacing:number}` | Rectangular array (rows x cols, spacing). |
| `set_entity_layer` | 写入 | `{handle:string, layer:string}` | Move an entity to an existing layer. |
| `set_entity_property` | 写入 | `{handle:string, layer?:string, colorIndex?:int, trueColor?:string, linetype?:string, lineweight?:int, visible?:bool}` | Set layer/colorIndex/trueColor/linetype/lineweight/visible on an entity. |
| `delete_entity` | 写入 | `{handle:string}` | Erase one entity by handle. |

## 块 / 属性 / 外部参照

| 操作 | 变更 | 参数 | 说明 |
| --- | --- | --- | --- |
| `list_blocks` | 只读 | `{}` | Local block definitions usable for insertion. |
| `block_create` | 写入 | `{name:string, entities:object[]}` | Create a block definition from line/circle/rectangle/text entities. |
| `create_block` | 写入 | `{name:string, entities:object[]}` | Alias of block_create. |
| `block_insert` | 写入 | `{name:string, position:point, scale?:number, rotation?:number, layer?:string}` | Insert a block definition with scale and rotation. |
| `insert_block` | 写入 | `{name:string, position:point, scale?:number, rotation?:number, layer?:string}` | Alias of block_insert. |
| `block_attributes` | 只读 | `{handle:string}` | List a block reference's attributes (tag/value). |
| `set_block_attribute` | 写入 | `{handle:string, tag:string, value:string}` | Set an attribute value on a block reference. |
| `block_explode` | 写入 | `{handle:string}` | Explode a block reference; returns created handles. |
| `xref_attach` | 写入 | `{path:string, name:string, insertPoint?:point}` | Attach an external DWG reference. |

## 系统变量 / 命令 / 脚本

| 操作 | 变更 | 参数 | 说明 |
| --- | --- | --- | --- |
| `set_sysvar` | 写入 | `{name:string, value:object}` | Write one AutoCAD system variable. |
| `send_command` | 写入 | `{command:string, args?:string[]}` | Run an AutoCAD command synchronously; pass each prompt answer in args, e.g. {command:"_.CIRCLE",args:["10,10","5"]}. |

## 事务计划

| 操作 | 变更 | 参数 | 说明 |
| --- | --- | --- | --- |
| `preview_drawing_plan` | 只读 | `{operations:object[]}` | Validate up to 100 operations without touching the drawing; returns planId. |
| `apply_drawing_plan` | 写入 | `{planId:string}` | Apply a previewed plan in one transaction (rolls back on any error). |

## 其他

| 操作 | 变更 | 参数 | 说明 |
| --- | --- | --- | --- |
| `compare_geometry` | 只读 | `{path:string, x0?:number, y0?:number, x1?:number, y1?:number}` | Raw-coordinate entity-signature diff. No origin/rotation/scale registration or full property comparison; output includes a warning and must not be treated as registered equality/design correctness proof. |
| `extract_parameters` | 只读 | `{}` | 提取 ModelSpace 和图纸表中支持读取的参数；不能证明设计意图已完整表达或全部实体均可读。与 `audit_drawing_read` 和针对性实体检查配合使用。 |
| `list_text_styles` | 只读 | `{}` | List text styles with typeface / big font so callers can pick a CJK-capable one. |
| `text_style_create` | 写入 | `{name:string, typeface?:string, bigFont?:string, current?:bool}` | Create a text style; typeface is a TrueType family (default SimSun) so Chinese renders instead of '?'. |

---

生成/核验时间：2026-10-21　来源：AutoCAD 2025 / R25.0.0.0 中运行的 v0.6.0 插件 `list_tools` 实机返回 66 项；新增 `audit_drawing_read`。
