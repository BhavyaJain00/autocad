# Colour presentation elevation

Built-in recipe (AI Copilot). Turns a finished line elevation into a 2D colour presentation elevation in plain AutoCAD (no rendering): a copy of the elevation, true-colour fills on separate layers A-ELEV-PRES-* with fill_presentation_regions (walls, stone or brick cladding, slab bands, glass, wood, metal, roof), sky, ground and 45-degree shadows with add_presentation_backdrop, a check with capture_view and a shareable PNG with export_view_png. The example steps copy an elevation lying in the window 0,-2' to 60',40' 90' to the right; the -LAYER / -HATCH steps are the fallback when a fill tool fails.

Saved 2026-11-19. These are run_commands steps that worked; adapt the coordinates to the new drawing.

## Steps

```json
[
  {"command":"COPY","inputs":["C","0,-2'","60',40'","","0,0","90',0",""],"note":"copy the line elevation (crossing window) 90' to the right; the original stays as the line version"},
  {"command":"ZOOM","inputs":["W","86',-6'","154',44'"],"note":"point fills only find areas that are on screen"},
  {"command":"-BOUNDARY","inputs":["100',12'",""],"note":"fallback when a point fill finds no closed area: make a closed polyline, then fill it by handle"},
  {"command":"-LAYER","inputs":["N","A-ELEV-PRES-WALL","C","T","243,233,212","A-ELEV-PRES-WALL",""],"note":"fallback without the tool: a true-colour layer"},
  {"command":"SETVAR","inputs":["CLAYER","A-ELEV-PRES-WALL"]},
  {"command":"-HATCH","inputs":["P","S","100',12'",""],"note":"fallback: SOLID fill by internal point on the current layer; then DRAWORDER it to the back"}
]
```

## Notes

- Workflow: get_drawing_info; find the elevation's extents (list_entities on its layer with a window, or the drawing title) and its ground line Y. Ask once with ask_user: the colour scheme (Warm cream + terracotta + sandstone, recommended / Modern white + grey + wood / Earthy brick + cream) and whether to keep a plain line copy (default yes: COPY it 1.5 x its width to the right and colour the copy). ZOOM W around the copy.
- fill_presentation_regions, in this order: walls (wall_paint, islands true so windows and doors stay open), accent walls and features (wall_accent / wall_grey / wall_white), cladding (stone_cladding, brick_cladding), slab_band for slab bands, chajjas, parapet coping and the plinth band, glass for each window and door pane (one point per pane), wood for doors and wooden cladding, metal for railings, grills, frames and the gate, roof_tiles or roof for roofs, planting for trees and shrubs drawn in front. Pick each point well inside its area, at least 3" from any line; an area cut by a band needs one point per piece. Up to 40 fills per call; send the walls first, then the rest.
- A fill that reports no closed area: close the gap (LINE / FILLET), or run -BOUNDARY at the point and fill the new polyline by handle, or ZOOM closer. Never fill the whole sheet: a point outside the building finds no boundary or a huge one; check the reported area.
- add_presentation_backdrop {window: the elevation's extents, ground_y}: sky gradient behind, ground band below (lawn or paving in front of a house with a garden or driveway), and shadows: every chajja / sunshade (depth = its projection, typically 1'6 to 2'0), slab projection (6"-9"), balcony (its projection, 3'-4'), canopy / portico, each as kind below with its elevation rectangle; projecting walls, columns and fins as kind side. Sun from the left unless the user says otherwise (shadows fall down and to the right).
- Palettes (true colours "R,G,B" for color overrides): Warm (default): walls 243,233,212, accent 201,122,92, stone 201,180,150, bands 236,232,224, wood 150,98,60, glass 112,162,202 to 208,230,244, metal 70,78,88. Modern white: walls 248,246,240, accent grey 176,178,180, wood cladding 150,98,60, glass 90,140,180 to 200,225,240, metal 40,44,50. Earthy: brick cladding 178,94,68, walls 236,220,190, bands 245,240,230, wood 120,78,48.
- Keep it 2D and calm: solid fills for walls and bands, gradients only for glass and sky, patterns only for stone, brick, roof tiles and paving. The linework stays on top; fills and shadows are sent behind it automatically. -LAYER OFF A-ELEV-PRES-* shows the plain line version again.
- Check with capture_view (area window around the copy); fix colours by filling again with a color override after erasing the wrong hatch (list_entities layer A-ELEV-PRES-* type HATCH gives the handles).
- Share: export_view_png {area: window, window: the copy's extents plus about 5% margin, name: "<project> front elevation"} writes Documents\AcadCopilot Exports\<name>_<date-time>.png (white background, dark lines, exact colours). Tell the user the path. Width 2400 px by default, 3000-4000 px for printing.
- Title under the presentation: TEXT 12 high "FRONT ELEVATION (PRESENTATION)" and the scale. No photo-realistic rendering, materials or lights: plain AutoCAD 2D only.
