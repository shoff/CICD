import { BULLET_SPEED, FORMATION_TOP, H, PLAYER_SPEED, PLAYER_Y, SLOT_DX, SLOT_DY, W } from './constants';
import { beamDivePath, divePath, exitPath, type Vec } from './paths';
import type {
  Banner,
  Bullet,
  CapturedShip,
  Enemy,
  EnemyKind,
  InputState,
  Particle,
  Phase,
  Player,
  Popup,
  ScoreStore,
  SoundSink,
} from './types';
import { NO_INPUT } from './types';
import { challengeWaves, isChallengeStage, normalWaves, type SpawnSpec } from './waves';

const HIT_BOX: Record<EnemyKind, Vec> = {
  bee: { x: 6, y: 5 },
  butterfly: { x: 6, y: 5 },
  boss: { x: 7, y: 7 },
};

const EXPLOSION_COLORS: Record<EnemyKind, string[]> = {
  bee: ['#ffd400', '#2f6bff', '#ffffff', '#ff2a2a'],
  butterfly: ['#ff2a2a', '#ffffff', '#2f6bff'],
  boss: ['#22d05a', '#ffd400', '#ff7a00', '#ffffff'],
};

const PLAYER_COLORS = ['#ffffff', '#ff2a2a', '#ffd400', '#2f6bff'];

// Tractor beam timeline, in seconds after the boss parks.
const BEAM_EXTENDED = 1;
const BEAM_RETRACT = 3.4;
const BEAM_DONE = 4.4;
const BEAM_REACH = 14;

const HOMING_SPEED = 110;
const MAX_ENEMY_BULLETS = 8;
const INTRO_TIME = 2.2;

export function enemyPoints(kind: EnemyKind, inFormation: boolean, escortsKilled = 0): number {
  switch (kind) {
    case 'bee':
      return inFormation ? 50 : 100;
    case 'butterfly':
      return inFormation ? 80 : 160;
    case 'boss':
      return inFormation ? 150 : [400, 800, 1600][Math.min(escortsKilled, 2)];
  }
}

function approach(v: number, target: number, step: number): number {
  return v < target ? Math.min(v + step, target) : Math.max(v - step, target);
}

function turnToward(angle: number, target: number, step: number): number {
  const d = Math.atan2(Math.sin(target - angle), Math.cos(target - angle));
  return Math.abs(d) <= step ? target : angle + Math.sign(d) * step;
}

function clamp(v: number, lo: number, hi: number): number {
  return Math.min(Math.max(v, lo), hi);
}

function isAttacking(e: Enemy): boolean {
  return e.state === 'diving' || e.state === 'escort' || e.state === 'beaming';
}

export class Game {
  phase: Phase = 'title';
  phaseTime = 0;
  time = 0;
  stage = 0;
  score = 0;
  high: number;
  /** Ships waiting in reserve, not counting the one in play. */
  reserve = 2;
  challenge = false;
  challengeHits = 0;
  challengeBonus = 0;
  shotsFired = 0;
  hitCount = 0;

  readonly player: Player = { x: W / 2, alive: false, dual: false };
  enemies: Enemy[] = [];
  playerBullets: Bullet[] = [];
  enemyBullets: Bullet[] = [];
  particles: Particle[] = [];
  popups: Popup[] = [];
  captured: CapturedShip | null = null;
  banner: Banner | null = null;

  private formationTime = 0;
  private swayMix = 1;
  private sway = 0;
  private spread = 1;
  private attackTimer = 0;
  private deathTime = 0;
  private readyTime = -1;
  private sinceShot = 10;
  private nextExtraLife = 20000;
  private nextId = 1;

  constructor(
    private readonly rng: () => number,
    private readonly sound: SoundSink,
    private readonly store: ScoreStore,
  ) {
    this.high = Math.max(20000, store.load());
  }

