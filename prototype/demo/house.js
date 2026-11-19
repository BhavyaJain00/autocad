/* house.js - the ONE house every drawing in the demo is drafted from. Keep all drawings consistent with it.
 *
 * Units: feet (decimal). Building-local coordinates:
 *   x = across the front: 0 = left side (SOUTH), 30 = right side (NORTH)
 *   y = depth: 0 = front face (EAST, road side), 40 = rear face (WEST)
 * Plot coordinates (site plan): plot corner (0,0) = front-left; the building sits at plot (5,12)..(35,52).
 * Levels (z) are heights in feet above road level (+-0).
 * Walls: outer 9" (0.75) with the outer FACE on the building edge, so outer centerlines are at 0.375 / 29.625 / 39.625.
 *        inner 4 1/2" (0.375) centred on their gridline.
 */
(function () {
  'use strict';
  const O = 0.375; // centreline of a 9" outer wall, measured from the outer face
  const T9 = 0.75, T4 = 0.375;

  const H = {
    name: 'PROPOSED G+2 RESIDENCE',
    plot: { w: 40, d: 60, area: 2400, facing: 'EAST' },
    setback: { front: 12, rear: 8, left: 5, right: 5 },
    bldgAt: [5, 12], // building-local (0,0) in plot coordinates
    bldg: { w: 30, d: 40 },
    road: { width: 30, label: '30\'-0" WIDE ROAD' },
    northRot: -90, // insert the NORTH block with this rotation: north points to +x (right side of every plan)
    t: { outer: T9, inner: T4, parapet: T9, compound: T4 },
    O,
  };

  // ------------------------------------------------------------------ levels and heights
  H.lvl = {
    road: 0, plinth: 1.5, gf: 1.5, ff: 12, sf: 22.5, roof: 33,
    frontParapetTop: 25.5, // SF front terrace parapet (3'-0" above SF floor)
    roofParapetTop: 36, // main roof parapet (3'-0")
    mumtyRoof: 41.5, // stair headroom roof (8'-6" above roof)
    tankTop: 45.5, // overhead water tank on the mumty (4'-0" high)
  };
  H.ftf = 10.5; // floor to floor
  H.slab = 5 / 12; // RCC slab 5"
  H.beam = 1.5; // beam depth incl. slab, under every slab edge and wall line
  H.lintel = 7; // door/window head above floor level
  H.chajja = { proj: 1.5, thk: 0.33, ext: 0.5 }; // sunshade over windows: projection, thickness, extension each side
  H.balcony = { rect: [14, -3.5, 28, 0], railH: 3.5, level: 12 }; // FF balcony slab (cantilever) + MS railing

  // ------------------------------------------------------------------ door / window types (all floors)
  H.types = {
    D: { kind: 'door', w: 3.5, h: 7, desc: 'MAIN DOOR, TEAK WOOD PANELLED' },
    D1: { kind: 'door', w: 3, h: 7, desc: 'FLUSH DOOR' },
    D2: { kind: 'door', w: 2.5, h: 7, desc: 'WPC DOOR (TOILET)' },
    D3: { kind: 'door', w: 3.5, h: 7, desc: 'GLAZED DOOR TO TERRACE' },
    FD: { kind: 'door', w: 5, h: 7, desc: 'FRENCH DOOR, 2 LEAVES' },
    MD: { kind: 'door', w: 3, h: 7, desc: 'MS DOOR, MUMTY TO ROOF' },
    W1: { kind: 'window', w: 6, h: 4, sill: 3, desc: '3-TRACK ALUMINIUM SLIDING' },
    W2: { kind: 'window', w: 5, h: 4, sill: 3, desc: '2-TRACK ALUMINIUM SLIDING' },
    W3: { kind: 'window', w: 4, h: 4, sill: 3, desc: '2-TRACK ALUMINIUM SLIDING' },
    W4: { kind: 'window', w: 3, h: 4, sill: 3, desc: 'CASEMENT WINDOW' },
    KW: { kind: 'window', w: 4, h: 3.5, sill: 3.5, desc: 'ALUMINIUM SLIDING (KITCHEN)' },
    V: { kind: 'window', w: 2, h: 1.5, sill: 5.5, desc: 'LOUVRED VENTILATOR' },
  };

  // ------------------------------------------------------------------ walls (centrelines)
  const outerLoop = [[O, O], [30 - O, O], [30 - O, 40 - O], [O, 40 - O]];
  const IW = {
    W1: { id: 'W1', a: [O, 15], b: [30 - O, 15] }, // front band / middle band (full width)
    W2: { id: 'W2', a: [12, O], b: [12, 15] }, // parking (bedroom 3 on FF) | living
    W3: { id: 'W3', a: [O, 28.5], b: [30 - O, 28.5] }, // middle band / rear band (full width)
    W4: { id: 'W4', a: [8, 15], b: [8, 28.5] }, // staircase | dining
    W5: { id: 'W5', a: [19, 15], b: [19, 40 - O] }, // dining+passage | kitchen+bedroom 2
    W6: { id: 'W6', a: [13, 28.5], b: [13, 40 - O] }, // bedroom 1 | passage+toilet
    W7: { id: 'W7', a: [13, 33], b: [19, 33] }, // passage | toilet
  };
  H.walls = { outerLoop, inner: IW };

  // ------------------------------------------------------------------ openings shared by floors
  const op = {
    O2: { id: 'O2', axis: 'h', at: 15, from: 13, to: 16.5, t: T4 }, // living -> dining
    O3: { id: 'O3', axis: 'v', at: 8, from: 15.1875, to: 18.1875, t: T4 }, // dining -> stair floor landing
    O4: { id: 'O4', axis: 'v', at: 19, from: 17.5, to: 20.5, t: T4 }, // dining -> kitchen
    O5: { id: 'O5', axis: 'h', at: 28.5, from: 14, to: 17, t: T4 }, // dining -> passage
  };
  const dBed1 = { mark: 'D1', axis: 'h', at: 28.5, from: 9, to: 12, t: T4, hinge: 'from', side: +1 }; // dining -> bedroom 1
  const dBed2 = { mark: 'D1', axis: 'v', at: 19, from: 29.25, to: 32.25, t: T4, hinge: 'to', side: +1 }; // passage -> bedroom 2 / study
  const dToilet = { mark: 'D2', axis: 'h', at: 33, from: 14.5, to: 17, t: T4, hinge: 'from', side: +1 }; // passage -> toilet
  const wKitSide = { mark: 'KW', axis: 'v', at: 30 - O, from: 19.5, to: 23.5, t: T9 };
  const wBed2Rear = { mark: 'W2', axis: 'h', at: 40 - O, from: 22, to: 27, t: T9 };
  const wBed1Rear = { mark: 'W2', axis: 'h', at: 40 - O, from: 3, to: 8, t: T9 };
  const wBed1Side = { mark: 'W3', axis: 'v', at: O, from: 32, to: 36, t: T9 };
  const wStair = { mark: 'W4', axis: 'v', at: O, from: 20.5, to: 23.5, t: T9 };
  const wVent = { mark: 'V', axis: 'h', at: 40 - O, from: 15, to: 17, t: T9 };
  const wLivSide = { mark: 'W2', axis: 'v', at: 30 - O, from: 4.5, to: 9.5, t: T9 };

  // rooms: rect = [x0, y0, x1, y1] on wall centrelines / outer faces
  H.floors = {
    GF: {
      key: 'GF', name: 'GROUND FLOOR', lvl: H.lvl.gf,
      outer: { pts: outerLoop, closed: true, t: T9 },
      inner: ['W1', 'W2', 'W3', 'W4', 'W5', 'W6', 'W7'],
      rooms: [
        { name: 'CAR PARKING', rect: [0, 0, 12, 15] },
        { name: 'LIVING', rect: [12, 0, 30, 15] },
        { name: 'STAIRCASE', rect: [0, 15, 8, 28.5], noSize: true },
        { name: 'DINING', rect: [8, 15, 19, 28.5] },
        { name: 'KITCHEN', rect: [19, 15, 30, 28.5] },
        { name: 'BEDROOM 1', rect: [0, 28.5, 13, 40] },
        { name: 'PASSAGE', rect: [13, 28.5, 19, 33], noSize: true },
        { name: 'TOILET', rect: [13, 33, 19, 40] },
        { name: 'BEDROOM 2', rect: [19, 28.5, 30, 40] },
      ],
      openings: [
        { id: 'O1', axis: 'h', at: O, from: 0.75, to: 11.8125, t: T9, note: 'OPEN PARKING ENTRY' },
        op.O2, op.O3, op.O4, op.O5,
      ],
      doors: [
        { mark: 'D', axis: 'v', at: 12, from: 6, to: 9.5, t: T4, hinge: 'from', side: +1 }, // main door: parking porch -> living
        dBed1, dBed2, dToilet,
      ],
      windows: [
        { mark: 'W1', axis: 'h', at: O, from: 18, to: 24, t: T9 }, // living front
        wLivSide, wKitSide, wBed2Rear, wBed1Rear, wBed1Side, wStair, wVent,
      ],
    },
    FF: {
      key: 'FF', name: 'FIRST FLOOR', lvl: H.lvl.ff,
      outer: { pts: outerLoop, closed: true, t: T9 },
      inner: ['W1', 'W2', 'W3', 'W4', 'W5', 'W6', 'W7'],
      balcony: H.balcony,
      rooms: [
        { name: 'BEDROOM 3', rect: [0, 0, 12, 15] },
        { name: 'LIVING', rect: [12, 0, 30, 15] },
        { name: 'BALCONY', rect: [14, -3.5, 28, 0], clear: [14, 3.5] },
        { name: 'STAIRCASE', rect: [0, 15, 8, 28.5], noSize: true },
        { name: 'DINING', rect: [8, 15, 19, 28.5] },
        { name: 'KITCHEN', rect: [19, 15, 30, 28.5] },
        { name: 'BEDROOM 1', rect: [0, 28.5, 13, 40] },
        { name: 'PASSAGE', rect: [13, 28.5, 19, 33], noSize: true },
        { name: 'TOILET', rect: [13, 33, 19, 40] },
        { name: 'BEDROOM 2', rect: [19, 28.5, 30, 40] },
      ],
      openings: [op.O2, op.O3, op.O4, op.O5],
      doors: [
        { mark: 'D1', axis: 'v', at: 12, from: 10.5, to: 13.5, t: T4, hinge: 'to', side: -1 }, // living -> bedroom 3
        { mark: 'FD', axis: 'h', at: O, from: 18.5, to: 23.5, t: T9, leaves: 2, side: +1 }, // French door to balcony (opens in)
        dBed1, dBed2, dToilet,
      ],
      windows: [
        { mark: 'W2', axis: 'h', at: O, from: 3.5, to: 8.5, t: T9 }, // bedroom 3 front
        { mark: 'W3', axis: 'v', at: O, from: 5, to: 9, t: T9 }, // bedroom 3 side
        wLivSide, wKitSide, wBed2Rear, wBed1Rear, wBed1Side, wStair, wVent,
      ],
    },
    SF: {
      key: 'SF', name: 'SECOND FLOOR', lvl: H.lvl.sf,
      // the y=15 wall becomes an outside wall (9") facing the open front terrace
      outer: { pts: [[O, 15], [30 - O, 15], [30 - O, 40 - O], [O, 40 - O]], closed: true, t: T9 },
      parapet: { pts: [[O, 15], [O, O], [30 - O, O], [30 - O, 15]], closed: false, t: T9, h: 3 }, // front terrace parapet
      inner: ['W3', 'W4', 'W5', 'W6', 'W7'],
      rooms: [
        { name: 'OPEN TERRACE', rect: [0, 0, 30, 15] },
        { name: 'STAIRCASE', rect: [0, 15, 8, 28.5], noSize: true },
        { name: 'LIVING / DINING', rect: [8, 15, 19, 28.5] },
        { name: 'KITCHEN', rect: [19, 15, 30, 28.5] },
        { name: 'BEDROOM', rect: [0, 28.5, 13, 40] },
        { name: 'PASSAGE', rect: [13, 28.5, 19, 33], noSize: true },
        { name: 'TOILET', rect: [13, 33, 19, 40] },
        { name: 'STUDY', rect: [19, 28.5, 30, 40] },
      ],
      openings: [op.O3, op.O4, op.O5],
      doors: [
        { mark: 'D3', axis: 'h', at: 15, from: 10.5, to: 14, t: T9, hinge: 'from', side: +1 }, // terrace door (opens in)
        dBed1, dBed2, dToilet,
      ],
      windows: [
        { mark: 'W4', axis: 'h', at: 15, from: 15.5, to: 18.5, t: T9 }, // living window to terrace
        { mark: 'KW', axis: 'h', at: 15, from: 22.5, to: 26.5, t: T9 }, // kitchen window to terrace
        wKitSide, wBed1Rear, wBed1Side, wBed2Rear, wStair, wVent,
      ],
    },
  };

  // ------------------------------------------------------------------ staircase (same position on every floor)
  // dog-legged, 18 risers @ 7" = 10'-6" per floor, 8 treads @ 10" per flight, flights 3'-3", landing at the rear.
  H.stair = {
    box: [0.75, 15.1875, 7.8125, 28.3125], // clear inside the walls
    flightW: 3.25,
    tread: 10 / 12,
    riser: 7 / 12,
    risersPerFlight: 9,
    treadsPerFlight: 8,
    startY: 18.3958, // first riser of the UP flight (and top riser of the arriving flight)
    landingY: 25.0625, // edge of the mid landing (landing = 25.0625 .. 28.3125, 3'-3")
    upFlightX: [4.5625, 7.8125], // right half: goes UP from the front towards the rear landing
    downFlightX: [0.75, 4.0], // left half: second flight, comes back towards the front
    well: [4.0, 4.5625],
    landingLevels: { GF: 6.75, FF: 17.25, SF: 27.75 }, // mid landing levels
    handrailH: 3,
  };

  // ------------------------------------------------------------------ mumty (stair headroom on the roof) + water tank
  H.mumty = { x: [0, 8.1875], y: [14.625, 28.6875], from: H.lvl.roof, to: H.lvl.mumtyRoof, door: { mark: 'MD', face: 'x=8.1875', from: 16, to: 19 } };
  H.tank = { x: [1, 7], y: [17, 23], from: H.lvl.mumtyRoof, to: H.lvl.tankTop, label: 'OHT 2000 L' };

  // ------------------------------------------------------------------ site
  H.site = {
    compound: { t: T4, h: 5 }, // 4 1/2" compound wall along the plot boundary, 5'-0" high
    gate: { from: 5, to: 17, label: '12\'-0" SLIDING GATE' }, // on the front boundary (plot y = 0), plot x
    wicket: { from: 17.5, to: 21, label: '3\'-6" WICKET GATE' },
    driveway: [5, 0, 17, 12], // paved, plot coords
    path: [17.5, 0, 21, 12], // paved walkway from the wicket gate to the porch
    lawn: [21.5, 0.5, 34.5, 11.5],
    trees: [[25.5, 6], [31.5, 7.5], [2.5, 56], [37.5, 56], [2.5, 30], [37.5, 30]],
    sump: { rect: [7, 2, 12, 5.5], label: 'SUMP (U/G) 6000 L' },
    rwh: { c: [32, 56], r: 2, label: 'RAINWATER HARVESTING PIT' },
    utility: { rect: [5, 52, 35, 60], label: 'UTILITY / WASH AREA' },
    balconyAbove: [19, 8.5, 33, 12], // FF balcony projection shown dashed (plot coords)
  };
  H.areas = [
    ['PLOT AREA', '40\'-0" x 60\'-0"', '2,400 SQ.FT.'],
    ['GROUND COVERAGE', '30\'-0" x 40\'-0"', '1,200 SQ.FT. (50%)'],
    ['GROUND FLOOR', '', '1,200 SQ.FT.'],
    ['FIRST FLOOR (+ BALCONY)', '', '1,249 SQ.FT.'],
    ['SECOND FLOOR', '', '750 SQ.FT.'],
    ['MUMTY', '', '108 SQ.FT.'],
    ['TOTAL BUILT-UP AREA', '', '3,307 SQ.FT.'],
  ];

  // ------------------------------------------------------------------ elevation faces: map plan positions to the horizontal elevation axis u
  // front (EAST) elevation: viewer on the road looking west: u = x
  // side (NORTH) elevation: viewer at x = +inf looking south: u = y (front at the left)
  // rear (WEST) elevation: viewer behind looking east: u = 30 - x
  // section A-A: cut at x = 6 through the UP flight, looking south (-x): u = y (front at the left)
  H.faces = {
    front: { u: (x, y) => x, wallAxis: 'h', wallAt: O },
    north: { u: (x, y) => y, wallAxis: 'v', wallAt: 30 - O },
    rear: { u: (x, y) => 30 - x, wallAxis: 'h', wallAt: 40 - O },
  };
  H.sectionAA = { x: 6, look: 'south' };

  // ------------------------------------------------------------------ model-space placement of the 12 drawings (world feet)
  // origin = where the drawing's local (0,0) sits in model space (the AI sets its UCS there).
  // view = local rectangle [x0, y0, x1, y1] the camera frames while drafting; fit = final framing.
  H.sheets = [
    { id: '01', key: 'site', title: 'SITE PLAN', scale: '1/8" = 1\'-0"', origin: [25, 30], view: [-14, -18, 56, 70] },
    { id: '02', key: 'gf', title: 'GROUND FLOOR PLAN', scale: '1/4" = 1\'-0"', origin: [135, 40], view: [-9, -10, 39, 46] },
    { id: '03', key: 'ff', title: 'FIRST FLOOR PLAN', scale: '1/4" = 1\'-0"', origin: [235, 40], view: [-9, -10, 39, 46] },
    { id: '04', key: 'sf', title: 'SECOND FLOOR PLAN', scale: '1/4" = 1\'-0"', origin: [335, 40], view: [-9, -10, 39, 46] },
    { id: '05', key: 'wall', title: 'WALL LAYOUT (CENTRE LINE PLAN)', scale: '1/4" = 1\'-0"', origin: [35, -70], view: [-12, -12, 42, 48] },
    { id: '06', key: 'door', title: 'DOOR LAYOUT & SCHEDULE', scale: '1/4" = 1\'-0"', origin: [135, -60], view: [-9, -42, 41, 46] },
    { id: '07', key: 'window', title: 'WINDOW LAYOUT & SCHEDULE', scale: '1/4" = 1\'-0"', origin: [235, -60], view: [-9, -42, 41, 46] },
    { id: '08', key: 'furn', title: 'FURNITURE LAYOUT', scale: '1/4" = 1\'-0"', origin: [335, -70], view: [-9, -10, 39, 46] },
    { id: '09', key: 'front', title: 'FRONT ELEVATION (EAST)', scale: '1/4" = 1\'-0"', origin: [30, -190], view: [-12, -8, 44, 50] },
    { id: '10', key: 'siderear', title: 'SIDE & REAR ELEVATIONS', scale: '1/4" = 1\'-0"', origin: [115, -190], view: [-8, -8, 92, 50] },
    { id: '11', key: 'section', title: 'SECTION A-A', scale: '1/4" = 1\'-0"', origin: [235, -190], view: [-12, -10, 52, 50] },
    { id: '12', key: 'stair', title: 'STAIRCASE PLAN', scale: '1/2" = 1\'-0"', origin: [340, -200], view: [-6, 11, 20, 32] },
  ];

  // ------------------------------------------------------------------ helpers
  const r4 = (v) => Math.round(v * 10000) / 10000;
  /** clear room size [w, d] between wall faces for a room rect on a floor */
  H.clear = function (floorKey, rect) {
    const f = H.floors[floorKey];
    const sfFront = floorKey === 'SF';
    const side = (v, isY) => {
      if (!isY && (v <= 0 || v >= 30)) return T9;
      if (isY && (v <= 0 || v >= 40)) return T9;
      if (isY && v === 15 && sfFront) return T9 / 2;
      return T4 / 2;
    };
    const w = rect[2] - rect[0] - side(rect[0], false) - side(rect[2], false);
    const d = rect[3] - rect[1] - side(rect[1], true) - side(rect[3], true);
    return [r4(w), r4(d)];
  };
  /** centre of a rect */
  H.center = (rect) => [(rect[0] + rect[2]) / 2, (rect[1] + rect[3]) / 2];
  /** count door/window marks: { MARK: { GF: n, FF: n, SF: n, total } } (+ the mumty door) */
  H.tally = function () {
    const out = {};
    for (const fk of ['GF', 'FF', 'SF']) {
      const f = H.floors[fk];
      for (const it of [...f.doors, ...f.windows]) {
        const m = (out[it.mark] = out[it.mark] || { GF: 0, FF: 0, SF: 0, ROOF: 0, total: 0 });
        m[fk]++; m.total++;
      }
    }
    out.MD = { GF: 0, FF: 0, SF: 0, ROOF: 1, total: 1 };
    return out;
  };
  /** windows/doors of a floor that sit in one outer face: 'front' (y=0.375), 'north' (x=29.625), 'rear' (y=39.625), 'south' (x=0.375) */
  H.onFace = function (floorKey, face) {
    const f = H.floors[floorKey];
    const test = {
      front: (o) => o.axis === 'h' && Math.abs(o.at - O) < 1e-6,
      north: (o) => o.axis === 'v' && Math.abs(o.at - (30 - O)) < 1e-6,
      rear: (o) => o.axis === 'h' && Math.abs(o.at - (40 - O)) < 1e-6,
      south: (o) => o.axis === 'v' && Math.abs(o.at - O) < 1e-6,
      terrace: (o) => floorKey === 'SF' && o.axis === 'h' && Math.abs(o.at - 15) < 1e-6,
    }[face];
    return [...f.windows, ...f.doors, ...f.openings].filter(test).map((o) => ({ ...o, type: H.types[o.mark] || null }));
  };

  window.H = H;
})();
