import { Synth } from './audio';
import { Game } from './game';
import { Input } from './input';
import { Renderer } from './renderer';
import type { ScoreStore } from './types';

const STEP = 1 / 60;
const HIGH_SCORE_KEY = 'star-swarm.high-score';

const store: ScoreStore = {
  load() {
    try {
      return Number(localStorage.getItem(HIGH_SCORE_KEY)) || 0;
    } catch {
      return 0;
    }
  },
  save(score) {
    try {
      localStorage.setItem(HIGH_SCORE_KEY, String(score));
    } catch {
      // Storage can be unavailable (private mode, blocked site data); the score just won't persist.
    }
  },
};

async function start(): Promise<void> {
  const canvas = document.getElementById('screen');
  if (!(canvas instanceof HTMLCanvasElement)) throw new Error('Missing #screen canvas');

  try {
    await document.fonts.load('8px "Press Start 2P"');
  } catch {
    // Fall back to the monospace font.
  }

  const synth = new Synth();
  const game = new Game(Math.random, synth, store);
  const renderer = new Renderer(canvas);
  const input = new Input(canvas, () => synth.unlock());
  let paused = false;

  document.addEventListener('visibilitychange', () => {
    if (document.hidden && game.phase !== 'title') paused = true;
  });

  let last = performance.now();
  let acc = 0;
  const frame = (now: number): void => {
    const dt = Math.min((now - last) / 1000, 0.1);
    last = now;

    if (input.take('KeyP') || input.take('Escape')) paused = game.phase !== 'title' && !paused;
    if (input.take('KeyM')) synth.muted = !synth.muted;

    if (!paused) {
      acc += dt;
      while (acc >= STEP) {
        game.update(STEP, input.snapshot());
        input.endStep();
        acc -= STEP;
      }
    }
    renderer.draw(game, paused ? 0 : dt, { paused, muted: synth.muted });
    requestAnimationFrame(frame);
  };
  requestAnimationFrame(frame);
}

void start();