  update(dt: number, input: InputState): void {
    this.time += dt;
    this.phaseTime += dt;
    if (this.banner && this.time > this.banner.until) this.banner = null;

    switch (this.phase) {
      case 'title':
        this.updateEffects(dt);
        if (input.start || input.firePressed) this.newGame();
        break;
      case 'stageIntro':
        this.updateWorld(dt, input);
        if (this.phaseTime >= INTRO_TIME) {
          this.setPhase('playing');
          this.spawnWaves();
        }
        break;
      case 'playing':
        this.updateWorld(dt, input);
        this.updateAttacks(dt);
        this.updateRespawn(dt);
        this.checkStageEnd();
        break;
      case 'stageClear':
        this.updateWorld(dt, input);
        if (this.phaseTime >= 1.5) this.startStage(this.stage + 1);
        break;
      case 'challengeResult':
        this.updateWorld(dt, input);
        if (this.phaseTime >= 4) this.startStage(this.stage + 1);
        break;
      case 'gameOver':
        this.updateWorld(dt, NO_INPUT);
        if (this.phaseTime >= 9 || (this.phaseTime >= 3 && (input.start || input.firePressed))) {
          this.setPhase('title');
          this.enemies = [];
          this.enemyBullets = [];
        }
        break;
    }
  }

  newGame(): void {
    this.score = 0;
    this.reserve = 2;
    this.nextExtraLife = 20000;
    this.shotsFired = 0;
    this.hitCount = 0;
    this.player.x = W / 2;
    this.player.alive = true;
    this.player.dual = false;
    this.captured = null;
    this.readyTime = -1;
    this.particles = [];
    this.popups = [];
    this.startStage(1);
  }

  startStage(stage: number): void {
    this.stage = stage;
    this.challenge = isChallengeStage(stage);
    this.challengeHits = 0;
    this.challengeBonus = 0;
    this.enemies = [];
    this.playerBullets = [];
    this.enemyBullets = [];
    this.formationTime = 0;
    this.swayMix = 1;
    this.attackTimer = 2.5;
    this.setPhase('stageIntro');
    this.sound.play('stageStart');
  }

  /** Where a formation slot currently is, including the sway and breathing motion. */
  slot(row: number, col: number): Vec {
    return {
      x: W / 2 + (col - 4.5) * SLOT_DX * this.spread + this.sway,
      y: FORMATION_TOP + row * SLOT_DY * (1 + (this.spread - 1) * 0.6),
    };
  }

  /** How far a boss's tractor beam is extended, 0..1. */
  beamLevel(e: Enemy): number {
    if (e.state !== 'beaming') return 0;
    const t = e.beamTime;
    if (t < BEAM_EXTENDED) return t / BEAM_EXTENDED;
    if (t < BEAM_RETRACT) return 1;
    return Math.max(0, 1 - (t - BEAM_RETRACT) / (BEAM_DONE - BEAM_RETRACT));
  }

  /** Sends a boss down to fire its tractor beam. Public so tests can stage a capture. */
  startBeamDive(boss: Enemy): void {
    boss.state = 'diving';
    boss.beamDive = true;
    boss.dist = 0;
    boss.speed = this.diveSpeed();
    boss.escortsKilled = 0;
    boss.fireAt = [];
    boss.path = beamDivePath(boss.x, boss.y, clamp(this.player.x, 24, W - 24));
    this.sound.play('dive');
  }

  /** Sends an enemy on an attack run. Public so tests can stage one. */
  startDive(e: Enemy): void {
    e.state = 'diving';
    e.beamDive = false;
    e.dist = 0;
    e.speed = this.diveSpeed();
    e.escortsKilled = 0;
    const tx = clamp(this.player.x + (this.rng() - 0.5) * 60, 16, W - 16);
    e.path = divePath(e.x, e.y, tx, 20 + this.rng() * 50);
    e.fireAt = this.planShots(e.path.length);
    this.sound.play('dive');
  }

  private setPhase(phase: Phase): void {
    this.phase = phase;
    this.phaseTime = 0;
  }

  private setBanner(text: string, color: string, seconds: number): void {
    this.banner = { text, color, until: this.time + seconds };
  }

