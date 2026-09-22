import { H, W, BEAM_Y } from './constants';

export interface Vec {
  x: number;
  y: number;
}

export interface PathPoint {
  x: number;
  y: number;
  /** Direction of travel in radians (atan2 of the velocity). */
  heading: number;
}

type Cubic = readonly [Vec, Vec, Vec, Vec];

function cubicAt(c: Cubic, t: number): Vec {
  const u = 1 - t;
  const a = u * u * u;
  const b = 3 * u * u * t;
  const d = 3 * u * t * t;
  const e = t * t * t;
  return {
    x: a * c[0].x + b * c[1].x + d * c[2].x + e * c[3].x,
    y: a * c[0].y + b * c[1].y + d * c[2].y + e * c[3].y,
  };
}

/**
 * A chain of cubic Bezier segments flattened into a polyline, so a sprite can travel it
 * at constant speed by distance rather than by curve parameter.
 */
export class Path {
  readonly length: number;
  private readonly xs: number[] = [];
  private readonly ys: number[] = [];
  private readonly cum: number[] = [0];

  constructor(segments: readonly Cubic[], samplesPerSegment = 32) {
    if (segments.length === 0) throw new Error('A path needs at least one segment');
    segments.forEach((seg, i) => {
      for (let k = i === 0 ? 0 : 1; k <= samplesPerSegment; k++) {
        const p = cubicAt(seg, k / samplesPerSegment);
        this.xs.push(p.x);
        this.ys.push(p.y);
      }
    });
    let total = 0;
    for (let i = 1; i < this.xs.length; i++) {
      total += Math.hypot(this.xs[i] - this.xs[i - 1], this.ys[i] - this.ys[i - 1]);
      this.cum.push(total);
    }
    this.length = total;
  }

  at(distance: number): PathPoint {
    const d = Math.min(Math.max(distance, 0), this.length);
    let lo = 0;
    let hi = this.xs.length - 1;
    while (hi - lo > 1) {
      const mid = (lo + hi) >> 1;
      if (this.cum[mid] <= d) lo = mid;
      else hi = mid;
    }
    const span = this.cum[hi] - this.cum[lo];
    const t = span > 0 ? (d - this.cum[lo]) / span : 0;
    const dx = this.xs[hi] - this.xs[lo];
    const dy = this.ys[hi] - this.ys[lo];
    return { x: this.xs[lo] + dx * t, y: this.ys[lo] + dy * t, heading: Math.atan2(dy, dx) };
  }
}

/**
 * Fluent builder for paths. With `mirrored` set, every coordinate is reflected about the
 * vertical centre line, so one definition serves both the left and right flight pattern.
 */
export class PathBuilder {
  private readonly segments: Cubic[] = [];
  private cur: Vec;

  constructor(start: Vec, private readonly mirrored = false) {
    this.cur = this.map(start.x, start.y);
  }

  private map(x: number, y: number): Vec {
    return { x: this.mirrored ? W - x : x, y };
  }

  curve(c1x: number, c1y: number, c2x: number, c2y: number, x: number, y: number): this {
    const end = this.map(x, y);
    this.segments.push([this.cur, this.map(c1x, c1y), this.map(c2x, c2y), end]);
    this.cur = end;
    return this;
  }

  line(x: number, y: number): this {
    const s = this.cur;
    const e = this.map(x, y);
    const c1 = { x: s.x + (e.x - s.x) / 3, y: s.y + (e.y - s.y) / 3 };
    const c2 = { x: s.x + (2 * (e.x - s.x)) / 3, y: s.y + (2 * (e.y - s.y)) / 3 };
    this.segments.push([s, c1, c2, e]);
    this.cur = e;
    return this;
  }

  /**
   * Circular arc around (cx, cy) from angle `from` to `to` (screen coordinates, y down, so
   * increasing angles turn clockwise). The current point is expected to lie on the circle at `from`.
   */
  arc(cx: number, cy: number, r: number, from: number, to: number): this {
    const c = this.map(cx, cy);
    const a0 = this.mirrored ? Math.PI - from : from;
    const a1 = this.mirrored ? Math.PI - to : to;
    const pieces = Math.max(1, Math.ceil(Math.abs(a1 - a0) / (Math.PI / 2)));
    const step = (a1 - a0) / pieces;
    const k = (4 / 3) * Math.tan(step / 4);
    for (let i = 0; i < pieces; i++) {
      const s = a0 + i * step;
      const e = s + step;
      const p0 = this.cur;
      const sx = c.x + r * Math.cos(s);
      const sy = c.y + r * Math.sin(s);
      const p3 = { x: c.x + r * Math.cos(e), y: c.y + r * Math.sin(e) };
      const c1 = { x: sx - k * r * Math.sin(s), y: sy + k * r * Math.cos(s) };
      const c2 = { x: p3.x + k * r * Math.sin(e), y: p3.y - k * r * Math.cos(e) };
      this.segments.push([p0, c1, c2, p3]);
      this.cur = p3;
    }
    return this;
  }

