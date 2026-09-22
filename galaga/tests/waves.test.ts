import { describe, expect, it } from 'vitest';
import { challengeWaves, isChallengeStage, normalWaves } from '../src/waves';

describe('waves', () => {
  it('fills every formation slot exactly once', () => {
    for (const stage of [1, 2, 4, 8]) {
      const specs = normalWaves(stage);
      expect(specs).toHaveLength(40);
      const slots = new Set(specs.map((s) => `${s.row}:${s.col}`));
      expect(slots.size).toBe(40);
      expect(specs.filter((s) => s.kind === 'boss').every((s) => s.row === 0)).toBe(true);
      expect(specs.filter((s) => s.kind === 'butterfly').every((s) => s.row === 1 || s.row === 2)).toBe(true);
      expect(specs.filter((s) => s.kind === 'bee').every((s) => s.row >= 3)).toBe(true);
      expect(specs.filter((s) => s.kind === 'bee')).toHaveLength(20);
      expect(specs.filter((s) => s.kind === 'butterfly')).toHaveLength(16);
    }
  });

  it('sends 40 enemies through a challenging stage', () => {
    expect(challengeWaves(3)).toHaveLength(40);
    expect(challengeWaves(7)).toHaveLength(40);
  });

  it('makes stages 3, 7, 11... challenging', () => {
    const challenging = Array.from({ length: 16 }, (_, i) => i + 1).filter(isChallengeStage);
    expect(challenging).toEqual([3, 7, 11, 15]);
  });
});