  private diveSpeed(): number {
    return Math.min(130 + this.stage * 5, 200);
  }

  private spawnWaves(): void {
    const specs = this.challenge ? challengeWaves(this.stage) : normalWaves(this.stage);
    this.enemies = specs.map((s) => this.createEnemy(s));
  }

  private createEnemy(spec: SpawnSpec): Enemy {
    let fireAt: number[] = [];
    if (!this.challenge && this.stage >= 2 && this.rng() < 0.08 + this.stage * 0.02) {
      fireAt = [spec.path.length * (0.3 + this.rng() * 0.3)];
    }
    return {
      id: this.nextId++,
      kind: spec.kind,
      row: spec.row,
      col: spec.col,
      x: -100,
      y: -100,
      angle: 0,
      state: 'waiting',
      delay: spec.delay,
      path: spec.path,
      dist: 0,
      speed: 150 + Math.min(this.stage, 10) * 4,
      hp: spec.kind === 'boss' && !this.challenge ? 2 : 1,
      dead: false,
      fireAt,
      leader: null,
      escortOffset: { x: 0, y: 0 },
      escortsKilled: 0,
      beamDive: false,
      beamTime: 0,
    };
  }

  private planShots(pathLength: number): number[] {
    const r = this.rng();
    const count = r < 0.2 + this.stage * 0.04 ? 2 : r < 0.75 ? 1 : 0;
    const shots: number[] = [];
    for (let i = 0; i < count; i++) shots.push(pathLength * (0.2 + this.rng() * 0.3));
    return shots.sort((a, b) => a - b);
  }

  private updateWorld(dt: number, input: InputState): void {
    this.updateFormation(dt);
    this.updatePlayer(dt, input);
    for (const e of this.enemies) this.updateEnemy(e, dt);
    this.updateCaptured(dt);
    this.updateBullets(dt);
    this.collide();
    this.enemies = this.enemies.filter((e) => !e.dead);
    this.updateEffects(dt);
  }

  private updateFormation(dt: number): void {
    this.formationTime += dt;
    const filling =
      this.phase === 'stageIntro' || this.enemies.some((e) => e.state === 'waiting' || e.state === 'entering');
    this.swayMix = approach(this.swayMix, filling ? 1 : 0, dt * 0.5);
    this.sway = Math.sin(this.formationTime * 1.1) * 14 * this.swayMix;
    this.spread = 1 + (1 - this.swayMix) * 0.14 * (0.5 - 0.5 * Math.cos(this.formationTime * 1.8));
  }

  private updatePlayer(dt: number, input: InputState): void {
    const p = this.player;
    this.sinceShot += dt;
    if (!p.alive) return;

    let dir = 0;
    if (input.left) dir -= 1;
    if (input.right) dir += 1;
    if (input.pointerX !== null) {
      const centre = p.x + (p.dual ? 8 : 0);
      const d = input.pointerX - centre;
      dir = Math.abs(d) > 1.5 ? Math.sign(d) : 0;
    }
    p.x = clamp(p.x + dir * PLAYER_SPEED * dt, 8, W - 8 - (p.dual ? 16 : 0));

    const wantsShot = (input.firePressed && this.sinceShot > 0.08) || (input.fire && this.sinceShot > 0.3);
    if (wantsShot && this.playerBullets.length <= (p.dual ? 2 : 1)) {
      this.sinceShot = 0;
      this.shotsFired++;
      this.playerBullets.push({ x: p.x, y: PLAYER_Y - 8, vx: 0, vy: -BULLET_SPEED, dead: false });
      if (p.dual) this.playerBullets.push({ x: p.x + 16, y: PLAYER_Y - 8, vx: 0, vy: -BULLET_SPEED, dead: false });
      this.sound.play('shoot');
    }
  }

