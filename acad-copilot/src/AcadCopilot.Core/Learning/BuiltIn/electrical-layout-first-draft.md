# Electrical layout (first draft)

Built-in recipe (AI Copilot). Electrical layout of an Indian house drawn from an existing floor plan, as a FIRST DRAFT for a qualified electrical engineer: insert_symbol_library once, then the symbols placed per room with -INSERT on E-SWCH / E-LITE / E-POWR, wiring runs as arcs, place_services_legend (legend + draft note) and a title. The example steps are one bedroom with inner faces x 2'9 to 13'9 and y 3'9 to 15'9 and a 3' door in the bottom wall hinged at x=3'1.5 (latch jamb at x=6'1.5); adapt every coordinate to the plan.

Saved 2026-11-19. These are run_commands steps that worked; adapt the coordinates to the new drawing.

## Steps

```json
[
  {"command":"SETVAR","inputs":["ATTREQ","1"],"note":"-INSERT asks for the label attribute"},
  {"command":"SETVAR","inputs":["CLAYER","E-SWCH"],"note":"switchboards"},
  {"command":"-INSERT","inputs":["E-SWITCHBOARD","7'1.5,4'0","","","0","SB1"],"note":"latch side of the door, 6 inch clear of the jamb, back edge on the wall face y=3'9"},
  {"command":"SETVAR","inputs":["CLAYER","E-LITE"]},
  {"command":"-INSERT","inputs":["E-FAN","8'3,9'9","","","0",""],"note":"fan at the room centre"},
  {"command":"-INSERT","inputs":["E-LIGHT","3'1,9'9","","","0",""],"note":"wall light on the left wall"},
  {"command":"SETVAR","inputs":["CLAYER","E-POWR"]},
  {"command":"-INSERT","inputs":["E-SOCKET-5A","13'7,7'0","","","90",""],"note":"5A socket beside the bed; rotation 90 puts the flat side on the right wall"},
  {"command":"-INSERT","inputs":["E-SOCKET-5A","13'7,12'6","","","90",""]},
  {"command":"-INSERT","inputs":["E-AC-POINT","8'3,15'6","","","0",""],"note":"AC point high on the outer (top) wall"},
  {"command":"SETVAR","inputs":["CLAYER","E-LITE"]},
  {"command":"ARC","inputs":["7'1.5,4'3","7'9,7'3","8'3,9'2"],"note":"wiring run SB1 to the fan (3-point arc)"},
  {"command":"ARC","inputs":["6'7.5,4'3","4'3,7'0","3'5,9'9"],"note":"wiring run SB1 to the wall light"}
]
```

## Notes

- Workflow: get_drawing_info and list_layers; COPY the finished floor plan (walls, doors, windows, room names; not furniture or dimensions) to a free area 20' or more away and title it ELECTRICAL LAYOUT - <FLOOR>; insert_symbol_library once; show a per-room point schedule (room: lights, fans, 5A, 15A, AC, exhaust, SB) and ask "Draw it"; place the symbols room by room (one run_commands per 2-3 rooms, SETVAR CLAYER before each kind); wiring arcs; place_services_legend beside the layout (it adds the draft note); capture_view to check.
- -INSERT inputs: block, centre point, "" (X scale 1), "" (Y scale), rotation, then the label (SB1, DB, C1 ... or "" to keep the default). If ATTREQ is 0 no label prompt appears. Rotation puts the flat (back) side of sockets and boards on the wall: 0 = wall below the symbol, 90 = wall on its right, 180 = wall above, 270 = wall on its left. Centre the symbol half its depth off the wall face.
- Switchboard (E-SWCH): inside each room next to its door on the LATCH (opening) side, 4'-0" above floor, about 6" from the frame, never behind the door leaf. One per room door; a 2-way board at the other end of long halls and at stair landings; optional bed-side board in the master bedroom.
- Lights (E-LITE): at least one per room. Bedrooms: one ceiling or wall light at 7'-0" plus a bed-side light; living/dining 2-3; kitchen one ceiling + one over the counter; toilets one ceiling + one over the mirror; balcony, sit-out, staircase landings (2-way switched), parking and the gate one each.
- Fans (E-LITE): one ceiling fan at the centre of each bedroom, living and dining (two when the room is longer than about 16'); none in toilets. Exhaust fan (E-EXHAUST-FAN) high on the outer wall of the kitchen (hob side) and of every toilet (in the ventilator).
- 5A sockets (E-POWR) at 1'-0" above floor: both sides of beds, study table, TV wall (2 + TV/data), living 2-3, dining 1, each bedroom 3-4; kitchen and wash counters at 3'-6" above floor.
- 15A power sockets with switch (E-POWR): kitchen fridge (1'-0"), microwave, mixer, water purifier (3'-6" over the counter, 3-4 in total), washing machine in the utility/wash area (3'-6"), iron point, geyser (E-GEYSER) in each toilet at 6'-0" away from the shower, AC point (E-AC-POINT, 16A/20A) in every bedroom at 7'-0" on or near an outer wall (outdoor unit and drain).
- DB (E-DB, MCB DB with main switch and RCCB): inside near the main entrance door at 5'-0" to 6'-0", away from water and the kitchen; a sub-DB per floor in G+1 / G+2; energy meter at the gate or compound wall. Earth pits (E-EARTH, labels E1, E2) in the setback near the DB.
- Wiring runs: 3-point ARCs (or PLINE) from the switchboard to each point it controls, inside the room, clear of door swings: lights and fans on E-LITE, sockets on E-POWR. Optional circuit labels C1, C2 in the symbol label.
- Legend and note: place_services_legend {discipline: electrical, at: [x, y]} draws the legend table with the used symbols and "DRAFT - TO BE CHECKED BY A QUALIFIED ELECTRICAL ENGINEER". Title TEXT 12 high "ELECTRICAL LAYOUT - GROUND FLOOR" with the scale under it.
- Honesty: this is an indicative first draft. Never present loads, wire sizes, MCB / RCCB ratings or earthing as designed or compliant; say the engineer designs them.
