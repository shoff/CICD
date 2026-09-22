import { describe, expect, it } from 'vitest';
import { W } from '../src/constants';
import { PathBuilder, divePath, entrySide, entryTop } from '../src/paths';

describe('Path', () => {
  it('measures a straight line and interpolates along it by distance', () => {
    const path = new PathBuilder({ x: 0, y: 0 }).line(30, 40).build();
    expect(path.length).toBeCloseTo(50, 5);
    const mid = path.at(25);
    expect(mid.x).toBeCloseTo(15, 5);
    expect(mid.y).toBeCloseTo(20, 5);
    expect(mid.heading).toBeCloseTo(Math.atan2(40, 30), 5);
  });

  it('clamps distances outside the path', () => {
    const path = new PathBuilder({ x: 10, y: 10 }).line(10, 50).build();
    expect(path.at(-5)).toMatchObject({ x: 10, y: 10 });
    expect(path.at(1000).y).toBeCloseTo(50, 5);
  });

  it('keeps arc points on the circle', () => {
    const path = new PathBuilder({ x: 130, y: 100 }).arc(100, 100, 30, 0, -2 * Math.PI).build();
    expect(path.length).toBeCloseTo(2 * Math.PI * 30, 0);
    for (let d = 0; d < path.length; d += 7) {
      const p = path.at(d);
      expect(Math.hypot(p.x - 100, p.y - 100)).toBeCloseTo(30, 0);
    }
  });

  it('mirrors a pattern about the centre line', () => {
    const left = entryTop(false);
    const right = entryTop(true);
    expect(right.length).toBeCloseTo(left.length, 5);
    for (const d of [0, 40, 120, left.length]) {
      expect(right.at(d).x).toBeCloseTo(W - left.at(d).x, 5);
      expect(right.at(d).y).toBeCloseTo(left.at(d).y, 5);
    }
  });

  it('starts entries off screen', () => {
    for (const path of [entryTop(false), entryTop(true), entrySide(false), entrySide(true)]) {
      const s = path.at(0);
      expect(s.x < 0 || s.x > W || s.y < 0).toBe(true);
    }
  });

  it('dives from the slot to below the screen without a jump', () => {
    const path = divePath(60, 50, 120, 40);
    expect(path.at(0)).toMatchObject({ x: 60, y: 50 });
    expect(path.at(path.length).y).toBeGreaterThan(288);
    let prev = path.at(0);
    for (let d = 1; d <= path.length; d += 1) {
      const p = path.at(d);
      expect(Math.hypot(p.x - prev.x, p.y - prev.y)).toBeLessThanOrEqual(1.0001);
      prev = p;
    }
  });
});