  private updateEnemy(e: Enemy, dt: number): void {
    switch (e.state) {
      case 'waiting':
        e.delay -= dt;
        if (e.delay <= 0) {
          e.state = this.challenge ? 'challenge' : 'entering';
          this.followPath(e, 0);
        }
        break;
      case 'entering':
      case 'challenge':
      case 'diving':
        this.followPath(e, dt);
        break;
      case 'homing': {
        const s = this.slot(e.row, e.col);
        const dx = s.x - e.x;
        const dy = s.y - e.y;
        const d = Math.hypot(dx, dy);
        const step = HOMING_SPEED * dt;
        if (d <= step) {
          e.x = s.x;
          e.y = s.y;
          e.state = 'formation';
        } else {
          e.x += (dx / d) * step;
          e.y += (dy / d) * step;
          e.angle = turnToward(e.angle, Math.atan2(dy, dx) + Math.PI / 2, 8 * dt);
        }
        break;
      }
      case 'formation': {
        const s = this.slot(e.row, e.col);
        e.x = s.x;
        e.y = s.y;
        e.angle = turnToward(e.angle, 0, 6 * dt);
        break;
      }
      case 'escort':
        this.updateEscort(e);
        break;
      case 'beaming':
        this.updateBeam(e, dt);
        break;
    }
  }

  private followPath(e: Enemy, dt: number): void {
    const path = e.path;
    if (!path) return;
    e.dist += e.speed * dt;
    const pt = path.at(e.dist);
    e.x = pt.x;
    e.y = pt.y;
    e.angle = pt.heading + Math.PI / 2;
    while (e.fireAt.length > 0 && e.dist >= e.fireAt[0]) {
      e.fireAt.shift();
      this.enemyFire(e);
    }
    if (e.dist < path.length) return;

    e.path = null;
    if (e.state === 'challenge') {
      e.dead = true; // flew off screen without being hit
    } else if (e.state === 'entering') {
      e.state = 'homing';
    } else if (e.beamDive) {
      e.state = 'beaming';
      e.beamTime = 0;
      this.sound.play('beam');
    } else {
      this.wrapToTop(e);
    }
  }

  private wrapToTop(e: Enemy): void {
    e.x = this.slot(e.row, e.col).x;
    e.y = -16;
    e.angle = Math.PI;
    e.state = 'homing';
    e.leader = null;
  }

  private updateEscort(e: Enemy): void {
    const leader = e.leader;
    if (leader && !leader.dead && leader.state === 'diving') {
      e.x = leader.x + e.escortOffset.x;
      e.y = leader.y + e.escortOffset.y;
      e.angle = leader.angle;
      while (e.fireAt.length > 0 && leader.dist >= e.fireAt[0]) {
        e.fireAt.shift();
        this.enemyFire(e);
      }
    } else if (!leader || leader.dead) {
      e.leader = null;
      e.state = 'diving';
      e.beamDive = false;
      e.dist = 0;
      e.fireAt = [];
      e.path = exitPath(e.x, e.y);
    } else {
      this.wrapToTop(e);
    }
  }

  private updateBeam(e: Enemy, dt: number): void {
    const c = this.captured;
    if (c && c.state === 'pulled' && c.boss === e) return; // hold the beam while the ship is reeled in

    e.beamTime += dt;
    if (e.beamTime >= BEAM_DONE) {
      e.state = 'homing';
      return;
    }
    const p = this.player;
    if (
      this.phase === 'playing' &&
      e.beamTime >= BEAM_EXTENDED &&
      e.beamTime < BEAM_RETRACT &&
      p.alive &&
      !p.dual &&
      !this.captured &&
      Math.abs(p.x - e.x) < BEAM_REACH
    ) {
      this.captured = { x: p.x, y: PLAYER_Y, angle: 0, state: 'pulled', boss: e, offsetY: 0 };
      p.alive = false;
      this.deathTime = 0;
      this.playerBullets = [];
      this.sound.play('capture');
    }
  }

