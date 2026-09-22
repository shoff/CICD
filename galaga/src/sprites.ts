// Pixel art is authored as strings, one character per pixel; '.' is transparent.
// All sprites face up; the renderer rotates them to their direction of travel.

type Palette = Record<string, string>;

const BASE: Palette = {
  W: '#ffffff',
  R: '#ff2a2a',
  B: '#2f6bff',
  Y: '#ffd400',
  G: '#22d05a',
  O: '#ff7a00',
  C: '#27e0ff',
};

const FIGHTER = [
  '.......W.......',
  '.......W.......',
  '.......W.......',
  '......WWW......',
  '......WWW......',
  '...R..WWW..R...',
  '...R..WWW..R...',
  '...W.WWWWW.W...',
  'R..WWWBWBWWW..R',
  'R..WWBBWBBWW..R',
  'W..WWBBWBBWW..W',
  'W.WWWWWWWWWWW.W',
  'WWWWWRWWWRWWWWW',
  'WWW.RRWWWRR.WWW',
  'WW...R.W.R...WW',
  'W......W......W',
];

const BEE = [
  [
    '.B.........B.',
    '..B...Y...B..',
    'BB.B.YYY.B.BB',
    'BBBBRYYYRBBBB',
    '.BBBYYYYYBBB.',
    '..B.RYYYR.B..',
    '....YRYRY....',
    '.....YRY.....',
    '......Y......',
  ],
  [
    '......Y......',
    '.....YYY.....',
    '...BRYYYRB...',
    '..BBYYYYYBB..',
    '.BBBRYYYRBBB.',
    'BBB.YRYRY.BBB',
    'BB...YRY...BB',
    'B.....Y.....B',
    '.............',
  ],
];

const BUTTERFLY = [
  [
    'W....R.R....W',
    'WW...RRR...WW',
    'WWW.RRRRR.WWW',
    '.WWBRRWRRBWW.',
    '..WBRRWRRBW..',
    '...BRRRRRB...',
    '....RRRRR....',
    '.....RRR.....',
    '......R......',
  ],
  [
    '.....R.R.....',
    '.....RRR.....',
    '....RRRRR....',
    '..WBRRWRRBW..',
    '.WWBRRWRRBWW.',
    'WWW.RRRRR.WWW',
    'WW...RRR...WW',
    'W.....R.....W',
    '......R......',
  ],
];

const BOSS = [
  [
    '......YYY......',
    '.....YYYYY.....',
    '....YGGYGGY....',
    '...GGGGYGGGG...',
    'O..GGYGGGYGG..O',
    'OO.GGGGGGGGG.OO',
    'OOGGGGGGGGGGGOO',
    'OOO.GGGGGGG.OOO',
    '.OO..GGGGG..OO.',
    '..O...GGG...O..',
    '......G.G......',
  ],
  [
    '......YYY......',
    '.....YYYYY.....',
    '....YGGYGGY....',
    '...GGGGYGGGG...',
    '...GGYGGGYGG...',
    '..OGGGGGGGGGO..',
    '.OOGGGGGGGGGOO.',
    'OOO.GGGGGGG.OOO',
    'OO...GGGGG...OO',
    'O.....GGG.....O',
    '......G.G......',
  ],
];

const BADGE_1 = ['WRRR', 'WRRRR', 'WRRR', 'W', 'W', 'W', 'W'];
const BADGE_5 = ['..R..', '.RRR.', 'RRBRR', 'RBBBR', 'RBWBR', 'RBBBR', 'RRBRR', '.RRR.', '..R..'];
const BADGE_10 = ['.YYY.', 'YRRRY', 'YRWRY', 'YRRRY', '.YRY.', '..Y..', '..Y..', '..Y..'];

function build(rows: string[], overrides: Palette = {}): HTMLCanvasElement {
  const palette = { ...BASE, ...overrides };
  const canvas = document.createElement('canvas');
  canvas.width = Math.max(...rows.map((r) => r.length));
  canvas.height = rows.length;
  const ctx = canvas.getContext('2d');
  if (!ctx) throw new Error('2D canvas is not available');
  rows.forEach((row, y) => {
    [...row].forEach((ch, x) => {
      const color = palette[ch];
      if (!color) return;
      ctx.fillStyle = color;
      ctx.fillRect(x, y, 1, 1);
    });
  });
  return canvas;
}

export type Frames = [HTMLCanvasElement, HTMLCanvasElement];

export interface SpriteSet {
  fighter: HTMLCanvasElement;
  capturedFighter: HTMLCanvasElement;
  bee: Frames;
  butterfly: Frames;
  boss: Frames;
  bossHurt: Frames;
  badge1: HTMLCanvasElement;
  badge5: HTMLCanvasElement;
  badge10: HTMLCanvasElement;
}

export function createSprites(): SpriteSet {
  const frames = (art: string[][], overrides: Palette = {}): Frames => [build(art[0], overrides), build(art[1], overrides)];
  return {
    fighter: build(FIGHTER),
    capturedFighter: build(FIGHTER, { W: '#ff2a2a', R: '#ffffff', B: '#ffd400' }),
    bee: frames(BEE),
    butterfly: frames(BUTTERFLY),
    boss: frames(BOSS),
    bossHurt: frames(BOSS, { G: '#b44cff', O: '#2f6bff' }),
    badge1: build(BADGE_1),
    badge5: build(BADGE_5),
    badge10: build(BADGE_10),
  };
}
