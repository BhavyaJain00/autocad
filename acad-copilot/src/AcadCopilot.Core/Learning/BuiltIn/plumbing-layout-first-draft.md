# Plumbing layout (first draft)

Built-in recipe (AI Copilot). Plumbing (water supply and drainage) layout of an Indian house drawn from an existing floor plan, as a FIRST DRAFT for a qualified plumbing engineer: insert_symbol_library once, fixtures and traps on P-FIXT, supply (inlet) runs on P-WATR, drainage (outlet) runs and stacks on P-DRAN, inspection chambers in the setback, flow arrows with sizes and slopes, place_services_legend (legend + draft note) and a title. The example steps are one toilet with inner faces x 20'0 to 25'0 and y 3'9 to 11'9 whose bottom wall is the outer wall (outer face y=3'0); adapt every coordinate to the plan.

Saved 2026-11-19. These are run_commands steps that worked; adapt the coordinates to the new drawing.

## Steps

```json
[
  {"command":"SETVAR","inputs":["ATTREQ","1"],"note":"-INSERT asks for the label attribute"},
  {"command":"SETVAR","inputs":["CLAYER","P-FIXT"],"note":"fixtures and traps"},
  {"command":"-INSERT","inputs":["P-WC","21'3,3'9","","","180",""],"note":"WC: base point = back edge centre on the outer wall face, 1'3 from the side wall; rotation 180 turns it into the room"},
  {"command":"-INSERT","inputs":["P-WASH-BASIN","25'0,9'0","","","270",""],"note":"basin on the right wall (rotation 270 points it to -X)"},
  {"command":"-INSERT","inputs":["P-NAHANI-TRAP","23'9,10'6","","","0","NT"],"note":"nahani trap in the bath area; floor sloped 1:60 to it"},
  {"command":"-INSERT","inputs":["P-BASIN-TAP","23'9,11'7","","","0",""],"note":"bath tap on the top wall"},
  {"command":"SETVAR","inputs":["CLAYER","P-DRAN"]},
  {"command":"-INSERT","inputs":["P-STACK","21'3,2'6","","","0","SP"],"note":"soil stack outside the outer wall behind the WC"},
  {"command":"-INSERT","inputs":["P-STACK","24'0,2'6","","","0","WP"],"note":"waste stack"},
  {"command":"PLINE","inputs":["21'3,4'6","21'3,2'9",""],"note":"WC to soil stack (110 SWR)"},
  {"command":"PLINE","inputs":["23'9,10'3","23'9,4'3","24'0,2'9",""],"note":"nahani trap to waste stack in the sunken slab"},
  {"command":"-INSERT","inputs":["P-OUTLET-PIPE","23'9,7'3","","","270","75 SWR 1:60"],"note":"flow arrow (rotation 270 = flowing down) with size and slope"},
  {"command":"-INSERT","inputs":["P-INSPECTION-CHAMBER","22'6,0'9","","","0","IC1"],"note":"IC in the setback where the stacks join"},
  {"command":"SETVAR","inputs":["CLAYER","P-WATR"]},
  {"command":"PLINE","inputs":["20'3,11'3","24'6,11'3","24'6,9'0",""],"note":"supply line 6 inch off the wall faces to the tap and basin"},
  {"command":"-INSERT","inputs":["P-INLET-PIPE","22'0,11'3","","","0","20 DIA"],"note":"flow arrow with pipe size"}
]
```

## Notes

- Workflow: get_drawing_info and list_layers; COPY the finished floor plan (walls, doors, windows, room names, sanitary fixtures if drawn) to a free area 20' or more away and title it PLUMBING LAYOUT - <FLOOR>; insert_symbol_library once; list the wet areas (toilets, kitchen sink, utility / wash, balconies, terrace, sump, OHT) with their fixtures and the stack positions, and ask "Draw it"; then fixtures (P-FIXT), stacks and drains (P-DRAN), ICs, supply (P-WATR), arrows and labels, place_services_legend beside the layout (it adds the draft note); capture_view to check.
- -INSERT inputs: block, point, "" (X scale 1), "" (Y scale), rotation, then the label (NT, SP, IC1, a pipe size, or "" for the default). Fixtures (P-WC, P-WASH-BASIN) have their base point at the centre of the back edge on the wall face and extend towards -Y: rotation 0 = wall above the fixture, 180 = wall below, 90 = wall on the left, 270 = wall on the right. Other symbols are centred.
- Planning: keep wet areas stacked on every floor and grouped (toilet back to back with kitchen / utility). Soil (SP) and waste (WP) stacks on an outer wall or in a shaft about 2'-0" x 3'-0", never inside rooms; rain water pipes (RWP) from the terrace at the corners. Soil stacks continue 3'-0" above the terrace as vent pipes with a cowl.
- Sizes (typical, indicative): soil pipes from WCs 110 mm (4") SWR; waste from basins, sinks, nahani and floor traps 75 mm (3") SWR; RWP 110 mm. Water supply CPVC / PPR: main and down-take 25 mm (1"), branches 20 mm (3/4"), 15 mm (1/2") to taps; a stop cock at every toilet / kitchen branch.
- Slopes: horizontal soil drains about 1:40, waste lines 1:60, building drains to the IC 1:40 to 1:60; toilet and wash floors 1:60 to 1:100 towards the nahani / floor trap; toilets usually on a sunken slab 1'-0" to 1'-6" (write it as a note). Write slopes on the outlet arrows ("110 SWR 1:40").
- Traps: every WC has its own trap; a nahani trap (NT) in each bath area; floor traps (FT, with grating) in toilets, kitchen wash and balconies; a gully trap at the foot of every waste stack before the IC.
- Inspection chambers (IC) 1'-6" x 1'-6" minimum in the setback at every junction and change of direction, at most 30'-0" apart; the last one before the municipal sewer or septic tank. Number them IC1, IC2 ...
- Water: sump / UGT near the road (municipal connection) -> pump -> overhead tank (P-WATER-TANK, label "OHT 1000 L") on the stair cabin or terrace -> down-take with gate valve -> one branch per wet area. Heights: basin rim 2'-8", basin tap 3'-0", shower rose 6'-6", kitchen sink tap 3'-6", washing machine tap 3'-6" with a floor drain.
- Drawing: supply (inlet) runs as PLINE on P-WATR (continuous), drainage (outlet) runs on P-DRAN (DASHED; LTSCALE 24 at 1/4"=1'-0"), 6" off the wall faces, never through rooms where avoidable; a P-INLET-PIPE / P-OUTLET-PIPE arrow with size (and slope) on every run, labels SP / WP / RWP on stacks; note "All drains to IC; IC to municipal sewer / septic tank".
- Legend and note: place_services_legend {discipline: plumbing, at: [x, y]} draws the legend table with the used symbols and "DRAFT - TO BE CHECKED BY A QUALIFIED PLUMBING ENGINEER". Title TEXT 12 high "PLUMBING LAYOUT - GROUND FLOOR" with the scale under it.
- Honesty: this is an indicative first draft. Never present pipe sizes, slopes, venting or the sewer connection as designed or compliant; say the engineer designs them.