  private updateCaptured(dt: number): void {
    const c = this.captured;
    if (!c) return;
    switch (c.state) {
      case 'pulled': {
        const boss = c.boss;
        if (!boss) {
          c.state = 'released';
          break;
        }
        const tx = boss.x;
        const ty = boss.y + 18;
        const d = Math.hypot(tx - c.x, ty - c.y);
        const step = 32 * dt;
        c.angle += 10 * dt;
        if (d <= step) {
          c.x = tx;
          c.y = ty;
          c.angle = 0;
          c.state = 'held';
          c.offsetY = 18;
          boss.beamTime = BEAM_RETRACT;
          this.deathTime = 0;
          this.setBanner('FIGHTER CAPTURED', '#ff2a2a', 3);
        } else {
          c.x += ((tx - c.x) / d) * step;
          c.y += ((ty - c.y) / d) * step;
        }
        break;
      }
      case 'held': {
        const boss = c.boss;
        if (!boss) {
          c.state = 'fleeing';
          break;
        }
        c.offsetY = approach(c.offsetY, -16, 40 * dt);
        c.x = boss.x;
        c.y = boss.y + c.offsetY;
        c.angle = boss.state === 'formation' ? 0 : boss.angle;
        break;
      }
      case 'released': {
        const p = this.player;
        const tx = p.alive ? Math.min(p.x + 16, W - 8) : W / 2 + 8;
        const d = Math.hypot(tx - c.x, PLAYER_Y - c.y);
        const step = 100 * dt;
        c.angle += 12 * dt;
        if (d <= step) {
          c.x = tx;
          c.y = PLAYER_Y;
          c.angle = 0;
          if (p.alive) this.dock();
          else c.state = 'waitDock';
        } else {
          c.x += ((tx - c.x) / d) * step;
          c.y += ((PLAYER_Y - c.y) / d) * step;
        }
        break;
      }
      case 'waitDock':
        if (this.player.alive) this.dock();
        break;
      case 'fleeing':
        c.angle = 0;
        c.y -= 90 * dt;
        if (c.y < -20) this.captured = null;
        break;
    }
  }

  private dock(): void {
    const p = this.player;
    p.x = Math.min(p.x, W - 8 - 16);
    p.dual = true;
    this.captured = null;
    this.sound.play('rescue');
    this.setBanner('DUAL FIGHTER', '#27e0ff', 1.5);
  }

  private updateBullets(dt: number): void {
    for (const b of this.playerBullets) {
      b.y += b.vy * dt;
      if (b.y < -8) b.dead = true;
    }
    for (const b of this.enemyBullets) {
      b.x += b.vx * dt;
      b.y += b.vy * dt;
      if (b.y > H + 8) b.dead = true;
    }
  }

  private enemyFire(e: Enemy): void {
    const p = this.player;
    if (this.phase !== 'playing' || !p.alive || this.enemyBullets.length >= MAX_ENEMY_BULLETS) return;
    if (e.y < 16 || e.y > PLAYER_Y - 50) return;
    const vy = Math.min(140 + this.stage * 4, 220);
    const t = (PLAYER_Y - e.y) / vy;
    const tx = p.x + (p.dual ? 8 : 0);
    const vx = clamp((tx - e.x) / t, -60, 60);
    this.enemyBullets.push({ x: e.x, y: e.y + 6, vx, vy, dead: false });
  }

