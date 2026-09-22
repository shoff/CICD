import {
  Path,
  challengeDive,
  challengeLoop,
  challengeSide,
  challengeWave,
  entrySide,
  entryTop,
} from './paths';
import type { EnemyKind } from './types';

export interface SpawnSpec {
  kind: EnemyKind;
  row: number;
  col: number;
  path: Path;
  delay: number;
}

type Member = readonly [EnemyKind, number, number];

interface Stream {
  path: Path;
  members: readonly Member[];
}

const RELEASE_INTERVAL = 0.13;

/** Every fourth stage starting at 3 is a challenging stage, as in the arcade. */
export function isChallengeStage(stage: number): boolean {
  return stage >= 3 && (stage - 3) % 4 === 0;
}

/**
 * Five groups of eight fill the 40-slot formation:
 * row 0 holds 4 bosses, rows 1-2 hold 16 butterflies, rows 3-4 hold 20 bees.
 */
export function normalWaves(stage: number): SpawnSpec[] {
  const flip = stage % 2 === 0;
  const sideBees = stage >= 4 && stage % 4 === 0;
  const gap = Math.max(2.3, 3.1 - stage * 0.08);
  const col = (c: number): number => (flip ? 9 - c : c);
  const top = (m: boolean): Path => entryTop(m !== flip);
  const side = (m: boolean): Path => entrySide(m !== flip);

  const groups: Stream[][] = [
    [
      { path: top(false), members: [['butterfly', 1, 4], ['butterfly', 1, 5], ['butterfly', 2, 4], ['butterfly', 2, 5]] },
      { path: top(true), members: [['bee', 3, 4], ['bee', 3, 5], ['bee', 4, 4], ['bee', 4, 5]] },
    ],
    [
      {
        path: side(false),
        members: [
          ['boss', 0, 3], ['butterfly', 1, 3], ['boss', 0, 4], ['butterfly', 2, 3],
          ['boss', 0, 5], ['butterfly', 1, 6], ['boss', 0, 6], ['butterfly', 2, 6],
        ],
      },
    ],
    [
      {
        path: side(true),
        members: [
          ['butterfly', 1, 8], ['butterfly', 1, 7], ['butterfly', 2, 8], ['butterfly', 2, 7],
          ['butterfly', 1, 2], ['butterfly', 1, 1], ['butterfly', 2, 2], ['butterfly', 2, 1],
        ],
      },
    ],
    [
      {
        path: sideBees ? side(true) : top(true),
        members: [
          ['bee', 3, 9], ['bee', 3, 8], ['bee', 4, 9], ['bee', 4, 8],
          ['bee', 3, 7], ['bee', 3, 6], ['bee', 4, 7], ['bee', 4, 6],
        ],
      },
    ],
    [
      {
        path: sideBees ? side(false) : top(false),
        members: [
          ['bee', 3, 0], ['bee', 3, 1], ['bee', 4, 0], ['bee', 4, 1],
          ['bee', 3, 2], ['bee', 3, 3], ['bee', 4, 2], ['bee', 4, 3],
        ],
      },
    ],
  ];

  const specs: SpawnSpec[] = [];
  groups.forEach((streams, g) => {
    for (const stream of streams) {
      stream.members.forEach(([kind, row, c], i) => {
        specs.push({ kind, row, col: col(c), path: stream.path, delay: g * gap + i * RELEASE_INTERVAL });
      });
    }
  });
  return specs;
}

interface ChallengeGroup {
  make: (mirrored: boolean) => Path;
  kinds: readonly EnemyKind[];
}

/** Five groups of eight fly through in mirrored pairs of streams and leave; nobody shoots. */
export function challengeWaves(stage: number): SpawnSpec[] {
  const variant = Math.floor((stage - 3) / 4) % 2;
  const groups: ChallengeGroup[] =
    variant === 0
      ? [
          { make: challengeLoop, kinds: ['bee'] },
          { make: challengeSide, kinds: ['butterfly', 'boss'] },
          { make: challengeWave, kinds: ['butterfly'] },
          { make: challengeDive, kinds: ['bee'] },
          { make: challengeLoop, kinds: ['butterfly', 'bee'] },
        ]
      : [
          { make: challengeWave, kinds: ['butterfly'] },
          { make: challengeDive, kinds: ['bee', 'boss'] },
          { make: challengeSide, kinds: ['bee'] },
          { make: challengeLoop, kinds: ['butterfly'] },
          { make: challengeWave, kinds: ['bee', 'butterfly'] },
        ];

  const specs: SpawnSpec[] = [];
  groups.forEach((group, g) => {
    [false, true].forEach((mirrored, s) => {
      const path = group.make(mirrored);
      for (let i = 0; i < 4; i++) {
        specs.push({
          kind: group.kinds[(i + s) % group.kinds.length],
          row: 0,
          col: 0,
          path,
          delay: g * 3 + i * 0.16 + s * 0.08,
        });
      }
    });
  });
  return specs;
}
