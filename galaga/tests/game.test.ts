import { describe, expect, it } from 'vitest';
import { PLAYER_Y } from '../src/constants';
import { Game, enemyPoints } from '../src/game';
import type { Enemy, InputState, SoundName } from '../src/types';
import { NO_INPUT } from '../src/types';

const STEP = 1 / 60;

/** Deterministic PRNG (mulberry32) so simulations are reproducible. */
function seeded(seed: number): () => number {
  let a = seed;
  return () => {
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function makeGame(seed = 1): { game: Game; sounds: SoundName[]; saved: number[] } {
  const sounds: SoundName[] = [];
  const saved: number[] = [];
  const game = new Game(seeded(seed), { play: (n) => sounds.push(n) }, { load: () => 0, save: (s) => saved.push(s) });
  return { game, sounds, saved };
}

function run(game: Game, seconds: number, input: InputState | ((t: number) => InputState) = NO_INPUT): void {
  const frames = Math.round(seconds / STEP);
  for (let i = 0; i < frames; i++) game.update(STEP, typeof input === 'function' ? input(i * STEP) : input);
}

function startGame(game: Game): void {
  game.update(STEP, { ...NO_INPUT, start: true });
  expect(game.phase).toBe('stageIntro');
  run(game, 2.3);
  expect(game.phase).toBe('playing');
}

/** Stops enemies from starting attack runs, to stage scenarios by hand. */
function freezeAttacks(game: Game): void {
  (game as unknown as { attackTimer: number }).attackTimer = 1e9;
}

describe('scoring', () => {
  it('pays more for diving enemies and for bosses whose escorts died first', () => {
    expect(enemyPoints('bee', true)).toBe(50);
    expect(enemyPoints('bee', false)).toBe(100);
    expect(enemyPoints('butterfly', true)).toBe(80);
    expect(enemyPoints('butterfly', false)).toBe(160);
    expect(enemyPoints('boss', true)).toBe(150);
    expect(enemyPoints('boss', false, 0)).toBe(400);
    expect(enemyPoints('boss', false, 1)).toBe(800);
    expect(enemyPoints('boss', false, 2)).toBe(1600);
  });
});

describe('Game', () => {
  it('starts on the title screen and begins stage 1 on start', () => {
    const { game, sounds } = makeGame();
    expect(game.phase).toBe('title');
    startGame(game);
    expect(game.stage).toBe(1);
    expect(game.player.alive).toBe(true);
    expect(game.enemies).toHaveLength(40);
    expect(sounds).toContain('stageStart');
  });

  it('flies all 40 enemies into formation', () => {
    const { game } = makeGame();
    startGame(game);
    freezeAttacks(game);
    run(game, 25);
    expect(game.enemies).toHaveLength(40);
    expect(game.enemies.every((e) => e.state === 'formation')).toBe(true);
    const bosses = game.enemies.filter((e) => e.kind === 'boss');
    expect(bosses.every((b) => b.hp === 2)).toBe(true);
  });

  it('allows at most two shots in flight for a single fighter', () => {
    const { game } = makeGame();
    startGame(game);
    for (let i = 0; i < 6; i++) game.update(STEP, { ...NO_INPUT, fire: true, firePressed: true });
    expect(game.playerBullets.length).toBeLessThanOrEqual(2);
    expect(game.shotsFired).toBe(2);
  });

  it('scores by shooting enemies in formation', () => {
    const { game } = makeGame();
    startGame(game);
    freezeAttacks(game);
    run(game, 25);
    const bee = game.enemies.find((e) => e.kind === 'bee' && e.col === 4 && e.row === 4) as Enemy;
    game.player.x = bee.x;
    // Keep the player lined up under the (swaying) target while firing.
    run(game, 1.5, () => ({ ...NO_INPUT, fire: true, pointerX: bee.x }));
    expect(bee.dead).toBe(true);
    expect(game.score).toBeGreaterThanOrEqual(50);
  });

  it('takes two hits to destroy a boss', () => {
    const { game, sounds } = makeGame();
    startGame(game);
    freezeAttacks(game);
    run(game, 25);
    // Clear the column under a boss so shots reach it.
    const boss = game.enemies.find((e) => e.kind === 'boss') as Enemy;
    for (const e of game.enemies) if (e !== boss && e.col === boss.col) e.dead = true;
    let hitOnce = false;
    run(game, 3, () => {
      if (boss.hp === 1) hitOnce = true;
      return { ...NO_INPUT, fire: true, pointerX: boss.x };
    });
    expect(hitOnce).toBe(true);
    expect(boss.dead).toBe(true);
    expect(sounds).toContain('bossHit');
    expect(sounds).toContain('bossDie');
  });

  it('captures the fighter with the tractor beam, then respawns from reserve', () => {
    const { game, sounds } = makeGame(3);
    startGame(game);
    freezeAttacks(game);
    run(game, 25);
    const boss = game.enemies.find((e) => e.kind === 'boss') as Enemy;
    game.player.x = 112;
    game.startBeamDive(boss);
    run(game, 8, () => ({ ...NO_INPUT, pointerX: boss.x }));
    expect(sounds).toContain('capture');
    expect(game.captured?.state).toBe('held');
    expect(game.captured?.boss).toBe(boss);
    run(game, 10);
    expect(game.player.alive).toBe(true);
    expect(game.reserve).toBe(1);
    expect(boss.state).toBe('formation');
  });

  it('rescues a captured fighter as a dual fighter by shooting its diving boss', () => {
    const { game, sounds } = makeGame(3);
    startGame(game);
    freezeAttacks(game);
    run(game, 25);
    const boss = game.enemies.find((e) => e.kind === 'boss') as Enemy;
    game.player.x = 112;
    game.startBeamDive(boss);
    run(game, 8, () => ({ ...NO_INPUT, pointerX: boss.x }));
    run(game, 10);
    expect(game.captured?.state).toBe('held');

    // Kill the boss mid-dive: the captured ship is released and docks.
    boss.hp = 1;
    game.startDive(boss);
    run(game, 0.5);
    (game as unknown as { hitEnemy(e: Enemy): void }).hitEnemy(boss);
    expect(game.captured?.state).toBe('released');
    run(game, 5);
    expect(game.captured).toBeNull();
    expect(game.player.dual).toBe(true);
    expect(sounds).toContain('rescue');
  });

  it('loses a dual fighter wing before losing the ship', () => {
    const { game } = makeGame();
    startGame(game);
    freezeAttacks(game);
    game.player.dual = true;
    game.player.x = 100;
    game.enemyBullets.push({ x: 116, y: PLAYER_Y, vx: 0, vy: 0, dead: false });
    game.update(STEP, NO_INPUT);
    expect(game.player.alive).toBe(true);
    expect(game.player.dual).toBe(false);
    expect(game.player.x).toBe(100);
  });

  it('ends the game when the last ship is lost and saves the high score', () => {
    const { game, saved } = makeGame();
    startGame(game);
    game.reserve = 0;
    game.enemyBullets.push({ x: game.player.x, y: PLAYER_Y, vx: 0, vy: 0, dead: false });
    game.update(STEP, NO_INPUT);
    expect(game.player.alive).toBe(false);
    run(game, 10);
    expect(game.phase === 'gameOver' || game.phase === 'title').toBe(true);
    expect(saved.length).toBe(1);
  });

  it('advances to the next stage after the formation is wiped out', () => {
    const { game } = makeGame();
    startGame(game);
    freezeAttacks(game);
    run(game, 25);
    for (const e of game.enemies) e.dead = true;
    run(game, 0.1);
    expect(game.phase).toBe('stageClear');
    run(game, 1.5);
    expect(game.stage).toBe(2);
    expect(game.phase).toBe('stageIntro');
  });

  it('runs a challenging stage on stage 3 and pays the hit bonus', () => {
    const { game } = makeGame();
    startGame(game);
    game.startStage(3);
    run(game, 2.3);
    expect(game.challenge).toBe(true);
    expect(game.enemies).toHaveLength(40);
    for (let i = 0; i < 60 * 40 && game.phase === 'playing'; i++) game.update(STEP, NO_INPUT);
    expect(game.phase).toBe('challengeResult');
    expect(game.challengeBonus).toBe(game.challengeHits * 100);
  });

  it('survives a long unattended simulation across many seeds', () => {
    for (let seed = 1; seed <= 12; seed++) {
      const { game } = makeGame(seed);
      startGame(game);
      // Wiggle and fire constantly; the point is that nothing throws and state stays sane.
      run(game, 120, (t) => ({
        ...NO_INPUT,
        left: Math.sin(t * 0.7 + seed) < -0.3,
        right: Math.sin(t * 0.7 + seed) > 0.3,
        fire: true,
        firePressed: Math.floor(t * 8) % 2 === 0,
      }));
      expect(Number.isFinite(game.score)).toBe(true);
      expect(game.reserve).toBeGreaterThanOrEqual(0);
      for (const e of game.enemies) {
        expect(Number.isFinite(e.x) && Number.isFinite(e.y)).toBe(true);
      }
    }
  });
});