  private collide(): void {
    for (const b of this.playerBullets) {
      for (const e of this.enemies) {
        if (e.dead || e.state === 'waiting') continue;
        const box = HIT_BOX[e.kind];
        if (Math.abs(b.x - e.x) < box.x + 1.5 && Math.abs(b.y - e.y) < box.y + 4) {
          b.dead = true;
          this.hitEnemy(e);
          break;
        }
      }
      const c = this.captured;
      if (!b.dead && c && c.state === 'held' && Math.abs(b.x - c.x) < 7 && Math.abs(b.y - c.y) < 11) {
        b.dead = true;
        this.hitCount++;
        const pts = c.boss && c.boss.state === 'formation' ? 500 : 1000;
        this.addScore(pts);
        this.popup(c.x, c.y, String(pts), '#27e0ff');
        this.explode(c.x, c.y, PLAYER_COLORS, 22, 70);
        this.sound.play('playerDie');
        this.captured = null;
      }
    }

    const p = this.player;
    if (p.alive) {
      const ships = p.dual ? [p.x, p.x + 16] : [p.x];
      for (const b of this.enemyBullets) {
        const i = ships.findIndex((sx) => Math.abs(b.x - sx) < 5 && Math.abs(b.y - PLAYER_Y) < 7);
        if (i >= 0) {
          b.dead = true;
          this.hitPlayer(i === 1);
          break;
        }
      }
    }
    if (p.alive) {
      const ships = p.dual ? [p.x, p.x + 16] : [p.x];
      for (const e of this.enemies) {
        if (e.dead || e.state === 'waiting' || e.state === 'formation') continue;
        const i = ships.findIndex((sx) => Math.abs(e.x - sx) < 10 && Math.abs(e.y - PLAYER_Y) < 10);
        if (i >= 0) {
          e.hp = 1;
          this.hitEnemy(e);
          this.hitPlayer(i === 1);
          break;
        }
      }
    }

    this.playerBullets = this.playerBullets.filter((b) => !b.dead);
    this.enemyBullets = this.enemyBullets.filter((b) => !b.dead);
  }

  private hitEnemy(e: Enemy): void {
    this.hitCount++;
    e.hp--;
    if (e.hp > 0) {
      this.sound.play('bossHit');
      return;
    }
    e.dead = true;
    const inFormation = e.state === 'formation';
    const pts = enemyPoints(e.kind, inFormation, e.escortsKilled);
    this.addScore(pts);
    if (e.kind === 'boss' && !inFormation) this.popup(e.x, e.y, String(pts), '#27e0ff');
    if (this.challenge) this.challengeHits++;
    this.explode(e.x, e.y, EXPLOSION_COLORS[e.kind], e.kind === 'boss' ? 20 : 14, 60);
    this.sound.play(e.kind === 'boss' ? 'bossDie' : 'enemyDie');

    if (e.state === 'escort' && e.leader && !e.leader.dead) e.leader.escortsKilled++;

    const c = this.captured;
    if (c && c.boss === e) {
      c.boss = null;
      if (c.state === 'pulled') c.state = 'released';
      else if (c.state === 'held') c.state = inFormation ? 'fleeing' : 'released';
    }
  }

  private hitPlayer(wing: boolean): void {
    const p = this.player;
    this.sound.play('playerDie');
    if (p.dual) {
      this.explode(wing ? p.x + 16 : p.x, PLAYER_Y, PLAYER_COLORS, 24, 70);
      if (!wing) p.x += 16;
      p.dual = false;
      return;
    }
    this.explode(p.x, PLAYER_Y, PLAYER_COLORS, 40, 90);
    p.alive = false;
    this.deathTime = 0;
  }

  private updateAttacks(dt: number): void {
    if (this.challenge || !this.player.alive) return;
    if (this.enemies.some((e) => e.state === 'waiting' || e.state === 'entering')) return;
    this.attackTimer -= dt;
    if (this.attackTimer > 0) return;

    const remaining = this.enemies.length;
    const endgame = remaining <= 6;
    const base = Math.max(0.5, 2 - this.stage * 0.12);
    this.attackTimer = (endgame ? base * 0.5 : base) * (0.6 + this.rng() * 0.8);

    const attacking = this.enemies.filter(isAttacking).length;
    const maxAttackers = endgame ? remaining : Math.min(2 + Math.floor(this.stage / 2), 7);
    if (attacking >= maxAttackers) return;

    const ready = this.enemies.filter((e) => e.state === 'formation');
    if (ready.length === 0) return;
    const pick = <T>(items: T[]): T => items[Math.floor(this.rng() * items.length)];

    const bosses = ready.filter((e) => e.kind === 'boss');
    if (bosses.length > 0 && this.rng() < 0.3) {
      const boss = pick(bosses);
      const carrying = this.captured?.boss === boss;
      const beamBusy = this.enemies.some((e) => e.state === 'beaming' || (e.state === 'diving' && e.beamDive));
      if (!carrying && !this.captured && !this.player.dual && !beamBusy && this.rng() < 0.5) {
        this.startBeamDive(boss);
      } else {
        this.startBossDive(boss, ready, carrying ? 1 : 2);
      }
      return;
    }
    const grunts = ready.filter((e) => e.kind !== 'boss');
    this.startDive(pick(grunts.length > 0 ? grunts : ready));
  }

