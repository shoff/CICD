import type { Path, Vec } from './paths';

export type EnemyKind = 'bee' | 'butterfly' | 'boss';

export type EnemyState =
  | 'waiting' // scheduled, not on screen yet
  | 'entering' // flying its entry path
  | 'challenge' // flying a challenging-stage path; leaves the screen at the end
  | 'homing' // flying straight to its formation slot
  | 'formation'
  | 'diving' // attack run (or a boss heading down to fire its beam)
  | 'escort' // flying alongside a diving boss
  | 'beaming'; // boss parked with its tractor beam out

export interface Enemy {
  id: number;
  kind: EnemyKind;
  row: number;
  col: number;
  x: number;
  y: number;
  /** Sprite rotation; 0 means nose up. */
  angle: number;
  state: EnemyState;
  delay: number;
  path: Path | null;
  dist: number;
  speed: number;
  hp: number;
  dead: boolean;
  /** Path distances (the leader's, for escorts) at which to fire. */
  fireAt: number[];
  leader: Enemy | null;
  escortOffset: Vec;
  escortsKilled: number;
  beamDive: boolean;
  beamTime: number;
}

export interface Bullet {
  x: number;
  y: number;
  vx: number;
  vy: number;
  dead: boolean;
}

export interface Particle {
  x: number;
  y: number;
  vx: number;
  vy: number;
  life: number;
  maxLife: number;
  color: string;
}

export interface Popup {
  x: number;
  y: number;
  text: string;
  life: number;
  color: string;
}

export interface Player {
  /** Centre of the (left) ship. A dual fighter's second ship sits 16px to the right. */
  x: number;
  alive: boolean;
  dual: boolean;
}

export type CaptureState =
  | 'pulled' // being drawn up the tractor beam
  | 'held' // riding with its boss
  | 'released' // boss shot mid-flight: flying down to dock with the player
  | 'waitDock' // released while the player had no ship; docks on respawn
  | 'fleeing'; // boss shot in formation: the ship is lost

export interface CapturedShip {
  x: number;
  y: number;
  angle: number;
  state: CaptureState;
  boss: Enemy | null;
  offsetY: number;
}

export type Phase = 'title' | 'stageIntro' | 'playing' | 'stageClear' | 'challengeResult' | 'gameOver';

export interface InputState {
  left: boolean;
  right: boolean;
  fire: boolean;
  firePressed: boolean;
  start: boolean;
  /** Logical x the player is steering towards with touch or mouse, if any. */
  pointerX: number | null;
}

export const NO_INPUT: InputState = {
  left: false,
  right: false,
  fire: false,
  firePressed: false,
  start: false,
  pointerX: null,
};

export type SoundName =
  | 'shoot'
  | 'enemyDie'
  | 'bossHit'
  | 'bossDie'
  | 'playerDie'
  | 'dive'
  | 'beam'
  | 'capture'
  | 'rescue'
  | 'stageStart'
  | 'extraLife'
  | 'perfect';

export interface SoundSink {
  play(name: SoundName): void;
}

export interface ScoreStore {
  load(): number;
  save(score: number): void;
}

export interface Banner {
  text: string;
  color: string;
  until: number;
}