  build(): Path {
    return new Path(this.segments);
  }
}

// Entry patterns. Each ends somewhere below the formation; the enemy then homes into its slot.

/** Drops in from the top, swings out to the side, loops underneath and climbs. */
export function entryTop(mirrored: boolean): Path {
  return new PathBuilder({ x: 100, y: -16 }, mirrored)
    .curve(100, 60, 40, 90, 40, 150)
    .arc(70, 150, 30, Math.PI, 0)
    .curve(100, 120, 96, 104, 88, 92)
    .build();
}

/** Sweeps in from a bottom corner, turns a full loop and climbs. */
export function entrySide(mirrored: boolean): Path {
  return new PathBuilder({ x: -16, y: 250 }, mirrored)
    .curve(40, 250, 100, 230, 100, 180)
    .arc(70, 180, 30, 0, -2 * Math.PI)
    .curve(100, 150, 100, 130, 94, 110)
    .build();
}

// Challenging-stage fly-throughs: they start and end off screen.

export function challengeLoop(mirrored: boolean): Path {
  return new PathBuilder({ x: 112, y: -16 }, mirrored)
    .curve(112, 60, 40, 90, 40, 150)
    .arc(70, 150, 30, Math.PI, -Math.PI)
    .curve(40, 210, 110, 250, 120, 310)
    .build();
}

export function challengeSide(mirrored: boolean): Path {
  return new PathBuilder({ x: -16, y: 210 }, mirrored)
    .curve(60, 210, 112, 170, 112, 120)
    .arc(82, 120, 30, 0, -2 * Math.PI)
    .curve(112, 60, 160, 20, 250, 20)
    .build();
}

export function challengeWave(mirrored: boolean): Path {
  return new PathBuilder({ x: -16, y: 70 }, mirrored)
    .curve(60, 70, 50, 200, 112, 200)
    .curve(174, 200, 164, 70, 250, 70)
    .build();
}

export function challengeDive(mirrored: boolean): Path {
  return new PathBuilder({ x: 70, y: -16 }, mirrored)
    .curve(70, 100, 170, 110, 170, 170)
    .curve(170, 230, 90, 250, 60, 310)
    .build();
}

/** Half-loop up and away from the formation, returning the x where the dive begins. */
function turnOver(b: PathBuilder, x: number, y: number): number {
  const side = x < W / 2 ? -1 : 1;
  b.arc(x + side * 12, y, 12, side < 0 ? 0 : Math.PI, side < 0 ? -Math.PI : 2 * Math.PI);
  return x + side * 24;
}

/** Attack run: turn over, swoop at `targetX`, then curl away off the bottom of the screen. */
export function divePath(x: number, y: number, targetX: number, weave: number): Path {
  const side = x < W / 2 ? -1 : 1;
  const b = new PathBuilder({ x, y });
  const sx = turnOver(b, x, y);
  const ty = H * 0.7;
  b.curve(sx, y + 70, targetX + side * weave, ty - 50, targetX, ty);
  b.curve(targetX - side * weave * 0.8, ty + 40, targetX - side * 40, H, targetX - side * 40, H + 24);
  return b.build();
}

/** A boss's run down to the height where it parks and fires its tractor beam. */
export function beamDivePath(x: number, y: number, targetX: number): Path {
  const b = new PathBuilder({ x, y });
  const sx = turnOver(b, x, y);
  b.curve(sx, y + 60, targetX, BEAM_Y - 50, targetX, BEAM_Y);
  return b.build();
}

/** Straight down and off screen, for an escort whose leader was shot. */
export function exitPath(x: number, y: number): Path {
  return new PathBuilder({ x, y }).curve(x, y + 40, x, H, x, H + 24).build();
}