  private startBossDive(boss: Enemy, ready: Enemy[], maxEscorts: number): void {
    this.startDive(boss);
    const escorts = ready
      .filter((e) => e.kind === 'butterfly' && e.row === 1 && Math.abs(e.col - boss.col) <= 1)
      .slice(0, maxEscorts);
    escorts.forEach((e, i) => {
      e.state = 'escort';
      e.leader = boss;
      e.escortOffset = { x: i === 0 ? -16 : 16, y: -6 };
      e.fireAt = boss.path ? this.planShots(boss.path.length) : [];
    });
  }

  private updateRespawn(dt: number): void {
    const p = this.player;
    if (p.alive) return;
    if (this.captured?.state === 'pulled') return;
    this.deathTime += dt;

    if (this.readyTime >= 0) {
      this.readyTime += dt;
      if (this.readyTime >= 1.6) {
        this.readyTime = -1;
        this.banner = null;
        p.alive = true;
        p.dual = false;
        p.x = this.captured?.state === 'waitDock' ? W / 2 - 8 : W / 2;
        this.sinceShot = 10;
      }
      return;
    }

    const attackersOut = this.enemies.some(isAttacking);
    if (this.deathTime > 2.5 && (!attackersOut || this.deathTime > 8)) {
      if (this.reserve <= 0) {
        this.gameOver();
        return;
      }
      this.reserve--;
      this.readyTime = 0;
      this.setBanner('READY', '#ff2a2a', 1.6);
    }
  }

  private gameOver(): void {
    this.setPhase('gameOver');
    this.banner = null;
    this.store.save(this.high);
  }

  private checkStageEnd(): void {
    if (this.enemies.length > 0 || !this.player.alive || this.captured) return;
    if (this.challenge) {
      const perfect = this.challengeHits === 40;
      this.challengeBonus = perfect ? 10000 : this.challengeHits * 100;
      this.addScore(this.challengeBonus);
      if (perfect) this.sound.play('perfect');
      this.setPhase('challengeResult');
    } else {
      this.setPhase('stageClear');
    }
  }

  private addScore(points: number): void {
    this.score += points;
    if (this.score > this.high) this.high = this.score;
    while (this.score >= this.nextExtraLife) {
      this.reserve++;
      this.nextExtraLife = this.nextExtraLife === 20000 ? 70000 : this.nextExtraLife + 70000;
      this.sound.play('extraLife');
    }
  }

  private popup(x: number, y: number, text: string, color: string): void {
    this.popups.push({ x, y, text, life: 1.2, color });
  }

  private explode(x: number, y: number, colors: string[], count: number, speed: number): void {
    for (let i = 0; i < count; i++) {
      const a = this.rng() * Math.PI * 2;
      const s = speed * (0.3 + this.rng() * 0.7);
      const life = 0.35 + this.rng() * 0.5;
      this.particles.push({
        x,
        y,
        vx: Math.cos(a) * s,
        vy: Math.sin(a) * s,
        life,
        maxLife: life,
        color: colors[i % colors.length],
      });
    }
  }

  private updateEffects(dt: number): void {
    for (const pt of this.particles) {
      pt.x += pt.vx * dt;
      pt.y += pt.vy * dt;
      pt.vx *= 1 - 2 * dt;
      pt.vy *= 1 - 2 * dt;
      pt.life -= dt;
    }
    this.particles = this.particles.filter((pt) => pt.life > 0);
    for (const pop of this.popups) {
      pop.y -= 12 * dt;
      pop.life -= dt;
    }
    this.popups = this.popups.filter((pop) => pop.life > 0);
  }
}
